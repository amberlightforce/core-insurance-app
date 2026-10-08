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

/// <summary><c>bil.Disbursement.request</c>: the shared disbursement service, source CLM_PAYMENT in the slice (REQ-BIL-009).</summary>
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
/// <item>source register (REQ-BIL-197, -354): only CLM_PAYMENT from CLM, method SEPA_CT, EUR (D-SL2-06);</item>
/// <item>approval evidence (REQ-BIL-198): the request content must hash to the approved <c>approvalContentHash</c>
/// (<see cref="DisbursementContent"/>), else BIL-ERR-APPROVAL-MISMATCH; BIL runs no approval of its own for CLM;</item>
/// <item>payee account (REQ-BIL-343..345): the payee's Active CLAIM_PAYMENT account, VoP Match (REQ-BIL-204), and a
/// changed account inside cooling-off is held (REQ-BIL-199; four-eyes release is not built, so it fails closed);</item>
/// <item>duplicates (REQ-BIL-202): one live disbursement per source object and per (payee account, amount, source ref),
/// enforced by unique indexes, so concurrent identical requests produce one disbursement;</item>
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
        if (request.SourceType != DisbursementCodes.ClaimPayment)
        {
            return DomainError.Of(ModuleCode.BIL, "SOURCE", $"Source type {request.SourceType} is not in the source register of this release (CLM_PAYMENT only).");
        }

        if (request.Method != DisbursementCodes.SepaCreditTransfer)
        {
            return DomainError.Of(ModuleCode.BIL, "METHOD-NOT-ALLOWED", $"Method {request.Method} is not allowed for {request.SourceType} (SEPA_CT only).");
        }

        if (request.Amount.Currency != Currency.EUR)
        {
            return DomainError.Of(ModuleCode.BIL, "CURRENCY", "Claim payments are paid in EUR (D-SL2-06).");
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

        // 3. Payee account (REQ-BIL-343..345, REQ-BIL-199, REQ-BIL-204).
        var account = await db.PayeeAccounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.PayeeAccountId == payeeAccountId && a.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return BillingErrors.NotFound("payee account");
        }

        if (account.PartyId != payeePartyId || account.Purpose != DisbursementCodes.ClaimPaymentPurpose
            || account.Status != Codes.Of(PayeeAccountStatus.Active) || account.ValidTo is not null)
        {
            return DomainError.Of(ModuleCode.BIL, "PAYEE-ACCOUNT", "The payee account is not the payee's Active CLAIM_PAYMENT account.");
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
        var existing = await ExistingAsync(legalEntity, request, cancellationToken).ConfigureAwait(false);
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
            SourceModule = ModuleCode.CLM.ToString(),
            SourceType = request.SourceType,
            SourceId = request.SourceId,
            ClaimId = request.ClaimId,
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
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_disbursement_source" or "ux_disbursement_duplicate_key" })
        {
            // A concurrent request for the same source won the race (it waited on the index until that one committed).
            db.Entry(row).State = EntityState.Detached;
            var winner = await ExistingAsync(legalEntity, request, cancellationToken).ConfigureAwait(false);
            return Duplicate(winner ?? default);
        }

        // 7. Release to the bound bank channel; the stub acknowledges and debits at once (D-SL2-05).
        if (services.GetService<IBankChannel>() is { } channel)
        {
            await ReleaseAsync(row, channel, request.Amount, today, now, cancellationToken).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogRequested(logger, row.DisbursementId.Value, row.SourceType, row.SourceId, row.State);
        return DisbursementReader.View(row, account.IbanLast4);
    }

    private async Task ReleaseAsync(DisbursementRow row, IBankChannel channel, Money amount, BusinessDate today, Instant now, CancellationToken cancellationToken)
    {
        var machine = DisbursementStateModel.Machine;
        var lineage = BusinessKeys.Empty.With("disbursementId", row.DisbursementId.Value.ToString()).With("sourceId", row.SourceId);
        lineage = row.ClaimId is { } claim ? lineage.With("claimId", claim.Value.ToString()) : lineage;
        var dimensions = new LineDimensions { DisbursementId = row.DisbursementId, SourceType = row.SourceType, SourceId = row.SourceId, ClaimId = row.ClaimId };
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
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(DisbursementIssuedV1.Descriptor), "Disbursement", row.DisbursementId.Value.ToString(),
            new DisbursementIssuedV1
            {
                DisbursementId = row.DisbursementId,
                SourceModule = ModuleCode.CLM,
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
                SourceModule = ModuleCode.CLM,
                SourceType = row.SourceType,
                SourceId = row.SourceId,
                Amount = amount,
                ValueDate = outcome.ValueDate,
                Method = row.Method,
            },
            keys) { OccurredAt = now });
    }

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
    /// The live disbursement of the same source object. It covers the duplicate key (payee account, amount, source ref)
    /// too, which the database enforces as its own unique index (REQ-BIL-202).
    /// </summary>
    private Task<DisbursementId?> ExistingAsync(LegalEntityId legalEntity, DisbursementRequestRequest request, CancellationToken cancellationToken) =>
        db.Disbursements.AsNoTracking()
            .Where(d => d.LegalEntityId == legalEntity && Live.Contains(d.State) && d.SourceType == request.SourceType && d.SourceId == request.SourceId)
            .Select(d => (DisbursementId?)d.DisbursementId)
            .FirstOrDefaultAsync(cancellationToken);

    private static DomainError Duplicate(DisbursementId existing) =>
        new(ErrorCode.For(ModuleCode.BIL, "DUPLICATE"), "A disbursement for this source (or with the same payee account, amount and source reference) already exists (REQ-BIL-202).")
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
