using CoreIns.Rules;

namespace CoreIns.Modules.Finance.Domain;

/// <summary>
/// Earned premium rules (BO-FIN-06a, REQ-FIN-003). Premium is earned pro rata temporis by day over the half-open term
/// <c>[start, end)</c> in Europe/Athens calendar dates: earned = written x daysElapsed / termDays (TERM_RATIO), so a 365 or a
/// 366-day term is earned in full at its end whatever its length and the earned amounts always add up to the written premium.
/// (ACT/365F prices a part-year in rating; it is not an earning basis here.) The earned amount is cumulative: a run earns the
/// difference to what earlier runs already earned.
/// </summary>
internal static class Earning
{
    public const string SourceEventType = "EarningRun";
    public const string RuleCode = "EARN-UPR-RELEASE";

    /// <summary>LRC excluding loss component: written premium is credited here (rule WR-PREMIUM), so earning debits it.</summary>
    public const string LrcAccount = "GL-2110";

    /// <summary>Insurance revenue, earned premium (added by migration Earning).</summary>
    public const string RevenueAccount = "GL-4110";

    public const string Done = "DONE";

    /// <summary>Terms shown inline in a run or simulation response; the full detail is paged by fin.Earning.result (BO-FIN-06b).</summary>
    public const int InlineResults = 200;

    public static int TermDays(DateOnly start, DateOnly end) => end.DayNumber - start.DayNumber;

    /// <summary>Days of the term elapsed up to and including <paramref name="asOf"/>, clamped to <c>0..termDays</c>.</summary>
    public static int DaysEarned(DateOnly start, DateOnly end, DateOnly asOf) =>
        Math.Clamp(asOf.DayNumber + 1 - start.DayNumber, 0, TermDays(start, end));

    /// <summary>
    /// The premium that should be earned to date. Written gross is pro-rated by day and rounded with the shared money rounding;
    /// it never exceeds the net written premium, so a mid-term cancellation (a credit) stops the earning at the net, and at the end
    /// of the term the whole net written premium is earned.
    /// </summary>
    public static decimal EarnedToDate(decimal gross, decimal net, int daysEarned, int termDays)
    {
        if (termDays <= 0 || daysEarned <= 0)
        {
            return 0m;
        }

        var prorated = daysEarned >= termDays ? gross : DecimalRounding.Round(gross * daysEarned / termDays, 2, DecimalRounding.DefaultMoneyMode);
        return Math.Max(0m, Math.Min(prorated, net));
    }

    /// <summary>Splits a term's movement over its coverages with the largest-remainder allocator; one unlabelled part when the weights cannot carry it.</summary>
    public static IReadOnlyList<(string? Coverage, decimal Amount)> SplitByCoverage(decimal delta, IReadOnlyList<(string? Coverage, decimal Weight)> components)
    {
        if (components.Count <= 1 || components.Any(c => c.Weight <= 0m))
        {
            return [(components.Count == 1 ? components[0].Coverage : null, delta)];
        }

        var parts = CentAllocation.Allocate(delta, [.. components.Select(c => c.Weight)]);
        return [.. components.Select((c, i) => (c.Coverage, parts[i]))];
    }
}
