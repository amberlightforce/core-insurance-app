using CoreIns.SharedKernel;

namespace CoreIns.Modules.Policy.Domain.Servicing;

/// <summary>Delta mode of an out-of-sequence correction set. NET is the default and final for P1-P2 (D-SL6-10); GROSS exists as a flag (unit-tested only).</summary>
internal enum OosMode
{
    Net,
    Gross,
}

/// <summary>One step of the corrected history: new annual rates from <paramref name="EffectiveAt"/> (the full rate set, as a <see cref="ChangeIntent"/>).</summary>
internal sealed record OosStep(Instant EffectiveAt, IReadOnlyList<ChargeRate> Rates);

/// <summary>One premium line of a correction set: a net difference (NET) or a reversal / re-application of a whole cumulative (GROSS).</summary>
internal sealed record OosLine(ChargeKey Key, string ChargeCategory, decimal Amount, string Role);

/// <summary>
/// Out-of-sequence reverse-and-reapply, the pure part (REQ-POL-006, D-SL6-10). The corrected history is the term opened and then every change
/// applied in effective-time order, the new change inserted at its place; the engine's cumulative is a pure function of the rates and periods
/// (see <see cref="ServicingEngine"/>), so the result equals the in-order result whatever order the transactions were bound in. The correction
/// is the difference between that state and the written one, per element × coverage × charge type.
/// </summary>
internal static class OosEngine
{
    /// <summary>Applies the steps in order on the opened term; the first refusal is returned as it is.</summary>
    public static ServicingResult ApplyChain(ServicingEngine engine, ServicingState opened, IEnumerable<OosStep> steps, DeltaCorrelation correlation)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(opened);
        ArgumentNullException.ThrowIfNull(steps);
        var state = opened;
        foreach (var step in steps)
        {
            var applied = engine.Apply(state, new ChangeIntent(step.EffectiveAt, step.Rates), correlation);
            if (!applied.IsAccepted)
            {
                return applied;
            }

            state = applied.State!;
        }

        return ServicingResult.Accepted(state, []);
    }

    /// <summary>The written cumulative premium per charge key.</summary>
    public static SortedDictionary<ChargeKey, decimal> Cumulative(ServicingState state) =>
        new(state.Segments.GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.Sum(s => s.Amount)));

    /// <summary>The category of each key (from its latest segment).</summary>
    private static Dictionary<ChargeKey, string> Categories(params ServicingState[] states) =>
        states.SelectMany(s => s.Segments).GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.OrderBy(s => s.From).Last().Rate.ChargeCategory);

    /// <summary>
    /// The lines that turn <paramref name="before"/> into <paramref name="after"/>. NET: one signed difference per key whose cumulative
    /// moved, summing to the premium difference. GROSS: for each such key a reversal of the written cumulative and the re-application of
    /// the new one (nothing is emitted for a cumulative of zero).
    /// </summary>
    public static IReadOnlyList<OosLine> Lines(OosMode mode, ServicingState before, ServicingState after)
    {
        var was = Cumulative(before);
        var now = Cumulative(after);
        var categories = Categories(before, after);
        var lines = new List<OosLine>();
        foreach (var key in was.Keys.Union(now.Keys).Order())
        {
            var (b, a) = (was.GetValueOrDefault(key), now.GetValueOrDefault(key));
            if (b == a)
            {
                continue;
            }

            if (mode == OosMode.Net)
            {
                lines.Add(new OosLine(key, categories[key], a - b, "NET"));
                continue;
            }

            if (b != 0m)
            {
                lines.Add(new OosLine(key, categories[key], -b, "REVERSAL"));
            }

            if (a != 0m)
            {
                lines.Add(new OosLine(key, categories[key], a, "REAPPLY"));
            }
        }

        return lines;
    }

    /// <summary>NET lines as servicing deltas over [<paramref name="from"/>, term end): the form the tax port and the charge lines take.</summary>
    public static IReadOnlyList<ServicingDelta> NetDeltas(IReadOnlyList<OosLine> lines, ServicingTerm term, Instant from, DeltaCorrelation correlation)
    {
        var days = DayCount.Days(from, term.To, term.Zone);
        var termDays = term.Days;
        var deltas = new List<ServicingDelta>();
        foreach (var line in lines.Where(l => l.Role == "NET"))
        {
            deltas.Add(new ServicingDelta(
                line.Key, line.ChargeCategory, from, term.To, DayCount.Date(from, term.Zone), DayCount.Date(term.To, term.Zone), line.Amount, days, days, termDays,
                line.Amount >= 0m ? TransactionKind.EndorsementDebit : TransactionKind.EndorsementCredit, correlation.CorrelationId, correlation.SetId, deltas.Count + 1));
        }

        return deltas;
    }
}
