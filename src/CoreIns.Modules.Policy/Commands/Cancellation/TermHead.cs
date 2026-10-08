using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Policy.Commands.Cancellation;

/// <summary>Error names the cancellation raises that <see cref="PolicyErrorNames"/> (TEMPORAL's file) does not carry.</summary>
internal static class CancellationErrors
{
    public const string IllegalTransition = "ILLEGAL-TRANSITION";
}

/// <summary>
/// Rebuilds the servicing engine's view of a term's head (charge segments with their annual rates and written amounts) from the
/// stored transactions and NET charge lines, which are the only record of it: the issuance lines give the first segments, and
/// each later Change transaction is replayed through the same engine. The result is then proven against the ledger: the segment
/// amounts of every premium key must add up to the sum of its stored lines (Σ deltas = written, P7). Anything that does not
/// reconstruct exactly fails closed instead of crediting a guess (PITFALLS 10, 11).
/// <para>
/// Assumption (until PFC exposes the day count of the pinned artefact to POL): replayed changes used TERM_RATIO. The sum check
/// catches a term that was written under another convention.
/// </para>
/// </summary>
internal static class TermHead
{
    public static Result<ServicingState> Reconstruct(
        ServicingEngine engine,
        ServicingTerm term,
        IReadOnlyList<PolicyTransactionRow> transactions,
        IReadOnlyList<ChargeLineRow> lines,
        DeltaCorrelation correlation)
    {
        var ordered = transactions.OrderBy(t => t.Sequence).ToList();
        if (ordered.Count == 0 || Codes.Parse<PolicyTransactionKind>(ordered[0].Kind) != PolicyTransactionKind.Issuance)
        {
            return Inconsistent("the term has no issuance transaction");
        }

        var rates = new Dictionary<ChargeKey, ChargeRate>();
        var segments = new List<ServicingSegment>();
        foreach (var line in PremiumLines(lines, ordered[0]))
        {
            var rate = new ChargeRate(line.ElementLocator, line.CoverageCode, line.ChargeType, line.ChargeCategory, line.AnnualRate);
            if (!rates.TryAdd(rate.Key, rate))
            {
                return Inconsistent($"{line.ChargeType} appears twice on the issuance");
            }

            segments.Add(new ServicingSegment(rate, term.From, term.To, line.Amount));
        }

        var state = new ServicingState(term, segments, null, ordered[0].EffectiveAt);
        foreach (var transaction in ordered.Skip(1))
        {
            if (Codes.Parse<PolicyTransactionKind>(transaction.Kind) != PolicyTransactionKind.Change)
            {
                return Inconsistent($"a {transaction.Kind} transaction is not supported by cancellation");
            }

            foreach (var line in PremiumLines(lines, transaction))
            {
                rates[new ChargeKey(line.ElementLocator, line.CoverageCode, line.ChargeType)] =
                    new ChargeRate(line.ElementLocator, line.CoverageCode, line.ChargeType, line.ChargeCategory, line.AnnualRate);
            }

            var applied = engine.Apply(state, new ChangeIntent(transaction.EffectiveAt, [.. rates.Values]), correlation);
            if (!applied.IsAccepted)
            {
                return Inconsistent($"change {transaction.Sequence} cannot be replayed: {applied.Message}");
            }

            state = applied.State!;
        }

        foreach (var group in lines.Where(l => l.ChargeCategory == ChargeCategories.Premium).GroupBy(l => new ChargeKey(l.ElementLocator, l.CoverageCode, l.ChargeType)))
        {
            var written = state.Segments.Where(s => s.Key == group.Key).Sum(s => s.Amount);
            if (written != group.Sum(l => l.Amount))
            {
                return Inconsistent($"the stored charges of {group.Key.ChargeType} ({group.Sum(l => l.Amount)}) differ from the replayed ones ({written})");
            }
        }

        return state;
    }

    private static IEnumerable<ChargeLineRow> PremiumLines(IReadOnlyList<ChargeLineRow> lines, PolicyTransactionRow transaction) =>
        lines.Where(l => l.TransactionId == transaction.TransactionId && l.ChargeCategory == ChargeCategories.Premium);

    private static DomainError Inconsistent(string detail) =>
        DomainError.Of(ModuleCode.POL, "GATE-FAILED", $"The term's charges cannot be reconstructed for a cancellation: {detail}. Nothing was changed.");
}
