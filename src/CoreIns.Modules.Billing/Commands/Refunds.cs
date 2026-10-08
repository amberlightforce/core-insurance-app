using System.Text.RegularExpressions;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Modules.Billing.Services;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using AuthorityCheckRequest = CoreIns.Platform.Authority.AuthorityCheckRequest;
using StoredRefundState = CoreIns.Modules.Billing.Contracts.RefundState;

namespace CoreIns.Modules.Billing.Commands;

/// <summary><c>bil.Refund.propose</c> (REQ-BIL-007, REQ-BIL-181…-188): proposes a refund of the credit left on a billing account.</summary>
internal sealed record ProposeRefund(RefundProposeRequest Request) : ICommand<RefundProposeResponse>;

/// <summary><c>bil.Refund.decide</c> (REQ-BIL-188, -189, -191): approve or reject a refund that waits for a second person.</summary>
internal sealed record DecideRefund(RefundDecideRequest Request) : ICommand<RefundDecideResponse>;

/// <summary><c>bil.Refund.resubmit</c> (REQ-BIL-191): after a rejection the requester resubmits with changes, as a new refund.</summary>
internal sealed record ResubmitRefund(RefundResubmitRequest Request) : ICommand<RefundResubmitResponse>;

internal static partial class RefundRules
{
    /// <summary>Reason codes are short upper-case codes (they travel as an authority dimension and in events).</summary>
    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,63}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    public static partial Regex ReasonCodePattern();
}

internal sealed class ProposeRefundValidator : AbstractValidator<ProposeRefund>
{
    public ProposeRefundValidator()
    {
        RuleFor(c => c.Request.ReasonCode).NotEmpty().Must(c => RefundRules.ReasonCodePattern().IsMatch(c ?? string.Empty)).WithErrorCode("REASON_CODE")
            .WithMessage("The reason is a code of capital letters, digits and underscores.");
        RuleFor(c => c.Request.Comment).MaximumLength(500);
        RuleFor(c => c.Request.Credits).Must(c => c is null || c.Count <= 200).WithErrorCode("CREDITS");
    }
}

internal sealed class DecideRefundValidator : AbstractValidator<DecideRefund>
{
    public DecideRefundValidator()
    {
        RuleFor(c => c.Request.Comment).MaximumLength(500);
        RuleFor(c => c.Request.Comment).NotEmpty().WithErrorCode("COMMENT_REQUIRED").WithMessage("A rejection needs a comment (REQ-BIL-191).")
            .When(c => c.Request.Decision == RefundDecideRequest.DecisionValue.Reject);
    }
}

internal sealed class ResubmitRefundValidator : AbstractValidator<ResubmitRefund>
{
    public ResubmitRefundValidator()
    {
        RuleFor(c => c.Request.Comment).MaximumLength(500);
    }
}

/// <summary>
/// The refund workflow shared by propose, resubmit and decide (D-SL3-09, D-SL3-14; REQ-BIL-181…-191):
/// <list type="number">
/// <item><b>Credit and netting</b> (REQ-BIL-182): the credit left on the account's credit notes (item − Σ applications) is first set
/// against open debit items, same term first; only the rest is refunded, if it reaches the minimum (illustrative 5.00).
/// Nothing moves at proposal: the plan is stored as append-only lines and applied when the refund is approved.</item>
/// <item><b>Payee</b> (REQ-BIL-187, -186): the billing account's payer, paid to the payer's Active, verified (VoP Match/Confirmed)
/// REFUND account only. <c>payeeChanged</c> is computed by BIL: the account replaced an earlier account of the party.</item>
/// <item><b>Authority</b> (REQ-BIL-188, PITFALLS 1/4): <c>plt.Authority.check</c> of the requester on <c>BIL.REFUND</c> over the refund
/// <i>total</i>, with one open refund per account (unique index) so splitting cannot escape the limit. A refund within the auto limit,
/// to an unchanged payee, not a resubmission, and Allowed for the requester is approved by rule. Otherwise a PLT approval is
/// requested by BIL (type BIL.REFUND, subject BIL/Refund/{id}, hash = the disbursement content hash) with the dimensions amount,
/// currency, payeeChanged and reason, which the checker is re-checked on at decide time.</item>
/// <item><b>Segregation of duties</b> (REQ-BIL-189, PITFALLS 5): PLT refuses the maker and the maker's principal; BIL refuses every
/// other participant (earlier requesters and resubmitters of the chain) and the person who changed the payee account; the database
/// refuses an approval by any of them as well (trigger).</item>
/// <item><b>A rejection is final</b> (PITFALLS 6): a resubmission is a new refund pointing back to the rejected one, and is always
/// approved by a second person, never by rule.</item>
/// <item><b>Approval</b> (one transaction): credit applications for the netting and the refund lines (the database accepts them only
/// for an approved refund), <c>REFUND_APPROVED</c> LA-02 → LA-12 (sealed), <c>RefundApproved</c>, then the disbursement through the
/// shared service (source BIL_REFUND; screening and VoP fail closed). <c>RefundDisbursed</c> is published when the disbursement is
/// Issued (D-SL3-09, REQ-BIL-190).</item>
/// </list>
/// </summary>
internal sealed partial class RefundWorkflow(
    BillingDbContext db,
    RequestContext context,
    IClock clock,
    LedgerWriter ledger,
    RefundCredits credits,
    Allocator allocator,
    IAuthorityService authority,
    IServiceProvider services,
    IEventPublisher events,
    RefundReader reader,
    IOptions<BillingOptions> options,
    ILogger<RefundWorkflow> logger)
{
    private static readonly string[] Closed = [Codes.Of(StoredRefundState.Rejected), Codes.Of(StoredRefundState.Paid), Codes.Of(StoredRefundState.Returned)];

    /// <summary>The actor and, for a delegated actor, the person it acts for (PITFALLS 5).</summary>
    public string[] ActorKeys() => context.OnBehalfOf is { } principal ? [context.Actor.ToString(), principal.ToString()] : [context.Actor.ToString()];

    public async Task LockAsync(Guid id, CancellationToken cancellationToken)
    {
        var key = BitConverter.ToInt64(id.ToByteArray(), 8) ^ 0x5245_4655_4E44L;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Proposes (or resubmits) a refund of the account's credit; approves and pays it at once when it is approved by rule.</summary>
    public async Task<Result<RefundView>> CreateAsync(
        BillingAccountRow account, IReadOnlyList<Guid>? creditNotes, Guid? payeeAccountId, string reasonCode, string? comment,
        RefundRow? resubmits, CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var legalEntity = ledger.LegalEntityId;
        var now = clock.Now;
        await LockAsync(account.BillingAccountId.Value, cancellationToken).ConfigureAwait(false);

        if (await db.Refunds.AsNoTracking().Where(r => r.BillingAccountId == account.BillingAccountId && !Closed.Contains(r.State))
                .Select(r => (RefundId?)r.RefundId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) is { } open)
        {
            return OpenError(open);
        }

        if (account.Currency != Currency.EUR.Code)
        {
            return DomainError.Of(ModuleCode.BIL, "CURRENCY", "Refunds are paid in EUR (D-SL2-06).");
        }

        // 1. Credit, netting (REQ-BIL-182) and the minimum.
        var sources = await credits.RemainingAsync(legalEntity, account.BillingAccountId, creditNotes, cancellationToken).ConfigureAwait(false);
        var debits = await credits.OpenDebitsAsync(legalEntity, account.BillingAccountId, cancellationToken).ConfigureAwait(false);
        var plan = RefundPlanner.Plan(sources, debits);
        if (plan.Lines.Count == 0)
        {
            return DomainError.Of(ModuleCode.BIL, "NO-CREDIT", sources.Count == 0
                ? "The billing account has no credit to refund."
                : "The credit is fully set against open invoices of the account; nothing is left to refund (REQ-BIL-182).");
        }

        var total = new Money(plan.Total, Currency.FromCode(account.Currency));
        if (plan.Total < opts.RefundMinimum)
        {
            return DomainError.Of(ModuleCode.BIL, "REFUND-BELOW-MINIMUM", $"The refund {total} is below the minimum refund {opts.RefundMinimum:0.00} (illustrative); the credit stays on the account.");
        }

        // 2. The payee: the account's payer, to a verified REFUND account (REQ-BIL-186, -187, -343…-345).
        var payee = await FindPayeeAccountAsync(legalEntity, account, payeeAccountId, cancellationToken).ConfigureAwait(false);
        if (payee.Error is not null)
        {
            return payee.Error;
        }

        var payeeAccount = payee.Account!;
        var payeeChanged = payeeAccount.IsChange;

        // 3. Authority of the requester on the refund total (REQ-BIL-188; the dimensions are computed here, never taken from the client).
        var check = await authority.CheckAsync(
            new AuthorityCheckRequest(
                context.Actor, context.Roles, SupportAuthorityTypes.Refund, RefundAuthority.Dimensions(total, payeeChanged, reasonCode),
                ObjectRef.For(ModuleCode.BIL, "BillingAccount", account.BillingAccountId), now),
            cancellationToken).ConfigureAwait(false);
        context.AuthorityChecks.Add(check);
        if (check.Decision == AuthorityDecision.Deny)
        {
            return DomainError.Of(ModuleCode.BIL, "NOT-PERMITTED", $"No authority for a refund of {total} (BIL.REFUND: {check.ReasonCode}).") with
            {
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["authorityCheckId"] = check.CheckId.ToString(), ["reasonCode"] = check.ReasonCode },
            };
        }

        var byRule = check.Decision == AuthorityDecision.Allow && plan.Total <= opts.RefundAutoApproveLimit && !payeeChanged && resubmits is null;

        // 4. The rows.
        var refundId = RefundId.New();
        var sourceId = refundId.Value.ToString("D");
        var hash = DisbursementContent.Hash(DisbursementCodes.RefundPayment, sourceId, account.PayerPartyId, payeeAccount.PayeeAccountId, total);
        var machine = RefundStateModel.Machine;
        var state = machine.FireOrThrow(machine.Start(StoredRefundState.Proposed).Value, RefundTrigger.Submit);
        if (byRule)
        {
            state = machine.FireOrThrow(state, RefundTrigger.Approve);
        }

        var participants = (resubmits?.Participants ?? []).Concat(ActorKeys()).Distinct(StringComparer.Ordinal).ToArray();
        var refund = new RefundRow
        {
            RefundId = refundId,
            LegalEntityId = legalEntity,
            Jurisdiction = ledger.Jurisdiction.Value,
            BillingAccountId = account.BillingAccountId,
            State = Codes.Of(state),
            ApprovalState = Codes.Of(byRule ? RefundApprovalStateCode.NotRequired : RefundApprovalStateCode.Pending),
            Amount = total.Amount,
            Currency = total.Currency.Code,
            PayeePartyId = account.PayerPartyId,
            PayeeAccountId = payeeAccount.PayeeAccountId,
            PayeeChanged = payeeChanged,
            PayeeAccountChangedBy = payeeAccount.CreatedBy,
            PayoutMethod = DisbursementCodes.SepaCreditTransfer,
            ReasonCode = reasonCode,
            Comment = comment,
            SelectedCreditNotes = creditNotes is { Count: > 0 } ? [.. creditNotes.Distinct()] : null,
            CreditSetKey = RefundContent.CreditSetKey(plan.Lines),
            ResubmitsRefundId = resubmits?.RefundId,
            Participants = participants,
            RequestedBy = context.Actor.ToString(),
            ApprovalContentHash = hash.Value,
            ProposedAt = now,
            RecordVersion = 1,
        };
        db.Refunds.Add(refund);
        foreach (var line in plan.Lines)
        {
            db.RefundCredits.Add(new RefundCreditRow
            {
                RefundCreditId = Guid.CreateVersion7(),
                RefundId = refundId,
                LegalEntityId = legalEntity,
                CreditNoteId = line.Credit.CreditNoteId,
                CreditItemId = line.Credit.CreditItemId,
                PolicyId = line.Credit.PolicyId,
                TermId = line.Credit.TermId,
                TransactionId = line.Credit.TransactionId,
                ChargeType = line.Credit.ChargeType,
                ChargeCategory = line.Credit.ChargeCategory,
                Amount = line.Amount,
                Currency = total.Currency.Code,
            });
        }

        foreach (var pair in plan.Netting)
        {
            db.RefundNettings.Add(new RefundNettingRow
            {
                RefundNettingId = Guid.CreateVersion7(),
                RefundId = refundId,
                LegalEntityId = legalEntity,
                CreditNoteId = pair.Credit.CreditNoteId,
                CreditItemId = pair.Credit.CreditItemId,
                TargetInvoiceId = pair.Target.InvoiceId,
                TargetInvoiceItemId = pair.Target.InvoiceItemId,
                Amount = pair.Amount,
                Currency = total.Currency.Code,
            });
        }

        var saved = await TrySaveAsync(cancellationToken).ConfigureAwait(false);
        if (saved is not null)
        {
            return saved;
        }

        if (!byRule)
        {
            // A refund above the auto limit, to a changed payee, after a rejection or beyond the requester's own authority waits for a
            // second person with BIL.REFUND authority over the same total (REQ-BIL-188). A dry run creates no request.
            if (!context.DryRun)
            {
                var approvals = services.GetService<IPlatformApprovalService>();
                if (approvals is null)
                {
                    return DomainError.Of(ModuleCode.BIL, "APPROVAL-MISMATCH", "The approval service is not available; the refund cannot be sent for approval.");
                }

                try
                {
                    var response = await approvals.RequestAsync(
                        new ApprovalRequestRequest
                        {
                            Type = DisbursementApproval.RefundType,
                            ObjectRef = DisbursementApproval.RefundSubject(sourceId),
                            PayloadHash = hash,
                            Authority = new ApprovalAuthority
                            {
                                Type = SupportAuthorityTypes.Refund.Value,
                                Amount = total,
                                Codes = RefundAuthority.ApprovalCodes(total, payeeChanged, reasonCode),
                            },
                            ReferralRole = opts.RefundApproverRole,
                            Reason = $"Refund {total} on billing account {account.AccountNumber}: {(payeeChanged ? "payee account changed; " : string.Empty)}"
                                     + $"{(resubmits is not null ? "resubmitted after a rejection; " : string.Empty)}requester check {check.Decision} ({check.ReasonCode}).",
                        },
                        CommandOptions.New(),
                        cancellationToken).ConfigureAwait(false);
                    refund.ApprovalRequestId = response.Request.RequestId;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DomainException ex)
                {
                    return ex.Error;
                }
            }

            LogProposed(logger, refundId.Value, total.Amount, refund.State, payeeChanged);
            return (await reader.ViewsAsync([refund], cancellationToken).ConfigureAwait(false))[0];
        }

        var approved = await ApproveAndPayAsync(refund, decidedBy: null, cancellationToken).ConfigureAwait(false);
        if (approved.IsFailure)
        {
            return approved.Error!;
        }

        return (await reader.ViewsAsync([refund], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// Applies an approved refund: credit applications (netting, then the refund lines), item states, <c>REFUND_APPROVED</c>,
    /// <c>RefundApproved</c>, the disbursement and, once it is Issued, <c>RefundDisbursed</c> (D-SL3-09). The row must already be
    /// Approved when its applications are inserted (the database refuses them otherwise), so the state is saved first.
    /// </summary>
    public async Task<Result<bool>> ApproveAndPayAsync(RefundRow refund, string? decidedBy, CancellationToken cancellationToken)
    {
        var now = clock.Now;
        var machine = RefundStateModel.Machine;
        var currency = Currency.FromCode(refund.Currency);
        var lines = await db.RefundCredits.Where(c => c.RefundId == refund.RefundId).OrderBy(c => c.RefundCreditId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var netting = await db.RefundNettings.Where(n => n.RefundId == refund.RefundId).OrderBy(n => n.RefundNettingId).ToListAsync(cancellationToken).ConfigureAwait(false);

        // The plan was computed when the refund was proposed; the credit and the open debit must still be there (fail closed, REQ-BIL-130).
        var current = await credits.RemainingAsync(ledger.LegalEntityId, refund.BillingAccountId, null, cancellationToken).ConfigureAwait(false);
        var remaining = current.ToDictionary(c => c.CreditItemId, c => c.Remaining);
        var needed = lines.Select(l => (l.CreditItemId, l.Amount)).Concat(netting.Select(n => (n.CreditItemId, n.Amount)))
            .GroupBy(x => x.CreditItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var debitsNow = (await credits.OpenDebitsAsync(ledger.LegalEntityId, refund.BillingAccountId, cancellationToken).ConfigureAwait(false)).ToDictionary(d => d.InvoiceItemId, d => d.Open);
        if (needed.Any(n => remaining.GetValueOrDefault(n.Key) < n.Value)
            || netting.GroupBy(n => n.TargetInvoiceItemId).Any(g => debitsNow.GetValueOrDefault(g.Key) < g.Sum(n => n.Amount)))
        {
            return Stale("The credit or the open invoices of the account changed after the refund was proposed; propose it again.");
        }

        if (refund.State != Codes.Of(StoredRefundState.Approved))
        {
            refund.State = Codes.Of(machine.FireOrThrow(Codes.Parse<StoredRefundState>(refund.State), RefundTrigger.Approve));
        }

        if (decidedBy is not null)
        {
            refund.ApprovalState = Codes.Of(RefundApprovalStateCode.Approved);
            refund.DecidedBy = decidedBy;
            refund.DecidedAt = now;
        }

        refund.RecordVersion++;
        var saved = await TrySaveAsync(cancellationToken).ConfigureAwait(false);
        if (saved is not null)
        {
            return saved;
        }

        var actor = context.Actor.ToString();
        foreach (var n in netting)
        {
            db.CreditApplications.Add(new CreditApplicationRow
            {
                CreditApplicationId = Guid.CreateVersion7(),
                LegalEntityId = refund.LegalEntityId,
                BillingAccountId = refund.BillingAccountId,
                CreditNoteId = n.CreditNoteId,
                CreditItemId = n.CreditItemId,
                TargetKind = CreditApplicationTargets.Netting,
                TargetInvoiceId = n.TargetInvoiceId,
                TargetInvoiceItemId = n.TargetInvoiceItemId,
                RefundId = refund.RefundId,
                Amount = n.Amount,
                Currency = n.Currency,
                Actor = actor,
                RecordedAt = now,
            });
        }

        foreach (var l in lines)
        {
            db.CreditApplications.Add(new CreditApplicationRow
            {
                CreditApplicationId = Guid.CreateVersion7(),
                LegalEntityId = refund.LegalEntityId,
                BillingAccountId = refund.BillingAccountId,
                CreditNoteId = l.CreditNoteId,
                CreditItemId = l.CreditItemId,
                TargetKind = CreditApplicationTargets.Refund,
                RefundId = refund.RefundId,
                Amount = l.Amount,
                Currency = l.Currency,
                Actor = actor,
                RecordedAt = now,
            });
        }

        saved = await TrySaveAsync(cancellationToken).ConfigureAwait(false);
        if (saved is not null)
        {
            return saved;
        }

        await SettleItemsAsync(netting, lines, now, cancellationToken).ConfigureAwait(false);

        // REFUND_APPROVED (LA-02 → LA-12), one leg per refund line; FIN nets the refunds payable per refund id (rule set v3).
        var sourceId = refund.RefundId.Value.ToString("D");
        var rule = await ledger.RuleAsync(ledger.Key(EntryTypes.RefundApproved, RuleQualifiers.Any, RuleQualifiers.Any), cancellationToken).ConfigureAwait(false);
        if (rule.IsFailure)
        {
            return rule.Error!;
        }

        var legs = lines.Select(l => new PostingLeg(
            rule.Value,
            new Money(l.Amount, currency),
            new LineDimensions
            {
                BillingAccountId = refund.BillingAccountId,
                PolicyId = l.PolicyId,
                PolicyTermId = l.TermId,
                TransactionId = l.TransactionId,
                ChargeType = l.ChargeType,
                ChargeCategory = l.ChargeCategory,
                InvoiceId = l.CreditNoteId,
                InvoiceItemId = l.CreditItemId,
                SourceType = DisbursementCodes.RefundPayment,
                SourceId = sourceId,
                TransactionKind = TransactionKinds.Refund,
            })).ToList();
        var lineage = BusinessKeys.Empty.With("billingAccountId", refund.BillingAccountId.Value.ToString()).With("refundId", sourceId);
        refund.ApprovedEntryId = ledger.Post(new EntrySpec(EntryTypes.RefundApproved, refund.BillingAccountId, legs, lineage, "bil.Refund.approve"));

        var keys = BusinessKeys.Empty.With("billingAccountId", refund.BillingAccountId.Value.ToString()).With("refundId", sourceId);
        var policyIds = lines.Select(l => l.PolicyId).Distinct().ToList();
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(RefundApprovedV1.Descriptor), "BillingAccount", refund.BillingAccountId.Value.ToString(),
            new RefundApprovedV1
            {
                RefundId = refund.RefundId,
                Amount = new Money(refund.Amount, currency),
                PayeePartyId = refund.PayeePartyId,
                PayoutMethod = refund.PayoutMethod,
                SourceRefs = [.. lines.Select(l => ObjectRef.For(ModuleCode.BIL, "Invoice", l.CreditNoteId)).Distinct()],
                DecisionMaker = RefundContent.ActorGuid(decidedBy ?? "SERVICE:bil-refund-rule"),
            },
            keys) { OccurredAt = now });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (context.DryRun)
        {
            return true;
        }

        // The disbursement (shared service, source BIL_REFUND): evidence is the refund's own approval.
        var evidence = refund.ApprovalRequestId is { } approval && decidedBy is not null
            ? "PLT/ApprovalRequest/" + approval.ToString("D")
            : DisbursementApproval.RefundAutoEvidencePrefix + sourceId;
        DisbursementRequestResponse disbursement;
        try
        {
            var disbursements = services.GetRequiredService<IBillingDisbursementService>();
            disbursement = await disbursements.RequestAsync(
                new DisbursementRequestRequest
                {
                    SourceType = DisbursementCodes.RefundPayment,
                    SourceId = sourceId,
                    PayeePartyId = refund.PayeePartyId,
                    PayeeAccountId = refund.PayeeAccountId,
                    Amount = new Money(refund.Amount, currency),
                    Method = DisbursementCodes.SepaCreditTransfer,
                    ApprovalEvidenceRef = evidence,
                    ApprovalContentHash = Sha256Hash.Parse(refund.ApprovalContentHash!),
                    PurposeText = "Refund",
                },
                new CommandOptions(IdempotencyKey.From(refund.RefundId.Value)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        refund.DisbursementId = disbursement.DisbursementId;
        var status = disbursement.Status;
        if (status is DisbursementRequestResponse.StatusValue.Released or DisbursementRequestResponse.StatusValue.Issued or DisbursementRequestResponse.StatusValue.Cleared)
        {
            refund.State = Codes.Of(machine.FireOrThrow(Codes.Parse<StoredRefundState>(refund.State), RefundTrigger.Disburse));
        }

        if (status is DisbursementRequestResponse.StatusValue.Issued or DisbursementRequestResponse.StatusValue.Cleared)
        {
            // Paid at ISSUED, the date the money leaves (REQ-BIL-190, D-SL3-09).
            refund.State = Codes.Of(machine.FireOrThrow(Codes.Parse<StoredRefundState>(refund.State), RefundTrigger.Pay));
            refund.PaidAt = now;
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(RefundDisbursedV1.Descriptor), "BillingAccount", refund.BillingAccountId.Value.ToString(),
                new RefundDisbursedV1
                {
                    RefundId = refund.RefundId,
                    DisbursementId = disbursement.DisbursementId,
                    Amount = new Money(refund.Amount, currency),
                    ValueDate = disbursement.ValueDate ?? ledger.Today,
                    SourcePolicyIds = policyIds,
                },
                keys.With("disbursementId", disbursement.DisbursementId.Value.ToString())) { OccurredAt = now });
        }

        refund.RecordVersion++;
        saved = await TrySaveAsync(cancellationToken).ConfigureAwait(false);
        if (saved is not null)
        {
            return saved;
        }

        LogApproved(logger, refund.RefundId.Value, refund.State, decidedBy is null ? "rule" : "approver");
        return true;
    }

    /// <summary>Rejects a pending refund: nothing was applied, so the credit stays on the account (REQ-BIL-191).</summary>
    public async Task<Result<bool>> RejectAsync(RefundRow refund, string decidedBy, string? comment, CancellationToken cancellationToken)
    {
        var now = clock.Now;
        refund.State = Codes.Of(RefundStateModel.Machine.FireOrThrow(Codes.Parse<StoredRefundState>(refund.State), RefundTrigger.Reject));
        refund.ApprovalState = Codes.Of(RefundApprovalStateCode.Rejected);
        refund.DecidedBy = decidedBy;
        refund.DecidedAt = now;
        refund.DecisionComment = comment;
        refund.RecordVersion++;
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(RefundRejectedV1.Descriptor), "BillingAccount", refund.BillingAccountId.Value.ToString(),
            new RefundRejectedV1 { RefundId = refund.RefundId, Reason = "APPROVER_REJECTED" },
            BusinessKeys.Empty.With("billingAccountId", refund.BillingAccountId.Value.ToString()).With("refundId", refund.RefundId.Value.ToString("D"))) { OccurredAt = now });
        var saved = await TrySaveAsync(cancellationToken).ConfigureAwait(false);
        return saved is not null ? saved : true;
    }

    private async Task SettleItemsAsync(List<RefundNettingRow> netting, List<RefundCreditRow> lines, Instant now, CancellationToken cancellationToken)
    {
        // Credit items used up are Settled; netted invoice items with nothing open are Settled and an invoice with nothing open is Paid.
        var creditIds = lines.Select(l => l.CreditItemId).Concat(netting.Select(n => n.CreditItemId)).Distinct().ToList();
        var creditItems = await db.InvoiceItems.Where(i => creditIds.Contains(i.InvoiceItemId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var used = await db.CreditApplications.AsNoTracking().Where(a => creditIds.Contains(a.CreditItemId)).GroupBy(a => a.CreditItemId)
            .Select(g => new { Item = g.Key, Sum = g.Sum(a => a.Amount) }).ToDictionaryAsync(g => g.Item, g => g.Sum, cancellationToken).ConfigureAwait(false);
        foreach (var item in creditItems.Where(i => used.GetValueOrDefault(i.InvoiceItemId) >= i.Amount))
        {
            item.State = Codes.Of(InvoiceItemState.Settled);
        }

        var invoiceIds = netting.Select(n => n.TargetInvoiceId).Distinct().ToList();
        if (invoiceIds.Count > 0)
        {
            var open = await allocator.OpenByItemAsync(invoiceIds, cancellationToken).ConfigureAwait(false);
            var invoices = await db.Invoices.Where(i => invoiceIds.Contains(i.InvoiceId)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var targets = await db.InvoiceItems.Where(i => invoiceIds.Contains(i.InvoiceId)).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in targets.Where(i => open.GetValueOrDefault(i.InvoiceItemId) == 0m && i.State is not "SETTLED" and not "CANCELLED"))
            {
                item.State = Codes.Of(InvoiceItemState.Settled);
            }

            foreach (var invoice in invoices)
            {
                var state = Codes.Parse<InvoiceState>(invoice.State);
                if (targets.Where(i => i.InvoiceId == invoice.InvoiceId).All(i => open.GetValueOrDefault(i.InvoiceItemId) == 0m)
                    && InvoiceStateModel.Machine.CanFire(state, InvoiceTrigger.AllocateInFull))
                {
                    invoice.State = Codes.Of(InvoiceStateModel.Machine.FireOrThrow(state, InvoiceTrigger.AllocateInFull));
                    invoice.UpdatedAt = now;
                    invoice.RecordVersion++;
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<(PayeeAccountRow? Account, DomainError? Error)> FindPayeeAccountAsync(
        LegalEntityId legalEntity, BillingAccountRow account, Guid? payeeAccountId, CancellationToken cancellationToken)
    {
        var active = Codes.Of(PayeeAccountStatus.Active);
        var query = db.PayeeAccounts.AsNoTracking().Where(a => a.LegalEntityId == legalEntity);
        var row = payeeAccountId is { } id
            ? await query.SingleOrDefaultAsync(a => a.PayeeAccountId == id, cancellationToken).ConfigureAwait(false)
            : await query.SingleOrDefaultAsync(
                a => a.PartyId == account.PayerPartyId && a.Purpose == DisbursementCodes.RefundPurpose && a.Status == active, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return (null, DomainError.Of(ModuleCode.BIL, "PAYEE-UNVERIFIED", "The payer has no verified bank account for refunds; register one first (REQ-BIL-186)."));
        }

        if (row.PartyId != account.PayerPartyId || row.Purpose != DisbursementCodes.RefundPurpose || row.Status != active || row.ValidTo is not null)
        {
            return (null, DomainError.Of(ModuleCode.BIL, "PAYEE-ACCOUNT", "A refund is paid to the payer's Active REFUND account (REQ-BIL-187)."));
        }

        return Codes.Parse<PayeeVerification>(row.VerificationStatus) is PayeeVerification.VopMatched or PayeeVerification.Confirmed
            ? (row, null)
            : (null, DomainError.Of(ModuleCode.BIL, "PAYEE-UNVERIFIED", $"Verification of payee is {row.VerificationStatus}; a refund is paid to a verified account only (REQ-BIL-186)."));
    }

    /// <summary>Saves; a race on the open-refund index is REFUND-OPEN and a guard or concurrency failure is STALE (409), never 500 (PITFALLS 15).</summary>
    private async Task<DomainError?> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_refund_open_per_account" })
        {
            return DomainError.Of(ModuleCode.BIL, "REFUND-OPEN", "The billing account already has an open refund; decide it first (one open refund per account).");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: Allocator.OverAllocationState })
        {
            return Stale("The credit or the open invoices of the account changed meanwhile; propose the refund again.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale("Someone else changed the refund meanwhile; load it and try again.");
        }
    }

    private static DomainError OpenError(RefundId existing) =>
        DomainError.Of(ModuleCode.BIL, "REFUND-OPEN", "The billing account already has an open refund; decide it first (one open refund per account).") with
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["existingRefundId"] = existing.Value.ToString("D") },
        };

    private static DomainError Stale(string detail) => DomainError.Of(ModuleCode.BIL, "STALE", detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refund {RefundId} of {Amount} proposed, state {State}, payee changed {PayeeChanged}.")]
    private static partial void LogProposed(ILogger logger, Guid refundId, decimal amount, string state, bool payeeChanged);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refund {RefundId} approved by {Basis}, state {State}.")]
    private static partial void LogApproved(ILogger logger, Guid refundId, string state, string basis);
}

internal sealed class ProposeRefundHandler(BillingDbContext db, LedgerWriter ledger, RefundWorkflow workflow) : ICommandHandler<ProposeRefund, RefundProposeResponse>
{
    public async Task<Result<RefundProposeResponse>> HandleAsync(ProposeRefund command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var account = await db.Accounts.SingleOrDefaultAsync(
            a => a.BillingAccountId == request.BillingAccountId && a.LegalEntityId == ledger.LegalEntityId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return BillingErrors.NotFound("billing account");
        }

        var view = await workflow.CreateAsync(account, request.Credits, request.PayeeAccountId, request.ReasonCode, request.Comment, null, cancellationToken).ConfigureAwait(false);
        return view.IsFailure ? view.Error! : new RefundProposeResponse { Refund = view.Value };
    }
}

internal sealed class ResubmitRefundHandler(BillingDbContext db, LedgerWriter ledger, RefundWorkflow workflow) : ICommandHandler<ResubmitRefund, RefundResubmitResponse>
{
    public async Task<Result<RefundResubmitResponse>> HandleAsync(ResubmitRefund command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = ledger.LegalEntityId;
        var rejected = await db.Refunds.AsNoTracking().SingleOrDefaultAsync(r => r.RefundId == request.RefundId && r.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (rejected is null)
        {
            return BillingErrors.NotFound("refund");
        }

        if (rejected.State != Codes.Of(StoredRefundState.Rejected))
        {
            return DomainError.Of(ModuleCode.BIL, "REFUND-STATE", $"Only a rejected refund is resubmitted; this one is {rejected.State} (REQ-BIL-191).");
        }

        // A resubmission may be made once: a refund already resubmitted is not resubmitted again (its successor is the refund to work on).
        if (await db.Refunds.AsNoTracking().AnyAsync(r => r.ResubmitsRefundId == rejected.RefundId, cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.BIL, "REFUND-STATE", "This refund was already resubmitted; work on the new refund (REQ-BIL-191).");
        }

        var account = await db.Accounts.SingleAsync(a => a.BillingAccountId == rejected.BillingAccountId, cancellationToken).ConfigureAwait(false);
        var view = await workflow.CreateAsync(
            account, rejected.SelectedCreditNotes, request.PayeeAccountId ?? rejected.PayeeAccountId, rejected.ReasonCode, request.Comment ?? rejected.Comment, rejected, cancellationToken)
            .ConfigureAwait(false);
        return view.IsFailure ? view.Error! : new RefundResubmitResponse { Refund = view.Value };
    }
}

internal sealed partial class DecideRefundHandler(
    BillingDbContext db,
    RequestContext context,
    LedgerWriter ledger,
    RefundWorkflow workflow,
    RefundReader reader,
    IServiceProvider services,
    ILogger<DecideRefundHandler> logger) : ICommandHandler<DecideRefund, RefundDecideResponse>
{
    public async Task<Result<RefundDecideResponse>> HandleAsync(DecideRefund command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = ledger.LegalEntityId;
        await workflow.LockAsync(request.RefundId.Value, cancellationToken).ConfigureAwait(false);
        var refund = await db.Refunds.SingleOrDefaultAsync(r => r.RefundId == request.RefundId && r.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (refund is null)
        {
            return BillingErrors.NotFound("refund");
        }

        if (refund.State != Codes.Of(StoredRefundState.PendingApproval) || refund.ApprovalRequestId is not { } approvalRequestId)
        {
            return DomainError.Of(ModuleCode.BIL, "REFUND-STATE", $"The refund is {refund.State}, not waiting for approval.");
        }

        // BIL's segregation of duties (REQ-BIL-189): everyone who took part in making the refund except the current maker (whom plt.Approval
        // refuses, with its own error), and the person who changed the payee account, cannot decide it; a delegated actor counts as its principal.
        var actorKeys = workflow.ActorKeys();
        var editors = refund.Participants.Where(p => p != refund.RequestedBy);
        if (actorKeys.Any(k => editors.Contains(k, StringComparer.Ordinal)) || actorKeys.Contains(refund.PayeeAccountChangedBy, StringComparer.Ordinal))
        {
            LogSodRefused(logger, refund.RefundId.Value);
            return DomainError.Of(ModuleCode.BIL, "SOD", "The requester, an editor of the refund or the person who changed the payee account cannot decide it (REQ-BIL-189).");
        }

        if (services.GetService<IPlatformApprovalService>() is not { } approvals)
        {
            return DomainError.Of(ModuleCode.BIL, "APPROVAL-MISMATCH", "The approval service is not available; the refund cannot be decided.");
        }

        var hash = Sha256Hash.Parse(refund.ApprovalContentHash!);
        var reject = request.Decision == RefundDecideRequest.DecisionValue.Reject;
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment;
        var subject = DisbursementApproval.RefundSubject(refund.RefundId.Value.ToString("D"));
        var money = new Money(refund.Amount, Currency.FromCode(refund.Currency));

        try
        {
            if (!context.DryRun)
            {
                // plt.Approval.decide: human checker, not the maker or an editor of the request, holding BIL.REFUND authority for the
                // request's own dimensions (amount, currency, payeeChanged, reason): re-checked here at decide time (PITFALLS 3/4).
                await approvals.DecideAsync(
                    new ApprovalDecideRequest
                    {
                        RequestId = approvalRequestId,
                        PayloadHash = hash,
                        Decision = reject ? ApprovalDecideRequest.DecisionValue.Reject : ApprovalDecideRequest.DecisionValue.Approve,
                        Comment = comment,
                    },
                    CommandOptions.New(),
                    cancellationToken).ConfigureAwait(false);
            }

            if (!reject && !context.DryRun)
            {
                // The decided approval must be for exactly this subject, type and content, and its authority must be what BIL computes now.
                var verified = await approvals.VerifyForExecutionAsync(
                    new ApprovalVerifyForExecutionRequest { RequestId = approvalRequestId, Hash = hash, Type = DisbursementApproval.RefundType, ObjectRef = subject },
                    cancellationToken).ConfigureAwait(false);
                var expectedCodes = RefundAuthority.ApprovalCodes(money, refund.PayeeChanged, refund.ReasonCode);
                var codes = verified.Authority.Codes ?? new Dictionary<string, string>();
                if (!verified.Ok || verified.Authority.Type != SupportAuthorityTypes.Refund.Value || verified.Authority.Amount != money
                    || expectedCodes.Any(e => !codes.TryGetValue(e.Key, out var value) || value != e.Value))
                {
                    return DomainError.Of(ModuleCode.BIL, "APPROVAL-MISMATCH", "The approval does not cover this refund's amount and payee; nothing is paid that was not approved.");
                }
            }
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        var decider = context.Actor.ToString();
        var outcome = reject
            ? await workflow.RejectAsync(refund, decider, comment, cancellationToken).ConfigureAwait(false)
            : await workflow.ApproveAndPayAsync(refund, decider, cancellationToken).ConfigureAwait(false);
        if (outcome.IsFailure)
        {
            return outcome.Error!;
        }

        var view = (await reader.ViewsAsync([refund], cancellationToken).ConfigureAwait(false))[0];
        return new RefundDecideResponse { Refund = view, DisbursementId = refund.DisbursementId };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "SECURITY: decision on refund {RefundId} refused: the actor took part in the refund or changed its payee account.")]
    private static partial void LogSodRefused(ILogger logger, Guid refundId);
}

/// <summary>Audit facts of the refund commands: the refund, its state and total; never an IBAN or a name.</summary>
internal static class RefundAudit
{
    public static CommandAuditFacts Facts(BusinessKeys keys, RefundView? view, string? extra = null) => view is null
        ? new CommandAuditFacts { ObjectRef = new ObjectRef(ModuleCode.BIL, "Refund", "request"), BusinessKeys = keys }
        : new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.BIL, "Refund", view.RefundId),
            BusinessKeys = keys.With("refundId", view.RefundId.Value.ToString()).With("billingAccountId", view.BillingAccountId.Value.ToString()),
            Changes = AuditDiff.Compute(null, new
            {
                state = view.State.ToString(),
                approvalState = view.ApprovalState.ToString(),
                amount = view.Amount.ToString(),
                payeeAccount = view.Payee.MaskedIban,
                reasonCode = view.ReasonCode,
                note = extra,
            }),
        };
}

internal sealed class ProposeRefundAuditor : ICommandAuditor<ProposeRefund, RefundProposeResponse>
{
    public CommandAuditFacts Describe(ProposeRefund command, Result<RefundProposeResponse>? result) =>
        RefundAudit.Facts(BusinessKeys.Empty.With("billingAccountId", command.Request.BillingAccountId.Value.ToString()), result is { IsSuccess: true } ok ? ok.Value.Refund : null);
}

internal sealed class ResubmitRefundAuditor : ICommandAuditor<ResubmitRefund, RefundResubmitResponse>
{
    public CommandAuditFacts Describe(ResubmitRefund command, Result<RefundResubmitResponse>? result) =>
        RefundAudit.Facts(BusinessKeys.Empty.With("resubmitsRefundId", command.Request.RefundId.Value.ToString()), result is { IsSuccess: true } ok ? ok.Value.Refund : null, "resubmission");
}

internal sealed class DecideRefundAuditor : ICommandAuditor<DecideRefund, RefundDecideResponse>
{
    public CommandAuditFacts Describe(DecideRefund command, Result<RefundDecideResponse>? result) =>
        RefundAudit.Facts(
            BusinessKeys.Empty.With("refundId", command.Request.RefundId.Value.ToString()),
            result is { IsSuccess: true } ok ? ok.Value.Refund : null,
            command.Request.Decision.ToString());
}
