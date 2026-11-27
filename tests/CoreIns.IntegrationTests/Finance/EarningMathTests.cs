using CoreIns.Modules.Finance.Domain;

namespace CoreIns.IntegrationTests.Finance;

/// <summary>BO-FIN-06a: the pure earning arithmetic (pro rata temporis by day, TERM_RATIO).</summary>
public sealed class EarningMathTests
{
    private static decimal Earned(string start, string end, string asOf, decimal gross, decimal? net = null)
    {
        var (s, e, a) = (DateOnly.Parse(start), DateOnly.Parse(end), DateOnly.Parse(asOf));
        return Earning.EarnedToDate(gross, net ?? gross, Earning.DaysEarned(s, e, a), Earning.TermDays(s, e));
    }

    [Fact]
    public void Monthly_proration_earns_days_over_term_days_rounded_half_up()
    {
        Earning.TermDays(new DateOnly(2026, 11, 1), new DateOnly(2027, 11, 1)).ShouldBe(365);
        Earned("2026-11-01", "2027-11-01", "2026-11-30", 1200m).ShouldBe(98.63m); // 1200 x 30 / 365
        Earned("2026-11-01", "2027-11-01", "2026-12-31", 1200m).ShouldBe(200.55m); // 61 days: 200.547...
        Earned("2026-11-01", "2027-11-01", "2026-10-31", 1200m).ShouldBe(0m); // before the term
    }

    [Fact]
    public void A_term_spanning_29_February_has_366_days_and_is_earned_in_full_at_its_end()
    {
        Earning.TermDays(new DateOnly(2028, 1, 1), new DateOnly(2029, 1, 1)).ShouldBe(366);
        Earned("2028-01-01", "2029-01-01", "2028-02-29", 366m).ShouldBe(60m); // 31 + 29 days
        Earned("2028-01-01", "2029-01-01", "2028-12-31", 366m).ShouldBe(366m);
        Earned("2028-01-01", "2029-01-01", "2029-06-30", 366m).ShouldBe(366m); // clamped after the term
    }

    [Fact]
    public void Days_are_calendar_dates_so_a_daylight_saving_change_does_not_shift_the_earning()
    {
        // 2026-03-29 is the Athens spring-forward Sunday (a 23-hour day); it earns like any other day.
        Earning.DaysEarned(new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1), new DateOnly(2026, 3, 29)).ShouldBe(29);
        Earned("2026-03-01", "2026-04-01", "2026-03-29", 310m).ShouldBe(290m);
        Earned("2026-10-01", "2026-11-01", "2026-10-25", 310m).ShouldBe(250m); // 2026-10-25 is the 25-hour day
    }

    [Fact]
    public void A_mid_term_cancellation_caps_the_earning_at_the_net_written_premium()
    {
        // 1200 written, cancelled after 90 of 365 days: the credit leaves a net of 295.89 which is all there is to earn.
        const decimal net = 295.89m;
        Earned("2026-11-01", "2027-11-01", "2027-01-29", 1200m, net).ShouldBe(295.89m);
        Earned("2026-11-01", "2027-11-01", "2026-12-31", 1200m, net).ShouldBe(200.55m); // before the cancel date only the pro rata part
        Earned("2026-11-01", "2027-11-01", "2027-10-31", 1200m, net).ShouldBe(net);
        Earned("2026-11-01", "2027-11-01", "2027-10-31", 1200m, 0m).ShouldBe(0m); // fully refunded
    }

    [Fact]
    public void The_earned_amounts_of_consecutive_runs_add_up_to_the_written_premium_at_term_end()
    {
        decimal posted = 0m;
        foreach (var asOf in new[] { "2026-11-30", "2026-12-31", "2027-01-31", "2027-06-30", "2027-10-31" })
        {
            posted += Earned("2026-11-01", "2027-11-01", asOf, 777.77m) - posted;
        }

        posted.ShouldBe(777.77m);
    }

    [Fact]
    public void A_term_movement_is_split_over_coverages_with_the_cents_adding_up()
    {
        var parts = Earning.SplitByCoverage(100.01m, [("MTPL", 250m), ("OD", 115m), ("GLASS", 35m)]);
        parts.Sum(p => p.Amount).ShouldBe(100.01m);
        parts.Count.ShouldBe(3);
        Earning.SplitByCoverage(-10.00m, [("A", 1m), ("B", 2m)]).Sum(p => p.Amount).ShouldBe(-10.00m);
        var single = Earning.SplitByCoverage(5m, [("A", 1m), ("B", 0m)]).Single(); // a non-positive weight cannot carry a share
        single.Coverage.ShouldBeNull();
        single.Amount.ShouldBe(5m);
    }
}
