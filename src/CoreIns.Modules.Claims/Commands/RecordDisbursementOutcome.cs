using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Claims.Commands;

/// <summary>BIL's disbursement fact for a claim payment (consumed from <c>DisbursementIssued</c> / <c>DisbursementCleared</c>).</summary>
internal sealed record RecordDisbursementOutcome(ClaimPaymentId ClaimPaymentId, DisbursementId DisbursementId, bool Cleared) : ICommand<string>;

/// <summary>
/// Moves the claim payment along BIL's status (REQ-CLM-004, -128; PRD-07 §7.3.3): Submitted → Issued publishes
/// <c>PaymentIssued</c> on the claim's event order (the FIN posting source, D-SL2-08; source id = payment id, D-SL2-12 b);
/// Issued → Cleared. A Cleared fact that arrives before Issued (a replayed dead letter, D-ARC-26) issues first. Idempotent:
/// a payment already at or past the fact is left as it is.
/// </summary>
internal sealed class RecordDisbursementOutcomeHandler(
    ClaimsDbContext db,
    RequestContext context,
    IClock clock,
    IEventPublisher events,
    ClaimProtection protection,
    IOptions<ClaimsOptions> options) : ICommandHandler<RecordDisbursementOutcome, string>
{
    public async Task<Result<string>> HandleAsync(RecordDisbursementOutcome command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var claimId = await db.ClaimPayments.AsNoTracking()
            .Where(p => p.ClaimPaymentId == command.ClaimPaymentId && p.LegalEntityId == legalEntity).Select(p => (ClaimId?)p.ClaimId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (claimId is null)
        {
            return "IGNORED";
        }

        var claim = (await ClaimSupport.LoadAsync(db, legalEntity, claimId.Value, cancellationToken).ConfigureAwait(false))!;
        var payment = await db.ClaimPayments.SingleAsync(p => p.ClaimPaymentId == command.ClaimPaymentId, cancellationToken).ConfigureAwait(false);
        if (payment.DisbursementId is { } known && known != command.DisbursementId)
        {
            return "IGNORED"; // another disbursement than the one this payment requested
        }

        var now = clock.Now;
        var state = Codes.Parse<PaymentStatus>(payment.Status);
        if (state == PaymentStatus.Submitted)
        {
            payment.Status = Codes.Of(PaymentStatus.Issued);
            payment.DisbursementId = command.DisbursementId;
            payment.IssuedAt = now;
            payment.HoldReason = null;
            payment.UpdatedAt = now;
            payment.RecordVersion++;
            await PublishIssuedAsync(claim, payment, now, cancellationToken).ConfigureAwait(false);
            state = PaymentStatus.Issued;
        }

        if (command.Cleared && state == PaymentStatus.Issued)
        {
            payment.Status = Codes.Of(PaymentStatus.Cleared);
            payment.ClearedAt = now;
            payment.UpdatedAt = now;
            payment.RecordVersion++;
        }

        return payment.Status;
    }

    private async Task PublishIssuedAsync(ClaimRow claim, ClaimPaymentRow payment, CoreIns.SharedKernel.Instant now, CancellationToken cancellationToken)
    {
        var transactions = await db.FinancialTransactions.AsNoTracking().Where(t => t.ClaimPaymentId == payment.ClaimPaymentId).OrderBy(t => t.Sequence)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var lineIds = transactions.Select(t => t.ReserveLineId).ToList();
        var lines = await db.ReserveLines.AsNoTracking().Where(l => lineIds.Contains(l.ReserveLineId)).ToDictionaryAsync(l => l.ReserveLineId, cancellationToken)
            .ConfigureAwait(false);
        var claimKey = claim.ClaimId.Value.ToString();
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PaymentIssuedV1.Descriptor),
            ClaimEvents.AggregateType,
            claimKey,
            new PaymentIssuedV1
            {
                ClaimId = claim.ClaimId,
                PaymentId = new PaymentId(payment.ClaimPaymentId.Value),
                TransactionIds = [.. transactions.Select(t => new PolicyTransactionId(t.TxnId))],
                Lines = [.. transactions.Select(t => new ClaimPaymentLine
                {
                    LineKey = t.TxnId.ToString("D"),
                    Amount = SetLifecycle.Three(t.Amount, t.Currency),
                    ReserveLineId = t.ReserveLineId.Value,
                    ExposureId = t.ExposureId,
                    CostType = lines[t.ReserveLineId].CostType,
                    CostCategory = lines[t.ReserveLineId].CostCategory,
                    Eroding = t.Eroding ?? false,
                })],
                Amount = SetLifecycle.Three(transactions.Sum(t => t.Amount), payment.Currency),
                PayeePartyId = payment.PayeePartyId,
                Method = payment.Method,
                DisbursementId = payment.DisbursementId!.Value,
                ExGratia = false,
                ComplaintRef = null,
                AccountingDate = options.Value.DateOf(now),
            },
            BusinessKeys.Empty.With("claimId", claimKey).With("paymentId", payment.ClaimPaymentId.Value.ToString())
                .With("disbursementId", payment.DisbursementId!.Value.Value.ToString())));
    }
}

/// <summary>Audit facts: the payment and its new status.</summary>
internal sealed class RecordDisbursementOutcomeAuditor : ICommandAuditor<RecordDisbursementOutcome, string>
{
    public CommandAuditFacts Describe(RecordDisbursementOutcome command, Result<string>? result) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.CLM, "ClaimPayment", command.ClaimPaymentId),
        BusinessKeys = BusinessKeys.Empty.With("paymentId", command.ClaimPaymentId.Value.ToString()).With("disbursementId", command.DisbursementId.Value.ToString()),
        Changes = result is { IsSuccess: true } ok ? AuditDiff.Compute(null, new { status = ok.Value }) : [],
    };
}
