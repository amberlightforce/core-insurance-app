using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using CoreIns.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Claims.Commands.Payments;

/// <summary>What a correction did to the payment.</summary>
/// <param name="Applied">False when the payment was already corrected (a replayed BIL fact or a second call).</param>
/// <param name="ReversalTransactionId">The first reversal transaction written (null when none was needed).</param>
internal sealed record CorrectionOutcome(bool Applied, Guid? ReversalTransactionId);

/// <summary>
/// The ledger effect of a payment correction (REQ-CLM-125..127, PRD-07 §7.3.3). A payment that is Approved or later is already in
/// the ledger as an eroding payment, so the correction writes a new, Approved, system-recorded set of reversal transactions
/// (negative payment lines linked to the originals through <c>reverses_txn_id</c>, same reserve lines, same eroding flag): the
/// derived open reserve of an eroding payment is restored, the paid total falls, and nothing is edited (D-ARC-34). The payment moves
/// to Voided, Stopped, Returned or Rejected; <c>PaymentVoided</c> is published once, on the claim's event order, only for a payment
/// that was issued (FIN reverses the journal of a <c>PaymentIssued</c>; a payment never issued has nothing to reverse). Idempotent:
/// a payment already corrected is left as it is. Runs in the caller's transaction; the caller holds the claim lock.
/// </summary>
internal sealed class PaymentCorrections(ClaimsDbContext db, RequestContext context, IClock clock, IEventPublisher events, FinancialsReader reader, IOptions<ClaimsOptions> options)
{
    private static readonly string[] Corrected =
        [Codes.Of(PaymentStatus.Voided), Codes.Of(PaymentStatus.Stopped), Codes.Of(PaymentStatus.Returned), Codes.Of(PaymentStatus.Rejected)];

    /// <summary>True when the payment already ended in a correction.</summary>
    public static bool IsCorrected(ClaimPaymentRow payment) => Corrected.Contains(payment.Status);

    public async Task<CorrectionOutcome> ApplyAsync(
        ClaimRow claim, ClaimPaymentRow payment, PaymentStatus target, PaymentVoidedV1.ReasonValue reason, string reasonCode, CancellationToken cancellationToken)
    {
        if (IsCorrected(payment))
        {
            return new CorrectionOutcome(false, null);
        }

        var now = clock.Now;
        var set = await db.TransactionSets.AsNoTracking().SingleAsync(s => s.SetId == payment.SetId, cancellationToken).ConfigureAwait(false);
        var inLedger = set.Status is "APPROVED" or "POSTED";
        Guid? reversalId = null;
        var originals = new List<FinancialTransactionRow>();
        if (inLedger && !(target == PaymentStatus.Rejected && payment.IssuedAt is null))
        {
            originals = await db.FinancialTransactions.AsNoTracking()
                .Where(t => t.ClaimPaymentId == payment.ClaimPaymentId && t.Kind == "PAYMENT" && t.ReversesTxnId == null).OrderBy(t => t.Sequence)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            reversalId = await WriteReversalAsync(claim, payment, originals, target, reasonCode, now, cancellationToken).ConfigureAwait(false);
        }

        payment.Status = Codes.Of(target);
        payment.HoldReason = null;
        payment.UpdatedAt = now;
        payment.RecordVersion++;
        if (payment.IssuedAt is not null && originals.Count > 0 && reversalId is { } first)
        {
            await PublishVoidedAsync(claim, payment, originals, reason, first, cancellationToken).ConfigureAwait(false);
        }

        return new CorrectionOutcome(true, reversalId);
    }

    private async Task<Guid?> WriteReversalAsync(
        ClaimRow claim, ClaimPaymentRow payment, List<FinancialTransactionRow> originals, PaymentStatus target, string reasonCode, Instant now,
        CancellationToken cancellationToken)
    {
        if (originals.Count == 0)
        {
            return null;
        }

        var actor = context.Actor.ToString();
        var today = DateOf(now);
        var lineIds = originals.Select(t => t.ReserveLineId).Distinct().ToList();
        var lines = await db.ReserveLines.Where(l => lineIds.Contains(l.ReserveLineId)).ToDictionaryAsync(l => l.ReserveLineId, cancellationToken).ConfigureAwait(false);
        var allLines = await reader.LinesAsync(claim.ClaimId, cancellationToken).ConfigureAwait(false);
        var approved = await reader.ApprovedAsync(claim.ClaimId, null, cancellationToken).ConfigureAwait(false);
        var exposureIds = originals.Select(t => t.ExposureId).Distinct().ToList();
        var exposures = await db.Exposures.AsNoTracking().Where(e => exposureIds.Contains(e.ExposureId)).ToListAsync(cancellationToken).ConfigureAwait(false);

        var set = new TransactionSetRow
        {
            SetId = ClaimTransactionSetId.New(), ClaimId = claim.ClaimId, Status = Codes.Of(SetStatus.Draft), LegalEntityId = claim.LegalEntityId,
            Jurisdiction = claim.Jurisdiction, CreatedAt = now, CreatedBy = actor, UpdatedAt = now,
            EvidenceKind = "DISBURSEMENT_OUTCOME", EvidenceRef = $"{payment.ClaimPaymentId.Value:D}:{Codes.Of(target)}",
        };
        var reversals = new List<FinancialTransactionRow>();
        foreach (var original in originals)
        {
            claim.LastTransactionSequence++;
            reversals.Add(new FinancialTransactionRow
            {
                TxnId = Guid.CreateVersion7(), SetId = set.SetId, ClaimId = claim.ClaimId, ReserveLineId = original.ReserveLineId, ExposureId = original.ExposureId,
                Sequence = claim.LastTransactionSequence, TxnNumber = $"{claim.ClaimNumber.Value}-T{claim.LastTransactionSequence:D4}", Kind = original.Kind,
                Amount = -original.Amount, Currency = original.Currency, FunctionalAmount = -original.FunctionalAmount, FunctionalCurrency = original.FunctionalCurrency,
                GroupAmount = -original.GroupAmount, GroupCurrency = original.GroupCurrency, FxRateId = original.FxRateId, Eroding = original.Eroding,
                PaymentType = original.PaymentType, ClaimPaymentId = payment.ClaimPaymentId, ReversesTxnId = original.TxnId, ReasonCode = reasonCode,
                Proposed = false, TransactionDate = today, LegalEntityId = claim.LegalEntityId, Jurisdiction = claim.Jurisdiction, CreatedAt = now, CreatedBy = actor,
            });
        }

        var content = new SetContent(set, reversals, lines, [payment]);
        set.ContentHash = SetHashing.Content(set.SetId, claim.ClaimId, content.Canonical()).Value;
        set.BasisHash = SetLifecycle.Basis(
            claim, exposures.Select(e => (e.ExposureId, e.Status)), allLines.Select(l => (SetLifecycle.Key(l), approved.GetValueOrDefault(l.ReserveLineId)))).Value;
        db.TransactionSets.Add(set);
        db.FinancialTransactions.AddRange(reversals);
        claim.UpdatedAt = now;
        // The set header seals its lines to this database transaction while it is a Draft; then it is approved (a system correction, no four-eyes).
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        set.Status = Codes.Of(SetStatus.Approved);
        set.Submitter = actor;
        set.SubmittedAt = now;
        set.ApprovedAt = now;
        set.DecidedAt = now;
        set.Approver = actor;
        set.FourEyes = false;
        set.UpdatedAt = now;
        set.RecordVersion++;
        return reversals[0].TxnId;
    }

    private async Task PublishVoidedAsync(
        ClaimRow claim, ClaimPaymentRow payment, List<FinancialTransactionRow> originals, PaymentVoidedV1.ReasonValue reason, Guid reversalId,
        CancellationToken cancellationToken)
    {
        var lineIds = originals.Select(t => t.ReserveLineId).ToList();
        var lines = await db.ReserveLines.AsNoTracking().Where(l => lineIds.Contains(l.ReserveLineId)).ToDictionaryAsync(l => l.ReserveLineId, cancellationToken)
            .ConfigureAwait(false);
        var reversalIds = await db.FinancialTransactions.AsNoTracking().Where(t => t.ReversesTxnId != null && originals.Select(o => o.TxnId).Contains(t.ReversesTxnId.Value))
            .OrderBy(t => t.Sequence).Select(t => t.TxnId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (reversalIds.Count == 0)
        {
            reversalIds = [reversalId];
        }

        var claimKey = claim.ClaimId.Value.ToString();
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PaymentVoidedV1.Descriptor),
            ClaimEvents.AggregateType,
            claimKey,
            new PaymentVoidedV1
            {
                ClaimId = claim.ClaimId,
                PaymentId = new PaymentId(payment.ClaimPaymentId.Value),
                ReversalTransactionIds = [.. reversalIds.Select(id => new PolicyTransactionId(id))],
                Reason = reason,
                Method = payment.Method,
                Amount = SetLifecycle.Three(originals.Sum(t => t.Amount), payment.Currency),
                Lines = [.. originals.Select(t => new ClaimPaymentLine
                {
                    LineKey = t.TxnId.ToString("D"),
                    Amount = SetLifecycle.Three(t.Amount, t.Currency),
                    ReserveLineId = t.ReserveLineId.Value,
                    ExposureId = t.ExposureId,
                    CostType = lines[t.ReserveLineId].CostType,
                    CostCategory = lines[t.ReserveLineId].CostCategory,
                    Eroding = t.Eroding ?? false,
                })],
            },
            BusinessKeys.Empty.With("claimId", claimKey).With("paymentId", payment.ClaimPaymentId.Value.ToString())));
    }

    private BusinessDate DateOf(Instant now) => options.Value.DateOf(now);
}
