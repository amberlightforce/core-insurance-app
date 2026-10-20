using CoreIns.Modules.Channels.Domain;
using CoreIns.Modules.Compliance.Clocks;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;

namespace CoreIns.IntegrationTests.Policy;

/// <summary>NX-EC-04: temporal edges (29 Feb, DST-spanning terms, 23/25 h days, 366-day ACT/365F, month-end and DST deadlines). No database.</summary>
public sealed class TemporalEdgeTests
{
    private static readonly TimeZoneInfo Athens = BusinessTimeZone.Athens;

    private static string Local(Instant i) =>
        TimeZoneInfo.ConvertTimeFromUtc(i.ToUtcDateTime(), Athens).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    // ---- PolicyTime.AnnualEnd ----

    [Theory]
    [InlineData("2028-02-29T10:00:00Z", "2029-02-28 12:00:00")] // 29 Feb inception: next year has no 29 Feb, clamps to 28 Feb
    [InlineData("2027-02-28T10:00:00Z", "2028-02-28 12:00:00")]
    [InlineData("2026-12-31T22:00:00Z", "2028-01-01 00:00:00")] // local midnight 1 Jan 2027 -> 1 Jan 2028 local
    public void AnnualEnd_keeps_local_time_and_clamps_29_Feb(string start, string expectedLocal)
    {
        Local(PolicyTime.AnnualEnd(Instant.Parse(start), Athens)).ShouldBe(expectedLocal);
    }

    [Fact]
    public void AnnualEnd_term_spanning_a_leap_day_is_366_days()
    {
        var start = Instant.Parse("2027-06-01T09:00:00Z");
        var end = PolicyTime.AnnualEnd(start, Athens);

        DayCount.Days(start, end, Athens).ShouldBe(366);
        Local(end).ShouldBe("2028-06-01 12:00:00");
    }

    [Fact]
    public void AnnualEnd_keeps_local_wall_time_across_DST_offset_changes()
    {
        // 12:00 EET (UTC+2) in October 2025 -> 12:00 local in October 2026 is still EEST until the 25th, so offsets differ by year.
        var end = PolicyTime.AnnualEnd(Instant.Parse("2025-10-26T10:00:00Z"), Athens); // 12:00 EET (after the 26 Oct 2025 change)
        Local(end).ShouldBe("2026-10-26 12:00:00");
        end.ShouldBe(Instant.Parse("2026-10-26T10:00:00Z"));

        // Summer to summer: 12:00 EEST stays 12:00 local.
        PolicyTime.AnnualEnd(Instant.Parse("2026-07-01T09:00:00Z"), Athens).ShouldBe(Instant.Parse("2027-07-01T09:00:00Z"));

        // Start in winter just before the spring change next year: 12:00 EET on 28 Mar 2026 -> 12:00 local on 28 Mar 2027 (EET, change is that night's 03:00).
        Local(PolicyTime.AnnualEnd(Instant.Parse("2026-03-28T10:00:00Z"), Athens)).ShouldBe("2027-03-28 12:00:00");
    }

    [Fact]
    public void AnnualEnd_landing_in_the_spring_gap_moves_to_the_first_valid_hour()
    {
        // 03:30 local on 2026-03-28 (EET) + 1 year = 2027-03-28 03:30, which does not exist (03:00 jumps to 04:00).
        var start = Instant.Parse("2026-03-28T01:30:00Z");
        Local(start).ShouldBe("2026-03-28 03:30:00");

        var end = PolicyTime.AnnualEnd(start, Athens);

        Local(end).ShouldBe("2027-03-28 04:30:00");
        end.ShouldBeGreaterThan(start);
    }

    [Fact]
    public void AnnualEnd_landing_in_the_autumn_overlap_stays_on_the_same_local_day()
    {
        // 03:30 local on 25 Oct 2025 + 1 year = 2026-10-25 03:30, ambiguous (repeated hour): either mapping is on that local day.
        var start = Instant.Parse("2025-10-25T00:30:00Z");
        var end = PolicyTime.AnnualEnd(start, Athens);

        end.ToBusinessDate(Athens).ShouldBe(new BusinessDate(2026, 10, 25));
        end.ShouldBeGreaterThan(start);
    }

    // ---- PolicyTime.EndOf ----

    [Theory]
    [InlineData(2027, 3, 28, "2027-03-28T20:59:59.999999Z")] // 23 h day
    [InlineData(2026, 10, 25, "2026-10-25T21:59:59.999999Z")] // 25 h day
    [InlineData(2028, 2, 29, "2028-02-29T21:59:59.999999Z")] // leap day
    public void EndOf_is_the_last_microsecond_of_the_local_day(int y, int m, int d, string expected)
    {
        PolicyTime.EndOf(new BusinessDate(y, m, d), Athens).ShouldBe(Instant.Parse(expected));
    }

    // ---- Cancellation on a 23 h / 25 h day ----

    [Fact]
    public void Cancellation_day_count_ignores_the_hours_of_a_23_hour_day()
    {
        var from = new BusinessDate(2027, 3, 1).StartOfDayIn(Athens);
        var to = PolicyTime.AnnualEnd(from, Athens);
        var lateOnTheShortDay = Instant.Parse("2027-03-28T20:30:00Z"); // 23:30 local on the 23 h day
        var dayStart = new BusinessDate(2027, 3, 28).StartOfDayIn(Athens);
        var nextStart = new BusinessDate(2027, 3, 29).StartOfDayIn(Athens);

        (nextStart - dayStart).ShouldBe(TimeSpan.FromHours(23));
        DayCount.Days(from, dayStart, Athens).ShouldBe(27);
        DayCount.Days(from, lateOnTheShortDay, Athens).ShouldBe(27);
        DayCount.Days(from, nextStart, Athens).ShouldBe(28);
        DayCount.Days(from, to, Athens).ShouldBe(366); // 1 Mar 2027 -> 1 Mar 2028 spans 29 Feb 2028
    }

    [Fact]
    public void Cancellation_day_count_ignores_the_hours_of_a_25_hour_day()
    {
        var from = new BusinessDate(2026, 10, 1).StartOfDayIn(Athens);
        var dayStart = new BusinessDate(2026, 10, 25).StartOfDayIn(Athens);
        var nextStart = new BusinessDate(2026, 10, 26).StartOfDayIn(Athens);
        var lateOnTheLongDay = nextStart.Minus(TimeSpan.FromSeconds(1));

        (nextStart - dayStart).ShouldBe(TimeSpan.FromHours(25));
        DayCount.Days(from, dayStart, Athens).ShouldBe(24);
        DayCount.Days(from, lateOnTheLongDay, Athens).ShouldBe(24);
        DayCount.Days(from, nextStart, Athens).ShouldBe(25);
    }

    [Fact]
    public void Day_count_uses_the_local_date_not_the_UTC_date()
    {
        var inception = Instant.Parse("2027-03-28T21:30:00Z"); // 00:30 EEST on 29 Mar; the UTC date is still 28 Mar
        DayCount.Date(inception, Athens).ShouldBe(new DateOnly(2027, 3, 29));
        DayCount.Days(inception, inception.Plus(TimeSpan.FromHours(1)), Athens).ShouldBe(0);
        DayCount.Days(inception, inception.Plus(TimeSpan.FromHours(24)), Athens).ShouldBe(1);
    }

    // ---- 366-day ACT/365F ----

    [Fact]
    public void Act365F_full_366_day_term_is_capped_at_one()
    {
        new ReferenceProration().Fraction(DayCountConvention.Act365F, 366, 366).ShouldBe(new ProrationFraction(1, 1));
    }

    [Theory]
    [InlineData(365, 366)]
    [InlineData(1, 366)]
    [InlineData(0, 366)]
    public void Act365F_partial_leap_term_uses_a_365_basis(int days, int termDays)
    {
        var f = new ReferenceProration().Fraction(DayCountConvention.Act365F, days, termDays);

        f.ShouldBe(new ProrationFraction(days, 365));
        ((decimal)f.Numerator / f.Denominator).ShouldBeLessThanOrEqualTo(1m);
    }

    [Fact]
    public void TermRatio_uses_the_term_length_as_the_basis_in_a_leap_term()
    {
        new ReferenceProration().Fraction(DayCountConvention.TermRatio, 183, 366).ShouldBe(new ProrationFraction(183, 366));
    }

    [Theory]
    [InlineData(-1, 366)]
    [InlineData(367, 366)]
    [InlineData(1, 0)]
    public void Proration_rejects_days_outside_the_term(int days, int termDays)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ReferenceProration().Fraction(DayCountConvention.Act365F, days, termDays));
    }

    // ---- Statutory deadlines ----

    [Theory]
    [InlineData("2027-03-31T10:00:00Z", "2027-04-30 23:59:59")] // 31 Mar + 1 month clamps to 30 Apr
    [InlineData("2027-08-31T10:00:00Z", "2027-09-30 23:59:59")]
    [InlineData("2027-12-31T10:00:00Z", "2028-01-31 23:59:59")]
    [InlineData("2028-01-30T10:00:00Z", "2028-02-29 23:59:59")] // leap
    [InlineData("2027-01-30T10:00:00Z", "2027-02-28 23:59:59")]
    public void One_month_deadline_clamps_at_month_end(string start, string expectedLocal)
    {
        Local(ClockMath.Deadline(Instant.Parse(start), IsoPeriod.Parse("P1M"), Athens)).ShouldBe(expectedLocal);
    }

    [Fact]
    public void Deadline_that_falls_on_a_DST_day_ends_at_the_local_day_end_whatever_its_length()
    {
        ClockMath.Deadline(Instant.Parse("2027-02-28T10:00:00Z"), IsoPeriod.Parse("P1M"), Athens)
            .ShouldBe(Instant.Parse("2027-03-28T20:59:59.999999Z")); // 23 h day
        ClockMath.Deadline(Instant.Parse("2026-09-25T10:00:00Z"), IsoPeriod.Parse("P1M"), Athens)
            .ShouldBe(Instant.Parse("2026-10-25T21:59:59.999999Z")); // 25 h day
    }

    [Fact]
    public void Day_period_deadline_across_the_spring_change_counts_calendar_days()
    {
        Local(ClockMath.Deadline(Instant.Parse("2027-03-20T10:00:00Z"), IsoPeriod.Parse("P30D"), Athens)).ShouldBe("2027-04-19 23:59:59");
    }

    [Fact]
    public void Warning_instant_is_local_midnight_of_the_warning_day_across_DST()
    {
        // Deadline 30 Mar 2027 (EEST); 7 days before is 23 Mar (EET): 00:00 local = 22 Mar 22:00Z.
        ClockMath.WarnAt(ClockMath.EndOfDay(new BusinessDate(2027, 3, 30), Athens), "P7D", Athens).ShouldBe(Instant.Parse("2027-03-22T22:00:00Z"));

        // Deadline 2 Apr 2027; 7 days before is 26 Mar (EET): 00:00 local = 25 Mar 22:00Z.
        ClockMath.WarnAt(ClockMath.EndOfDay(new BusinessDate(2027, 4, 2), Athens), "P7D", Athens).ShouldBe(Instant.Parse("2027-03-25T22:00:00Z"));
    }

    [Fact]
    public void Warning_with_an_unparseable_or_missing_period_yields_no_warning()
    {
        ClockMath.WarnAt(Instant.Parse("2027-03-30T10:00:00Z"), "nonsense", Athens).ShouldBeNull();
        ClockMath.WarnAt(Instant.Parse("2027-03-30T10:00:00Z"), null, Athens).ShouldBeNull();
    }

    // ---- Withdrawal window ----

    [Fact]
    public void Withdrawal_window_over_the_spring_change_closes_at_local_midnight()
    {
        var concluded = Instant.Parse("2027-03-20T08:00:00Z");
        var deadline = WithdrawalWindow.Deadline(concluded, 14, Athens);

        Local(deadline).ShouldBe("2027-04-03 23:59:59");
        WithdrawalWindow.IsOpen(concluded, deadline, 14, Athens).ShouldBeTrue();
        WithdrawalWindow.IsOpen(concluded, deadline.Plus(TimeSpan.FromSeconds(1)), 14, Athens).ShouldBeFalse();
    }

    [Fact]
    public void Withdrawal_window_whose_last_day_is_the_23_hour_day()
    {
        var concluded = Instant.Parse("2027-03-14T12:00:00Z");

        WithdrawalWindow.Deadline(concluded, 14, Athens).ShouldBe(Instant.Parse("2027-03-28T20:59:59Z"));
    }

    [Fact]
    public void Withdrawal_concluded_just_after_local_midnight_counts_that_local_day_as_day_zero()
    {
        var concluded = Instant.Parse("2026-09-30T21:10:00Z"); // 00:10 local on 1 Oct

        Local(WithdrawalWindow.Deadline(concluded, 14, Athens)).ShouldBe("2026-10-15 23:59:59");
    }

    [Fact]
    public void Withdrawal_window_started_on_29_Feb()
    {
        var concluded = Instant.Parse("2028-02-29T10:00:00Z");

        Local(WithdrawalWindow.Deadline(concluded, 1, Athens)).ShouldBe("2028-03-01 23:59:59");
        Local(WithdrawalWindow.Deadline(concluded, 0, Athens)).ShouldBe("2028-02-29 23:59:59");
    }

    [Fact]
    public void Withdrawal_window_at_year_end_crosses_the_year()
    {
        Local(WithdrawalWindow.Deadline(Instant.Parse("2026-12-25T10:00:00Z"), 14, Athens)).ShouldBe("2027-01-08 23:59:59");
    }
}
