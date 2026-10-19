using CoreIns.Modules.Compliance.Clocks;
using CoreIns.SharedKernel;

namespace CoreIns.IntegrationTests.Compliance;

/// <summary>SL6-CMP-CLOCK: month arithmetic and pause/resume in the legal entity's zone, never UTC (D-SL6-04). No database.</summary>
public sealed class ClockMathTests
{
    private static readonly TimeZoneInfo Athens = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");

    private static string LocalEndOfDay(Instant deadline) =>
        TimeZoneInfo.ConvertTimeFromUtc(deadline.ToUtcDateTime(), Athens).ToString("yyyy-MM-dd HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("2027-01-31T10:00:00Z", "2027-02-28 23:59:59.999999")] // 31 Jan + 1 month clamps to the month end
    [InlineData("2028-01-31T10:00:00Z", "2028-02-29 23:59:59.999999")] // leap year
    [InlineData("2026-10-14T09:30:00Z", "2026-11-14 23:59:59.999999")]
    public void P1M_is_one_calendar_month_with_clamp_due_at_the_end_of_the_Athens_day(string start, string expectedLocal)
    {
        var deadline = ClockMath.Deadline(Instant.Parse(start), IsoPeriod.Parse("P1M"), Athens);

        LocalEndOfDay(deadline).ShouldBe(expectedLocal);
    }

    [Fact]
    public void The_local_date_decides_not_the_UTC_date()
    {
        // 22:30 UTC on 30 Jan is 00:30 on 31 Jan in Athens: one month later is 28 Feb, not 28 Feb from the UTC date (30 Jan -> 28 Feb as well),
        // so use a month whose lengths differ: 30 Apr 22:30Z is 1 May 01:30 local, plus one month is 1 June.
        var deadline = ClockMath.Deadline(Instant.Parse("2026-04-30T22:30:00Z"), IsoPeriod.Parse("P1M"), Athens);

        LocalEndOfDay(deadline).ShouldBe("2026-06-01 23:59:59.999999");
    }

    [Fact]
    public void A_day_across_the_DST_change_ends_at_local_midnight()
    {
        // Europe/Athens: clocks go forward on 2027-03-28 (UTC+2 -> UTC+3). A deadline on that day is 20:59:59.999999Z, not 21:59:59.
        var deadline = ClockMath.EndOfDay(new BusinessDate(2027, 3, 28), Athens);
        deadline.ShouldBe(Instant.Parse("2027-03-28T20:59:59.999999Z"));

        // 2026-10-25 has 25 hours (UTC+3 -> UTC+2): the day ends at 21:59:59.999999Z.
        ClockMath.EndOfDay(new BusinessDate(2026, 10, 25), Athens).ShouldBe(Instant.Parse("2026-10-25T21:59:59.999999Z"));
    }

    [Fact]
    public void A_start_a_month_before_the_DST_change_is_still_due_at_the_end_of_the_local_day()
    {
        var deadline = ClockMath.Deadline(Instant.Parse("2027-02-28T10:00:00Z"), IsoPeriod.Parse("P1M"), Athens);

        LocalEndOfDay(deadline).ShouldBe("2027-03-28 23:59:59.999999");
        deadline.ShouldBe(Instant.Parse("2027-03-28T20:59:59.999999Z"));
    }

    [Fact]
    public void Pausing_moves_the_due_instant_by_the_paused_time()
    {
        var deadline = Instant.Parse("2026-11-14T21:59:59.999999Z");
        var pausedAt = Instant.Parse("2026-10-20T08:00:00Z");
        var resumedAt = Instant.Parse("2026-10-23T08:00:00Z");

        ClockMath.Shift(deadline, pausedAt, resumedAt).ShouldBe(Instant.Parse("2026-11-17T21:59:59.999999Z"));
    }

    [Fact]
    public void Extending_adds_calendar_time_and_ends_at_the_end_of_that_day()
    {
        var deadline = Instant.Parse("2026-11-14T21:59:59.999999Z"); // 14 Nov, end of day in Athens
        var extended = ClockMath.Extend(deadline, IsoPeriod.Parse("P2M"), Athens);

        LocalEndOfDay(extended).ShouldBe("2027-01-14 23:59:59.999999");
    }

    [Theory]
    [InlineData("P1M", 0, 1, 0)]
    [InlineData("P30D", 0, 0, 30)]
    [InlineData("P1Y2M1W3D", 1, 2, 10)]
    public void ISO_periods_parse(string text, int years, int months, int days)
    {
        IsoPeriod.TryParse(text, out var period).ShouldBeTrue();
        period.ShouldBe(new IsoPeriod(years, months, days));
    }

    [Theory]
    [InlineData("")]
    [InlineData("P")]
    [InlineData("1M")]
    [InlineData("PT5H")]
    [InlineData("P-1D")]
    public void Malformed_periods_are_refused(string text) => IsoPeriod.TryParse(text, out _).ShouldBeFalse();
}
