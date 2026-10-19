using System.Text.Json;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.Posting;

/// <summary>The outcome of a PaymentVoided: the reversal journals written, or why none could be (an intake exception).</summary>
internal sealed record PaymentVoidOutcome(IReadOnlyList<Guid> JournalIds, string? Reason, string? Detail)
{
    public static PaymentVoidOutcome Fail(string reason, string detail) => new([], reason, detail);
}

/// <summary>
/// CLM <c>PaymentVoided</c> (REQ-FIN-159, SL4-FIN-V4): the payment journal of every active book is reversed by a linked reversal
/// journal, never edited (REQ-FIN-072, -073). BIL's DISBURSEMENT_REVERSED entry reverses the cash side separately, so GL-2510 returns
/// to its previous balance per payment. Fail closed: no payment journal (the void arrived before the payment, or the payment never
/// posted), an already reversed journal, or a partial void (an amount below the payment) is an intake exception.
/// </summary>
internal sealed class PaymentVoidPosting(FinanceDbContext db, JournalReversal reversal)
{
    public static readonly string PaymentVoided = $"clm.{PaymentVoidedV1.EventType}";

    public async Task<PaymentVoidOutcome> ReverseAsync(string payload, LegalEntityId legalEntity, BusinessDate occurredOn, CancellationToken cancellationToken)
    {
        var voided = JsonSerializer.Deserialize<PaymentVoidedV1>(payload, SharedKernelJson.Options)
            ?? throw new JsonException("PaymentVoided payload is empty.");
        var paymentRef = voided.PaymentId.Value.ToString("D");
        var originals = await db.Journals.AsNoTracking()
            .Where(j => j.LegalEntityId == legalEntity.Value && j.SourceModule == "CLM" && j.SourceEventType == PaymentIssuedEventType
                && j.SourceType == SourceTypes.Event && j.SourceRef == paymentRef)
            .OrderBy(j => j.Book)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (originals.Count == 0)
        {
            return PaymentVoidOutcome.Fail(ExceptionReasons.NoOriginalJournal, $"No posted PaymentIssued journal exists for payment {paymentRef}; nothing to reverse (REQ-FIN-159).");
        }

        if (voided.Amount is { } amount)
        {
            foreach (var original in originals)
            {
                var debits = await db.JournalLines.AsNoTracking()
                    .Where(l => l.JournalId == original.JournalId && l.Side == Sides.Debit)
                    .SumAsync(l => l.Amount, cancellationToken).ConfigureAwait(false);
                if (amount.Transaction.Amount != debits)
                {
                    return PaymentVoidOutcome.Fail(ExceptionReasons.VoidRefused,
                        $"PaymentVoided amount {amount.Transaction} differs from the payment journal {original.JournalNumber}; partial voids are not supported (TODO(hardening)).");
                }
            }
        }

        var written = new List<Guid>();
        foreach (var original in originals)
        {
            var result = await reversal.ReverseAsync(legalEntity, original.JournalId, occurredOn, $"PaymentVoided: {voided.Reason}", cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return PaymentVoidOutcome.Fail(ExceptionReasons.VoidRefused, result.Error!.Detail ?? result.Error.ToString());
            }

            written.Add(result.Value.JournalId);
        }

        return new PaymentVoidOutcome(written, null, null);
    }

    private static string PaymentIssuedEventType => PaymentIssuedV1.EventType;
}
