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
        var sourceModule = Enum.Parse<ModuleCode>(row.SourceModule);
        var lineage = BusinessKeys.Empty.With("disbursementId", row.DisbursementId.Value.ToString()).With("sourceId", row.SourceId);
        lineage = row.ClaimId is { } claim ? lineage.With("claimId", claim.Value.ToString()) : lineage;
        lineage = refund is null ? lineage : lineage.With("refundId", refund.RefundId.Value.ToString()).With("billingAccountId", refund.BillingAccountId.Value.ToString());

        // A refund's lines carry its account and the REFUND transaction kind (servicing entry, FIN rule set v3).
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
        var releasedType = isFs ? EntryTypes.FsClearingReleased : EntryTypes.DisbursementReleased;
        var clearedType = isFs ? EntryTypes.FsClearingCleared : EntryTypes.DisbursementCleared;
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
        var cleared = await ledger.RuleAsync(ledger.Key(clearedType, RuleQualifiers.Any, row.SourceType), cancellationToken).ConfigureAwait(false);
        var clearRule = cleared.IsSuccess ? cleared.Value : throw new DomainException(cleared.Error!);
        row.State = Codes.Of(machine.FireOrThrow(DisbursementState.Issued, DisbursementTrigger.Clear));
        row.ClearedAt = now;
        row.ClearEntryId = ledger.Post(new EntrySpec(
            clearedType, null, [new PostingLeg(clearRule, amount, dimensions)], lineage, "bil.Disbursement.clear", row.DisbursementId));
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
}
