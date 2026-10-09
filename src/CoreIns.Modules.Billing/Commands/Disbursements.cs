using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Modules.Billing.Services;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CoreIns.Modules.Billing.Commands;

/// <summary><c>bil.Disbursement.request</c>: the shared disbursement service, sources CLM_CLAIM_PAYMENT and BIL_REFUND in the slice (REQ-BIL-009).</summary>
internal sealed record RequestDisbursement(DisbursementRequestRequest Request) : ICommand<DisbursementRequestResponse>;

/// <summary>Shape rules (BIL-ERR-VALIDATION). The register rules (source, method, currency) are typed errors in the handler.</summary>
internal sealed class RequestDisbursementValidator : AbstractValidator<RequestDisbursement>
{
    public RequestDisbursementValidator()
    {
        RuleFor(c => c.Request.SourceType).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Request.SourceId).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Request.Method).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Request.Amount).Must(a => a.IsPositive).WithErrorCode("AMOUNT").WithMessage("The amount must be positive.");
        RuleFor(c => c.Request.Amount).Must(a => a.IsRoundedToMinorUnits).WithErrorCode("AMOUNT-ROUNDING").WithMessage("The amount must be rounded to minor units.");
        RuleFor(c => c.Request.ApprovalEvidenceRef).MaximumLength(200);
        RuleFor(c => c.Request.PurposeText).MaximumLength(140);
        When(c => c.Request.SourceType == DisbursementCodes.ClaimPayment, () =>
        {
            RuleFor(c => c.Request.PayeePartyId).NotNull().WithErrorCode("PAYEE_REQUIRED");
            RuleFor(c => c.Request.PayeeAccountId).NotNull().WithErrorCode("PAYEE_ACCOUNT_REQUIRED");
            RuleFor(c => c.Request.ApprovalEvidenceRef).NotEmpty().WithErrorCode("APPROVAL_EVIDENCE_REQUIRED");
            RuleFor(c => c.Request.ApprovalContentHash).NotNull().WithErrorCode("APPROVAL_HASH_REQUIRED");
            RuleFor(c => c.Request.ClaimId).NotNull().WithErrorCode("CLAIM_REQUIRED");
            RuleFor(c => c.Request.AdHocPayee).Null().WithErrorCode("AD_HOC_PAYEE_NOT_SUPPORTED");
            RuleFor(c => c.Request.CoPayees).Must(p => p is null || p.Count == 0).WithErrorCode("CO_PAYEES_NOT_SUPPORTED");
            RuleFor(c => c.Request.CardToken).Null().WithErrorCode("CARD_NOT_SUPPORTED");
            RuleFor(c => c.Request.OffsetInstruction).Null().WithErrorCode("OFFSET_NOT_SUPPORTED");
        });
        When(c => c.Request.SourceType == DisbursementCodes.RefundPayment, () =>
        {
            RuleFor(c => c.Request.SourceId).Must(id => Guid.TryParseExact(id, "D", out var parsed) && parsed != Guid.Empty).WithErrorCode("REFUND_ID");
            RuleFor(c => c.Request.PayeePartyId).NotNull().WithErrorCode("PAYEE_REQUIRED");
            RuleFor(c => c.Request.PayeeAccountId).NotNull().WithErrorCode("PAYEE_ACCOUNT_REQUIRED");
            RuleFor(c => c.Request.ApprovalEvidenceRef).NotEmpty().WithErrorCode("APPROVAL_EVIDENCE_REQUIRED");
            RuleFor(c => c.Request.ApprovalContentHash).NotNull().WithErrorCode("APPROVAL_HASH_REQUIRED");
            RuleFor(c => c.Request.ClaimId).Null().WithErrorCode("CLAIM_NOT_ALLOWED");
            RuleFor(c => c.Request.AdHocPayee).Null().WithErrorCode("AD_HOC_PAYEE_NOT_SUPPORTED");
            RuleFor(c => c.Request.CoPayees).Must(p => p is null || p.Count == 0).WithErrorCode("CO_PAYEES_NOT_SUPPORTED");
            RuleFor(c => c.Request.CardToken).Null().WithErrorCode("CARD_NOT_SUPPORTED");
            RuleFor(c => c.Request.OffsetInstruction).Null().WithErrorCode("OFFSET_NOT_SUPPORTED");
        });
    }
}

/// <summary>
/// Requests, gates, releases and tracks one disbursement in a single transaction (REQ-BIL-009, -197..-211):
/// <list type="number">
/// <item>source register (REQ-BIL-197, -354): CLM_CLAIM_PAYMENT from CLM and BIL_REFUND from BIL itself (D-SL3-14), method SEPA_CT, EUR (D-SL2-06);</item>
/// <item>approval evidence (REQ-BIL-198): the request content must hash to the approved <c>approvalContentHash</c>
/// (<see cref="DisbursementContent"/>), else BIL-ERR-APPROVAL-MISMATCH; BIL runs no approval of its own for CLM. A BIL_REFUND
/// request is also cross-checked against the stored refund (APPROVED, same payee, account and amount) and its evidence must
/// be the refund's own: <c>BIL/RefundAuto/{id}</c> when approval was not required, else its PLT approval request;</item>
/// <item>payee account (REQ-BIL-343..345): the payee's Active CLAIM_PAYMENT account, VoP Match (REQ-BIL-204), and a
/// changed account inside cooling-off is held (REQ-BIL-199; four-eyes release is not built, so it fails closed);</item>
/// <item>duplicates (REQ-BIL-202): one live disbursement per source object (claim payment id) and per duplicate key
/// (payee account, amount, source reference = the claim), enforced by unique indexes, so concurrent identical requests
/// and two payment ids paying the same claim the same amount to the same account produce one disbursement;</item>
/// <item>sanctions (REQ-BIL-200, -214): <c>pty.Screening.screen</c> on the payee; anything but Clear without a payment
/// block — or a screening failure — refuses the request (fail closed; holds and <c>SanctionsHitCleared</c> are later);</item>
/// <item>Requested → PendingApproval → Approved, then, when a bank channel is bound, Released (DISBURSEMENT_RELEASED:
/// LA-17 → LA-13), Issued on the acknowledgement (<c>DisbursementIssued</c>) and Cleared on the statement debit
/// (DISBURSEMENT_CLEARED: LA-13 → LA-10, <c>DisbursementCleared</c>), REQ-BIL-206/207/211. Without a channel
/// (Production in the slice) the disbursement stays Approved.</item>
/// </list>
/// Rows, sealed ledger entries, <c>BillingEntryPosted</c> and the disbursement events commit together (REQ-BIL-332). No
/// IBAN or name is read, published or logged.
/// </summary>
internal sealed partial class RequestDisbursementHandler(
    BillingDbContext db,
    RequestContext context,
    IClock clock,
    INumberingService numbering,
    IEventPublisher events,
    LedgerWriter ledger,
    IServiceProvider services,
    ILogger<RequestDisbursementHandler> logger) : ICommandHandler<RequestDisbursement, DisbursementRequestResponse>
{
    private static readonly string[] Live =
    [
        Codes.Of(DisbursementState.Requested), Codes.Of(DisbursementState.PendingApproval), Codes.Of(DisbursementState.Approved),
        Codes.Of(DisbursementState.Released), Codes.Of(DisbursementState.Issued), Codes.Of(DisbursementState.Cleared),
    ];

    public async Task<Result<DisbursementRequestResponse>> HandleAsync(RequestDisbursement command, CancellationToken cancellationToken)
    {
        var request = command.Request;

        // 1. Source register (REQ-BIL-197, REQ-BIL-354) and method (REQ-BIL-209).
        var isRefund = request.SourceType == DisbursementCodes.RefundPayment;
        if (!isRefund && request.SourceType != DisbursementCodes.ClaimPayment)
        {
            return DomainError.Of(ModuleCode.BIL, "SOURCE", $"Source type {request.SourceType} is not in the source register of this release (CLM_CLAIM_PAYMENT, BIL_REFUND).");
        }

        if (request.Method != DisbursementCodes.SepaCreditTransfer)
        {
            return DomainError.Of(ModuleCode.BIL, "METHOD-NOT-ALLOWED", $"Method {request.Method} is not allowed for {request.SourceType} (SEPA_CT only).");
        }

        if (request.Amount.Currency != Currency.EUR)
        {
            return DomainError.Of(ModuleCode.BIL, "CURRENCY", "Payments are made in EUR (D-SL2-06).");
        }

        var payeePartyId = request.PayeePartyId!.Value;
        var payeeAccountId = request.PayeeAccountId!.Value;

        // 2. Approval evidence (REQ-BIL-198): the approved content must be the content BIL is asked to pay.
        var expected = DisbursementContent.Hash(request.SourceType, request.SourceId, payeePartyId, payeeAccountId, request.Amount);
        if (request.ApprovalContentHash != expected)
        {
            return DomainError.Of(ModuleCode.BIL, "APPROVAL-MISMATCH", "The request does not match the approved content hash; nothing is paid that was not approved.");
        }

        var legalEntity = ledger.LegalEntityId;
        var now = clock.Now;
        var today = ledger.Today;

        // 2a. A refund is paid only as BIL itself approved it (D-SL3-14): the stored refund is APPROVED for this very payee,
        // account and amount, and the evidence is its own (never a caller-chosen reference).
        RefundRow? refund = null;
        if (isRefund)
        {
            var mismatch = await CheckRefundAsync(legalEntity, request, payeePartyId, payeeAccountId, cancellationToken).ConfigureAwait(false);
            if (mismatch.Error is not null)
            {
                return mismatch.Error;
            }

            refund = mismatch.Refund;
        }

        // 2b. A PLT approval named as evidence must be Approved for exactly this payment and content (D-SL2-10 d, REQ-PLT-117).
        if (DisbursementApproval.TryParseApprovalRequest(request.ApprovalEvidenceRef, out var approvalRequestId))
        {
            var verified = await VerifyApprovalAsync(
                approvalRequestId, request.SourceId, expected, isRefund ? DisbursementApproval.RefundType : DisbursementApproval.ClaimPaymentType,
                isRefund ? DisbursementApproval.RefundSubject(request.SourceId) : DisbursementApproval.ClaimPaymentSubject(request.SourceId), cancellationToken)
                .ConfigureAwait(false);
            if (verified is not null)
            {
                return verified;
            }
        }

        // 3. Payee account (REQ-BIL-343..345, REQ-BIL-199, REQ-BIL-204).
        var account = await db.PayeeAccounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.PayeeAccountId == payeeAccountId && a.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return BillingErrors.NotFound("payee account");
        }

        var purpose = isRefund ? DisbursementCodes.RefundPurpose : DisbursementCodes.ClaimPaymentPurpose;
        if (account.PartyId != payeePartyId || account.Purpose != purpose
            || account.Status != Codes.Of(PayeeAccountStatus.Active) || account.ValidTo is not null)
        {
            return DomainError.Of(ModuleCode.BIL, "PAYEE-ACCOUNT", $"The payee account is not the payee's Active {purpose} account.");
        }

        if (Codes.Parse<PayeeVerification>(account.VerificationStatus) is not (PayeeVerification.VopMatched or PayeeVerification.Confirmed))
        {
            return DomainError.Of(ModuleCode.BIL, "VOP-HOLD", $"Verification of payee is {account.VerificationStatus}; the payment is held until the bank details are verified (REQ-BIL-204).");
        }

        if (account.IsChange && today < account.CoolingOffUntil)
        {
            return DomainError.Of(ModuleCode.BIL, "COOLING-OFF", $"The payee's bank account changed and is in cooling-off until {account.CoolingOffUntil}; a four-eyes release is required (REQ-BIL-199).");
        }

        // 4. Duplicates (REQ-BIL-202): a live disbursement for the same source object or duplicate key.
        var existing = await ExistingAsync(legalEntity, request, payeeAccountId, refund?.CreditSetKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Duplicate(existing.Value);
        }

        // 5. Sanctions screening of the payee (REQ-BIL-200), fail closed (REQ-BIL-214).
        var disbursementId = DisbursementId.New();
        var screening = await ScreenAsync(payeePartyId, disbursementId, cancellationToken).ConfigureAwait(false);
        if (screening.IsFailure)
        {
            return screening.Error!;
        }

        // 6. The disbursement: Requested → PendingApproval → Approved (the source's evidence is the approval).
        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.Disbursement, today), cancellationToken).ConfigureAwait(false);
        var machine = DisbursementStateModel.Machine;
        var state = machine.FireOrThrow(machine.FireOrThrow(machine.Start(DisbursementState.Requested).Value, DisbursementTrigger.EnterApproval), DisbursementTrigger.Approve);
        var row = new DisbursementRow
        {
            DisbursementId = disbursementId,
            LegalEntityId = legalEntity,
            Jurisdiction = ledger.Jurisdiction.Value,
            DisbursementNumber = number.Value,
            SourceModule = (isRefund ? ModuleCode.BIL : ModuleCode.CLM).ToString(),
            SourceType = request.SourceType,
            SourceId = request.SourceId,
            ClaimId = isRefund ? null : request.ClaimId,
            BusinessRef = refund?.CreditSetKey,
            PayeePartyId = payeePartyId,
            PayeeAccountId = payeeAccountId,
            Amount = request.Amount.Amount,
            Currency = request.Amount.Currency.Code,
            Method = request.Method,
            RequestedValueDate = request.RequestedValueDate,
            ApprovalEvidenceRef = request.ApprovalEvidenceRef!,
            ApprovalContentHash = expected.Value,
            PurposeText = request.PurposeText,
            State = Codes.Of(state),
            ScreeningResult = ScreeningCodes.Clear,
            ScreeningListVersions = screening.Value,
            ScreenedAt = now,
            VopResult = account.VopResult ?? Codes.Of(VopOutcome.NotAvailable),
            RequestedAt = now,
            ApprovedAt = now,
            CreatedBy = context.Actor.ToString(),
            RecordVersion = 1,
        };
        db.Disbursements.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_disbursement_source" or "ux_disbursement_duplicate_key" or "ux_disbursement_business_ref" })
        {
            // A concurrent request for the same source won the race (it waited on the index until that one committed).
            db.Entry(row).State = EntityState.Detached;
            var winner = await ExistingAsync(legalEntity, request, payeeAccountId, refund?.CreditSetKey, cancellationToken).ConfigureAwait(false);
            return Duplicate(winner ?? default);
        }

        // 7. Release to the bound bank channel; the stub acknowledges and debits at once (D-SL2-05).
        if (services.GetService<IBankChannel>() is { } channel)
        {
            await ReleaseAsync(row, channel, request.Amount, today, now, refund, cancellationToken).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogRequested(logger, row.DisbursementId.Value, row.SourceType, row.SourceId, row.State);
        return DisbursementReader.View(row, account.IbanLast4);
    }

    private async Task ReleaseAsync(
        DisbursementRow row, IBankChannel channel, Money amount, BusinessDate today, Instant now, RefundRow? refund, CancellationToken cancellationToken)
    {
        var machine = DisbursementStateModel.Machine;
        var sourceModule = Enum.Parse<ModuleCode>(row.SourceModule);
        var lineage = BusinessKeys.Empty.With("disbursementId", row.DisbursementId.Value.ToString()).With("sourceId", row.SourceId);
        lineage = row.ClaimId is { } claim ? lineage.With("claimId", claim.Value.ToString()) : lineage;
        lineage = refund is null ? lineage : lineage.With("refundId", refund.RefundId.Value.ToString()).With("billingAccountId", refund.BillingAccountId.Value.ToString());

        // A refund's lines carry its account and the REFUND transaction kind (servicing entry, FIN rule set v3).
        var dimensions = new LineDimensions
        {
            DisbursementId = row.DisbursementId,
            SourceType = row.SourceType,
            SourceId = row.SourceId,
            ClaimId = row.ClaimId,
            BillingAccountId = refund?.BillingAccountId,
            TransactionKind = refund is null ? null : TransactionKinds.Refund,
        };
        var valueDate = row.RequestedValueDate is { } requested && requested > today ? requested : today;

        // Released: the source's clearing account to disbursements in transit (REQ-BIL-211).
        var released = await ledger.RuleAsync(ledger.Key(EntryTypes.DisbursementReleased, RuleQualifiers.Any, row.SourceType), cancellationToken).ConfigureAwait(false);
        var rule = released.IsSuccess ? released.Value : throw new DomainException(released.Error!);
        row.State = Codes.Of(machine.FireOrThrow(Codes.Parse<DisbursementState>(row.State), DisbursementTrigger.Release));
        row.ReleasedAt = now;
        row.ValueDate = valueDate;
        row.ReleaseEntryId = ledger.Post(new EntrySpec(
            EntryTypes.DisbursementReleased, null, [new PostingLeg(rule, amount, dimensions)], lineage, "bil.Disbursement.release", row.DisbursementId));

        var outcome = await channel.SubmitAsync(
            new BankInstruction(row.DisbursementId, row.DisbursementNumber, amount, valueDate, row.Method, row.PayeeAccountId), cancellationToken).ConfigureAwait(false);
        if (!outcome.Accepted)
        {
            return; // Awaiting the bank's acknowledgement (asynchronous channels are a later package).
        }

        // Issued on the bank's acknowledgement (REQ-BIL-207): DisbursementIssued.
        row.State = Codes.Of(machine.FireOrThrow(DisbursementState.Released, DisbursementTrigger.Issue));
        row.IssuedAt = now;
        row.BankReference = outcome.BankReference;
        row.ValueDate = outcome.ValueDate;
        var keys = BusinessKeys.Empty.With("disbursementId", row.DisbursementId.Value.ToString());
        keys = row.ClaimId is { } claimId ? keys.With("claimId", claimId.Value.ToString()) : keys;
        keys = refund is null ? keys : keys.With("refundId", refund.RefundId.Value.ToString()).With("billingAccountId", refund.BillingAccountId.Value.ToString());
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(DisbursementIssuedV1.Descriptor), "Disbursement", row.DisbursementId.Value.ToString(),
            new DisbursementIssuedV1
            {
                DisbursementId = row.DisbursementId,
                SourceModule = sourceModule,
                SourceType = row.SourceType,
                SourceId = row.SourceId,
                Amount = amount,
                ValueDate = outcome.ValueDate,
                Method = row.Method,
            },
            keys) { OccurredAt = now });

        if (!outcome.Debited)
        {
            return;
        }

        // Cleared on the statement debit (REQ-BIL-207): disbursements in transit to cash at bank, DisbursementCleared.
        var cleared = await ledger.RuleAsync(ledger.Key(EntryTypes.DisbursementCleared, RuleQualifiers.Any, row.SourceType), cancellationToken).ConfigureAwait(false);
        var clearRule = cleared.IsSuccess ? cleared.Value : throw new DomainException(cleared.Error!);
        row.State = Codes.Of(machine.FireOrThrow(DisbursementState.Issued, DisbursementTrigger.Clear));
        row.ClearedAt = now;
        row.ClearEntryId = ledger.Post(new EntrySpec(
            EntryTypes.DisbursementCleared, null, [new PostingLeg(clearRule, amount, dimensions)], lineage, "bil.Disbursement.clear", row.DisbursementId));
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(DisbursementClearedV1.Descriptor), "Disbursement", row.DisbursementId.Value.ToString(),
            new DisbursementClearedV1
            {
                DisbursementId = row.DisbursementId,
                SourceModule = sourceModule,
                SourceType = row.SourceType,
                SourceId = row.SourceId,
                Amount = amount,
                ValueDate = outcome.ValueDate,
                Method = row.Method,
            },
            keys) { OccurredAt = now });
    }

    /// <summary>
    /// <c>plt.Approval.verifyForExecution</c> on the named request: the source's approval type and subject (CLM.CLAIM_PAYMENT on
    /// CLM/ClaimPayment/{id}, BIL.REFUND on BIL/Refund/{id}) and the recomputed content hash. Null when the approval covers this
    /// payment; otherwise BIL-ERR-APPROVAL-MISMATCH (fail closed).
    /// </summary>
    private async Task<DomainError?> VerifyApprovalAsync(Guid requestId, string sourceId, Sha256Hash hash, string type, ObjectRef subject, CancellationToken cancellationToken)
    {
        if (services.GetService<IPlatformApprovalService>() is not { } approvals)
        {
            return DomainError.Of(ModuleCode.BIL, "APPROVAL-MISMATCH", "The approval service is not available; the approval cannot be verified.");
        }

        try
        {
            var verified = await approvals.VerifyForExecutionAsync(
                new ApprovalVerifyForExecutionRequest { RequestId = requestId, Hash = hash, Type = type, ObjectRef = subject },
                cancellationToken).ConfigureAwait(false);
            return verified.Ok
                ? null
                : DomainError.Of(ModuleCode.BIL, "APPROVAL-MISMATCH", $"The approval request is {verified.Status}, not Approved; nothing is paid that was not approved.");
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            LogApprovalRefused(logger, ex.Error.Code.Value);
            return DomainError.Of(ModuleCode.BIL, "APPROVAL-MISMATCH", $"The approval does not cover this payment ({ex.Error.Code}).");
        }
    }

    /// <summary>
    /// A BIL_REFUND request must describe the stored refund exactly: it is APPROVED (by rule or by a second person), pays the
    /// same payee, account and amount, and names the refund's own evidence. Anything else is BIL-ERR-APPROVAL-MISMATCH.
    /// </summary>
    private async Task<(RefundRow? Refund, DomainError? Error)> CheckRefundAsync(
        LegalEntityId legalEntity, DisbursementRequestRequest request, PartyId payeePartyId, Guid payeeAccountId, CancellationToken cancellationToken)
    {
        static (RefundRow?, DomainError?) Mismatch(string detail) => (null, DomainError.Of(ModuleCode.BIL, "APPROVAL-MISMATCH", detail));
        var refundId = new RefundId(Guid.ParseExact(request.SourceId, "D"));
        var refund = await db.Refunds.AsNoTracking().SingleOrDefaultAsync(r => r.RefundId == refundId && r.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (refund is null)
        {
            return (null, BillingErrors.NotFound("refund"));
        }

        if (refund.State != Codes.Of(Contracts.RefundState.Approved)
            || refund.ApprovalState is not ("APPROVED" or "NOT_REQUIRED")
            || refund.PayeePartyId != payeePartyId || refund.PayeeAccountId != payeeAccountId
            || refund.Amount != request.Amount.Amount || refund.Currency != request.Amount.Currency.Code)
        {
            return Mismatch("The request does not describe an approved refund of this payee, account and amount; nothing is paid that was not approved.");
        }

        var evidence = refund.ApprovalState == "NOT_REQUIRED"
            ? DisbursementApproval.RefundAutoEvidencePrefix + request.SourceId
            : refund.ApprovalRequestId is { } approval ? "PLT/ApprovalRequest/" + approval.ToString("D") : null;
        return string.Equals(request.ApprovalEvidenceRef, evidence, StringComparison.Ordinal)
            ? (refund, null)
            : Mismatch("The approval evidence is not the refund's own.");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "SECURITY: disbursement approval verification refused ({Code}).")]
    private static partial void LogApprovalRefused(ILogger logger, string code);

    /// <summary>Screens the payee; the list versions on Clear, else the fail-closed error.</summary>
    private async Task<Result<string>> ScreenAsync(PartyId payee, DisbursementId disbursementId, CancellationToken cancellationToken)
    {
        if (services.GetService<IPartyScreeningService>() is not { } screening)
        {
            return DomainError.Of(ModuleCode.BIL, "SCREENING-UNAVAILABLE", "Sanctions screening is not available; the payment is not made (REQ-BIL-214).");
        }

        ScreeningScreenResponse response;
        try
        {
            response = await screening.ScreenAsync(
                new ScreeningScreenRequest { PartyId = payee, CallerRef = ObjectRef.For(ModuleCode.BIL, "Disbursement", disbursementId) },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogScreeningFailed(logger, ex.GetType().Name);
            return DomainError.Of(ModuleCode.BIL, "SCREENING-UNAVAILABLE", "Sanctions screening failed; the payment is not made (REQ-BIL-214).");
        }

        if (response.Result != ScreeningScreenResponse.ResultValue.Clear || response.PaymentBlock)
        {
            return DomainError.Of(ModuleCode.BIL, "PAYEE-BLOCKED", $"Sanctions screening of the payee returned {response.Result}{(response.PaymentBlock ? " with a payment block" : string.Empty)}; the payment is not made (REQ-BIL-200).");
        }

        return string.Join(",", response.ListVersions);
    }

    /// <summary>
    /// The live disbursement with the same source object (claim payment id or refund id) or the same duplicate key: payee
    /// account, amount and source reference — the claim for CLM, the credit set for a refund (REQ-BIL-202). The database
    /// enforces all three as unique indexes. A refund is never matched on a null claim.
    /// </summary>
    private Task<DisbursementId?> ExistingAsync(LegalEntityId legalEntity, DisbursementRequestRequest request, Guid payeeAccountId, string? businessRef, CancellationToken cancellationToken)
    {
        var amount = request.Amount.Amount;
        var currency = request.Amount.Currency.Code;
        var claim = request.ClaimId;
        var query = db.Disbursements.AsNoTracking().Where(d => d.LegalEntityId == legalEntity && Live.Contains(d.State) && d.SourceType == request.SourceType);
        query = businessRef is not null
            ? query.Where(d => d.SourceId == request.SourceId
                              || (d.BusinessRef == businessRef && d.PayeeAccountId == payeeAccountId && d.Amount == amount && d.Currency == currency))
            : query.Where(d => d.SourceId == request.SourceId
                              || (claim != null && d.ClaimId == claim && d.PayeeAccountId == payeeAccountId && d.Amount == amount && d.Currency == currency));
        return query.Select(d => (DisbursementId?)d.DisbursementId).FirstOrDefaultAsync(cancellationToken);
    }

    private static DomainError Duplicate(DisbursementId existing) =>
        new(ErrorCode.For(ModuleCode.BIL, "DUPLICATE"), "A disbursement for this source, or for the same claim or credit with the same payee account and amount, already exists (REQ-BIL-202).")
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["existingDisbursementId"] = existing.Value.ToString() },
        };

    [LoggerMessage(Level = LogLevel.Information, Message = "Disbursement {DisbursementId} for {SourceType} {SourceId} is {State}.")]
    private static partial void LogRequested(ILogger logger, Guid disbursementId, string sourceType, string sourceId, string state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sanctions screening failed ({ExceptionType}); the disbursement request is refused (fail closed).")]
    private static partial void LogScreeningFailed(ILogger logger, string exceptionType);
}

/// <summary>Audit facts: the disbursement, its number, source and amount (no IBAN, no name).</summary>
internal sealed class RequestDisbursementAuditor : ICommandAuditor<RequestDisbursement, DisbursementRequestResponse>
{
    public CommandAuditFacts Describe(RequestDisbursement command, Result<DisbursementRequestResponse>? result)
    {
        var keys = BusinessKeys.Empty.With("sourceId", command.Request.SourceId);
        keys = command.Request.ClaimId is { } claim ? keys.With("claimId", claim.Value.ToString()) : keys;
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = new ObjectRef(ModuleCode.BIL, "Disbursement", "request"), BusinessKeys = keys };
        }

        var value = ok.Value;
        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.BIL, "Disbursement", value.DisbursementId),
            ObjectNumber = value.DisbursementNumber?.Value,
            BusinessKeys = keys.With("disbursementId", value.DisbursementId.Value.ToString()),
            Changes = AuditDiff.Compute(null, new
            {
                sourceType = value.SourceType,
                amount = value.Amount.ToString(),
                status = value.Status.ToString(),
                payeeAccount = value.MaskedPayeeAccount,
                approvalEvidenceRef = command.Request.ApprovalEvidenceRef,
            }),
        };
    }
}
