using System.Text.Json;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Platform.Events;
using CoreIns.Rules;
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
/// to its previous balance per payment. A partial void (NX-HD-06) cannot use the link (a journal is reversed at most once): it posts a
/// compensating journal with every line on the opposite side, scaled to the voided share (rounded through <see cref="DecimalRounding"/>,
/// the residual on the largest line of each side so the journal balances), sourced as <c>PaymentVoided</c> with
/// <c>sourceRef = paymentId:void:businessEventId</c>. The voids of one payment never exceed it in total.
/// Fail closed: no payment journal (the void arrived before the payment, or the payment never posted), an already reversed journal, or
/// voids that together exceed the payment is an intake exception, and every book is decided before any journal is written.
/// </summary>
internal sealed class PaymentVoidPosting(FinanceDbContext db, JournalReversal reversal, JournalWriter writer, IEventPublisher events)
{
    public static readonly string PaymentVoided = $"clm.{PaymentVoidedV1.EventType}";

    public async Task<PaymentVoidOutcome> ReverseAsync(string payload, Guid businessEventId, LegalEntityId legalEntity, BusinessDate occurredOn, CancellationToken cancellationToken)
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

        // Phase 1: decide every book before any journal is written (fail closed, no partial post).
        var plans = new List<(JournalEntryRow Original, IReadOnlyList<JournalLineDraft> Lines, decimal Debits, decimal Prior, decimal Requested)>();
        var prefix = $"{paymentRef}:void:";
        foreach (var original in originals)
        {
            if (await db.Journals.AsNoTracking().AnyAsync(j => j.ReversesJournalId == original.JournalId, cancellationToken).ConfigureAwait(false))
            {
                return PaymentVoidOutcome.Fail(ExceptionReasons.VoidRefused, $"Payment journal {original.JournalNumber} is already reversed (REQ-FIN-073).");
            }

            var loaded = await JournalRows.LoadAsync(db, legalEntity, original.JournalId, cancellationToken).ConfigureAwait(false);
            if (loaded is not (_, var lines))
            {
                return PaymentVoidOutcome.Fail(ExceptionReasons.NoOriginalJournal, $"Payment journal {original.JournalNumber} cannot be loaded.");
            }

            var debits = lines.Where(l => l.Side == Sides.Debit).Sum(l => l.Amount.Amount);
            var prior = await PriorPartialVoidsAsync(legalEntity, original.Book, prefix, cancellationToken).ConfigureAwait(false);
            var remaining = debits - prior;
            var requested = remaining;
            if (voided.Amount is { } amount)
            {
                var currency = lines.First(l => l.Side == Sides.Debit).Amount.Currency;
                if (amount.Transaction.Currency != currency)
                {
                    return PaymentVoidOutcome.Fail(ExceptionReasons.VoidRefused, $"PaymentVoided amount {amount.Transaction} is not in the payment currency {currency.Code} of journal {original.JournalNumber}.");
                }

                requested = amount.Transaction.Amount;
            }

            if (requested <= 0m || requested > remaining)
            {
                return PaymentVoidOutcome.Fail(ExceptionReasons.VoidRefused,
                    $"PaymentVoided amount {requested} is not within the {remaining} of payment journal {original.JournalNumber} not yet voided (payment {debits}, voided before {prior}).");
            }

            plans.Add((original, lines, debits, prior, requested));
        }

        // Phase 2: a void of the whole untouched payment is the linked reversal; anything else is a scaled compensating journal.
        var written = new List<Guid>();
        foreach (var (original, lines, debits, prior, requested) in plans)
        {
            if (prior == 0m && requested == debits)
            {
                var result = await reversal.ReverseAsync(legalEntity, original.JournalId, occurredOn, $"PaymentVoided: {voided.Reason}", cancellationToken).ConfigureAwait(false);
                if (result.IsFailure)
                {
                    return PaymentVoidOutcome.Fail(ExceptionReasons.VoidRefused, result.Error!.Detail ?? result.Error.ToString());
                }

                written.Add(result.Value.JournalId);
                continue;
            }

            var scaled = Scale(lines, requested / debits);
            if (scaled is null)
            {
                return PaymentVoidOutcome.Fail(ExceptionReasons.VoidRefused, $"PaymentVoided amount {requested} is too small to post against payment journal {original.JournalNumber} (TODO(hardening): sub-cent voids).");
            }

            var draft = new JournalDraft(original.Book, occurredOn, occurredOn, SourceTypes.Event, original.RuleSetId, original.RuleSetVersion, original.RuleCodes, scaled);
            var source = new JournalSource(legalEntity, original.LegalEntityCode, original.Jurisdiction, "CLM", PaymentVoidedV1.EventType, [businessEventId], prefix + businessEventId.ToString("D"));
            var posted = await writer.WriteAsync(draft, source, Currency.FromCode(original.FunctionalCurrency.Trim()), null,
                $"PaymentVoided (partial {requested} of {debits}): {voided.Reason}", cancellationToken).ConfigureAwait(false);
            events.Publish(posted.Posted);
            written.Add(posted.JournalId);
        }

        return new PaymentVoidOutcome(written, null, null);
    }

    /// <summary>Σ debits of the partial-void journals already posted against the payment in <paramref name="book"/>.</summary>
    private async Task<decimal> PriorPartialVoidsAsync(LegalEntityId legalEntity, string book, string prefix, CancellationToken cancellationToken)
    {
        var ids = await db.Journals.AsNoTracking()
            .Where(j => j.LegalEntityId == legalEntity.Value && j.Book == book && j.SourceModule == "CLM" && j.SourceEventType == PaymentVoidedV1.EventType
                && j.SourceType == SourceTypes.Event && j.SourceRef.StartsWith(prefix))
            .Select(j => j.JournalId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ids.Count == 0
            ? 0m
            : await db.JournalLines.AsNoTracking().Where(l => ids.Contains(l.JournalId) && l.Side == Sides.Debit).SumAsync(l => l.Amount, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The compensating lines of a share of a journal: opposite sides, amounts times <paramref name="ratio"/> rounded to the currency's
    /// minor units, the residual (target minus the sum of the rounded lines) on the largest line of each side so debits equal credits. Null
    /// when a side would be empty after rounding or the result does not balance.
    /// </summary>
    private static List<JournalLineDraft>? Scale(IReadOnlyList<JournalLineDraft> lines, decimal ratio)
    {
        var amounts = ScaleSide(lines, ratio, l => l.Amount);
        var functional = ScaleSide(lines, ratio, l => l.FunctionalAmount);
        var result = new List<JournalLineDraft>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (amounts[i].Amount > 0m && functional[i].Amount > 0m)
            {
                result.Add(lines[i] with { Side = Sides.Opposite(lines[i].Side), Amount = amounts[i], FunctionalAmount = functional[i] });
            }
        }

        return result.Select(l => l.Side).Distinct().Count() == 2 && Journals.Check(result).Count == 0 ? result : null;
    }

    private static Money[] ScaleSide(IReadOnlyList<JournalLineDraft> lines, decimal ratio, Func<JournalLineDraft, Money> pick)
    {
        var scaled = new Money[lines.Count];
        foreach (var side in new[] { Sides.Debit, Sides.Credit })
        {
            var idx = Enumerable.Range(0, lines.Count).Where(i => lines[i].Side == side).ToList();
            if (idx.Count == 0)
            {
                continue;
            }

            var currency = pick(lines[idx[0]]).Currency;
            var places = currency.MinorUnits;
            var target = DecimalRounding.Round(idx.Sum(i => pick(lines[i]).Amount) * ratio, places, DecimalRounding.DefaultMoneyMode);
            var rounded = idx.ToDictionary(i => i, i => DecimalRounding.Round(pick(lines[i]).Amount * ratio, places, DecimalRounding.DefaultMoneyMode));
            var largest = idx.OrderByDescending(i => pick(lines[i]).Amount).First();
            rounded[largest] += target - rounded.Values.Sum();
            foreach (var i in idx)
            {
                scaled[i] = new Money(rounded[i], currency);
            }
        }

        return scaled;
    }

    private static string PaymentIssuedEventType => PaymentIssuedV1.EventType;
}
