using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Services;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Billing.Commands;

/// <summary>
/// Releases an Approved disbursement to the bound bank channel and, for the stub bank, issues and clears it at once (D-SL2-05):
/// DISBURSEMENT_RELEASED / _CLEARED entries (FS_CLEARING_RELEASED / _CLEARED for source FS_CLEARING, carrying the statement
/// reference and the counterparty), <c>DisbursementIssued</c> and <c>DisbursementCleared</c>. Shared by
/// <c>bil.Disbursement.request</c> and <c>bil.Disbursement.approveRelease</c>.
/// </summary>
internal sealed class DisbursementReleaser(LedgerWriter ledger, IEventPublisher events)
{
    public async Task ReleaseAsync(
        DisbursementRow row, IBankChannel channel, Money amount, BusinessDate today, Instant now, RefundRow? refund, CancellationToken cancellationToken)
    {
        var machine = DisbursementStateModel.Machine;
        var ctx = Describe(row, refund);
        var isFs = ctx.IsFs;
        var lineage = ctx.Lineage;
        var dimensions = ctx.Dimensions;
        var releasedType = isFs ? EntryTypes.FsClearingReleased : EntryTypes.DisbursementReleased;
        var valueDate = row.RequestedValueDate is { } requested && requested > today ? requested : today;

        // Released: the source's clearing account to disbursements in transit (REQ-BIL-211).
        var released = await ledger.RuleAsync(ledger.Key(releasedType, RuleQualifiers.Any, row.SourceType), cancellationToken).ConfigureAwait(false);
        var rule = released.IsSuccess ? released.Value : throw new DomainException(released.Error!);
        row.State = Codes.Of(machine.FireOrThrow(Codes.Parse<DisbursementState>(row.State), DisbursementTrigger.Release));
        row.ReleasedAt = now;
        row.ValueDate = valueDate;
        row.ReleaseEntryId = ledger.Post(new EntrySpec(
            releasedType, null, [new PostingLeg(rule, amount, dimensions)], lineage, "bil.Disbursement.release", row.DisbursementId));

        var outcome = await channel.SubmitAsync(
            new BankInstruction(row.DisbursementId, row.DisbursementNumber, amount, valueDate, row.Method, row.PayeeAccountId), cancellationToken).ConfigureAwait(false);
        if (!outcome.Accepted)
        {
            return; // Awaiting the bank's acknowledgement (manual stub bank: the dev endpoint issues it, SL4-BIL-DISBOPS).
        }

        // Issued on the bank's acknowledgement (REQ-BIL-207): DisbursementIssued.
        Issue(row, amount, refund, now, outcome.ValueDate, outcome.BankReference);
        if (outcome.Debited)
        {
            await ClearAsync(row, amount, refund, now, outcome.ValueDate, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Released → Issued on the bank's acknowledgement; publishes <c>DisbursementIssued</c>.</summary>
    public void Issue(DisbursementRow row, Money amount, RefundRow? refund, Instant now, BusinessDate valueDate, string bankReference)
    {
        var ctx = Describe(row, refund);
        row.State = Codes.Of(DisbursementStateModel.Machine.FireOrThrow(Codes.Parse<DisbursementState>(row.State), DisbursementTrigger.Issue));
        row.IssuedAt = now;
        row.BankReference = bankReference;
        row.ValueDate = valueDate;
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(DisbursementIssuedV1.Descriptor), "Disbursement", row.DisbursementId.Value.ToString(),
            new DisbursementIssuedV1
            {
                DisbursementId = row.DisbursementId,
                SourceModule = ctx.SourceModule,
                SourceType = row.SourceType,
                SourceId = row.SourceId,
                Amount = amount,
                ValueDate = valueDate,
                Method = row.Method,
            },
            ctx.Keys) { OccurredAt = now });
    }

    /// <summary>Issued → Cleared on the statement debit: disbursements in transit to cash at bank, <c>DisbursementCleared</c>.</summary>
    public async Task ClearAsync(DisbursementRow row, Money amount, RefundRow? refund, Instant now, BusinessDate valueDate, CancellationToken cancellationToken)
    {
        var ctx = Describe(row, refund);
        var clearedType = ctx.IsFs ? EntryTypes.FsClearingCleared : EntryTypes.DisbursementCleared;
        var cleared = await ledger.RuleAsync(ledger.Key(clearedType, RuleQualifiers.Any, row.SourceType), cancellationToken).ConfigureAwait(false);
        var clearRule = cleared.IsSuccess ? cleared.Value : throw new DomainException(cleared.Error!);
        row.State = Codes.Of(DisbursementStateModel.Machine.FireOrThrow(Codes.Parse<DisbursementState>(row.State), DisbursementTrigger.Clear));
        row.ClearedAt = now;
        row.ClearEntryId = ledger.Post(new EntrySpec(
            clearedType, null, [new PostingLeg(clearRule, amount, ctx.Dimensions)], ctx.Lineage, "bil.Disbursement.clear", row.DisbursementId));
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(DisbursementClearedV1.Descriptor), "Disbursement", row.DisbursementId.Value.ToString(),
            new DisbursementClearedV1
            {
                DisbursementId = row.DisbursementId,
                SourceModule = ctx.SourceModule,
                SourceType = row.SourceType,
                SourceId = row.SourceId,
                Amount = amount,
                ValueDate = valueDate,
                Method = row.Method,
            },
            ctx.Keys) { OccurredAt = now });
    }

    /// <summary>
    /// Posts the <c>DISBURSEMENT_REVERSED</c> entry of a disbursement whose release (and, after clearing, statement debit) is
    /// undone (SL4-BIL-DISBOPS, D-SL4-13): in transit LA-13 back to the source payable (LA-17 / LA-12 / LA-24) and, when it had
    /// cleared, cash LA-10 back to LA-13. Returns the entry id; null when nothing had been released yet.
    /// </summary>
    public async Task<Guid?> ReverseAsync(DisbursementRow row, Money amount, RefundRow? refund, CancellationToken cancellationToken)
    {
        if (row.ReleaseEntryId is null)
        {
            return null;
        }

        var ctx = Describe(row, refund);
        var legs = new List<PostingLeg>();
        if (row.ClearEntryId is not null)
        {
            legs.Add(new PostingLeg(await RuleOrThrowAsync(ReversalCash, cancellationToken).ConfigureAwait(false), amount, ctx.Dimensions));
        }

        legs.Add(new PostingLeg(await RuleOrThrowAsync(row.SourceType, cancellationToken).ConfigureAwait(false), amount, ctx.Dimensions));
        return ledger.Post(new EntrySpec(EntryTypes.DisbursementReversed, null, legs, ctx.Lineage, "bil.Disbursement.reverse", row.DisbursementId));
    }

    /// <summary>Qualifier of the cash leg of a reversal (the reversal of a statement debit).</summary>
    public const string ReversalCash = "CASH";

    private async Task<LedgerRule> RuleOrThrowAsync(string qualifier, CancellationToken cancellationToken)
    {
        var found = await ledger.RuleAsync(ledger.Key(EntryTypes.DisbursementReversed, RuleQualifiers.Any, qualifier), cancellationToken).ConfigureAwait(false);
        return found.IsSuccess ? found.Value : throw new DomainException(found.Error!);
    }

    private sealed record DisbursementContext(ModuleCode SourceModule, bool IsFs, BusinessKeys Lineage, BusinessKeys Keys, LineDimensions Dimensions);

    private static DisbursementContext Describe(DisbursementRow row, RefundRow? refund)
    {
        var lineage = BusinessKeys.Empty.With("disbursementId", row.DisbursementId.Value.ToString()).With("sourceId", row.SourceId);
        lineage = row.ClaimId is { } claim ? lineage.With("claimId", claim.Value.ToString()) : lineage;
        lineage = refund is null ? lineage : lineage.With("refundId", refund.RefundId.Value.ToString()).With("billingAccountId", refund.BillingAccountId.Value.ToString());
        var isFs = row.SourceType == DisbursementCodes.FsClearing;
        lineage = isFs && row.StatementRef is not null ? lineage.With("statementRef", row.StatementRef) : lineage;
        var dimensions = new LineDimensions
        {
            DisbursementId = row.DisbursementId,
            SourceType = row.SourceType,
            SourceId = row.SourceId,
            ClaimId = row.ClaimId,
            BillingAccountId = refund?.BillingAccountId,
            TransactionKind = refund is null ? null : TransactionKinds.Refund,
            StatementRef = isFs ? row.StatementRef : null,
            CounterpartyPartyId = isFs ? row.PayeePartyId : null,
        };
        var keys = BusinessKeys.Empty.With("disbursementId", row.DisbursementId.Value.ToString());
        keys = row.ClaimId is { } claimId ? keys.With("claimId", claimId.Value.ToString()) : keys;
        keys = refund is null ? keys : keys.With("refundId", refund.RefundId.Value.ToString()).With("billingAccountId", refund.BillingAccountId.Value.ToString());
        return new DisbursementContext(Enum.Parse<ModuleCode>(row.SourceModule), isFs, lineage, keys, dimensions);
    }
}
