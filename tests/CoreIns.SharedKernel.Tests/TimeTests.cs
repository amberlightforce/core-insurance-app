using System.Text.Json;
using CoreIns.SharedKernel.Json;
using FsCheck.Xunit;

namespace CoreIns.SharedKernel.Tests;

public sealed class DateRangeTests
{
    [Property(Arbitrary = [typeof(Generators)])]
    public bool Overlap_is_symmetric_and_matches_intersection(DateRange a, DateRange b) =>
        a.Overlaps(b) == b.Overlaps(a) && a.Overlaps(b) == a.Intersect(b).HasValue;

    [Property(Arbitrary = [typeof(Generators)])]
    public bool Intersection_lies_in_both_and_is_commutative(DateRange a, DateRange b)
    {
        var both = a.Intersect(b);
        return both is null || (a.Contains(both.Value) && b.Contains(both.Value) && both == b.Intersect(a));
    }

    [Property(Arbitrary = [typeof(Generators)])]
    public bool A_day_is_in_the_intersection_iff_it_is_in_both(DateRange a, DateRange b)
    {
        var both = a.Intersect(b);
        for (var day = new BusinessDate(2019, 12, 1); day < new BusinessDate(2032, 1, 1); day = day.AddDays(17))
        {
            if ((a.Contains(day) && b.Contains(day)) != (both?.Contains(day) ?? false))
            {
                return false;
            }
        }

        return true;
    }

    [Property(Arbitrary = [typeof(Generators)])]
    public bool Splitting_is_neutral(DateRange range)
    {
        if (range.End is { } end && end.DaysSince(range.Start) < 2)
        {
            return true;
        }

        var at = range.Start.AddDays(1);
        var (before, after) = range.SplitAt(at);
        return before.Meets(after) && !before.Overlaps(after) && before.Start == range.Start && after.End == range.End
               && range.Contains(before) && range.Contains(after);
    }

    [Fact]
    public void Periods_are_half_open()
    {
        var first = DateRange.Of(new BusinessDate(2026, 1, 1), new BusinessDate(2027, 1, 1));
        var second = DateRange.Open(new BusinessDate(2027, 1, 1));

        first.Contains(new BusinessDate(2026, 1, 1)).ShouldBeTrue();
        first.Contains(new BusinessDate(2027, 1, 1)).ShouldBeFalse();
        first.Overlaps(second).ShouldBeFalse();
        first.Meets(second).ShouldBeTrue();
        first.Days.ShouldBe(365);
        second.Days.ShouldBeNull();
        second.Contains(BusinessDate.MaxValue).ShouldBeTrue();
        first.ToString().ShouldBe("[2026-01-01, 2027-01-01)");
    }

    [Fact]
    public void Empty_or_inverted_periods_are_rejected()
    {
        var day = new BusinessDate(2026, 5, 1);
        Should.Throw<ArgumentException>(() => DateRange.Of(day, day));
        Should.Throw<ArgumentException>(() => DateRange.Of(day, day.AddDays(-1)));
        Should.Throw<ArgumentOutOfRangeException>(() => DateRange.Of(day, day.AddDays(3)).SplitAt(day));
    }

    [Fact]
    public void Periods_serialise_as_contract_date_periods()
    {
        var open = DateRange.Open(new BusinessDate(2026, 1, 1));
        var json = JsonSerializer.Serialize(open, SharedKernelJson.Options);

        json.ShouldBe("{\"from\":\"2026-01-01\",\"to\":null}");
        JsonSerializer.Deserialize<DateRange>(json, SharedKernelJson.Options).ShouldBe(open);
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<DateRange>("{\"from\":\"2026-01-01\"}", SharedKernelJson.Options));
    }
}

public sealed class InstantTests
{
    [Fact]
    public void Instants_have_microsecond_precision_and_rfc3339_text()
    {
        var instant = Instant.FromDateTimeOffset(new DateTimeOffset(2026, 10, 7, 10, 30, 0, TimeSpan.FromHours(3)).AddTicks(1_234_567));

        instant.ToString().ShouldBe("2026-10-07T07:30:00.123456Z");
        Instant.Parse("2026-10-07T07:30:00.123456789Z").ShouldBe(instant);
        Instant.Parse("2026-10-07T07:30:00Z").ToString().ShouldBe("2026-10-07T07:30:00Z");
        JsonSerializer.Serialize(instant, SharedKernelJson.Options).ShouldBe("\"2026-10-07T07:30:00.123456Z\"");
    }

    [Theory]
    [InlineData("2026-10-07T07:30:00+03:00")]
    [InlineData("2026-10-07 07:30:00Z")]
    [InlineData("2026-10-07T07:30:00Z\n")]
    [InlineData("2026-13-07T07:30:00Z")]
    public void Only_utc_rfc3339_is_accepted(string text) => Instant.TryParse(text, out _).ShouldBeFalse();

    [Fact]
    public void Local_kinds_are_refused() =>
        Should.Throw<ArgumentException>(() => Instant.FromUtcDateTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local)));

    [Fact]
    public void A_greek_business_date_starts_two_or_three_hours_earlier_in_utc()
    {
        var athens = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");

        new BusinessDate(2027, 1, 1).StartOfDayIn(athens).ShouldBe(Instant.Parse("2026-12-31T22:00:00Z"));
        new BusinessDate(2026, 7, 1).StartOfDayIn(athens).ShouldBe(Instant.Parse("2026-06-30T21:00:00Z"));
        Instant.Parse("2026-12-31T22:30:00Z").ToBusinessDate(athens).ShouldBe(new BusinessDate(2027, 1, 1));
    }

    [Fact]
    public void Business_dates_are_iso()
    {
        BusinessDate.Parse("2026-02-28").AddDays(1).ToString().ShouldBe("2026-03-01");
        BusinessDate.TryParse("28/02/2026", out _).ShouldBeFalse();
        new BusinessDate(2024, 2, 29).AddYears(1).ShouldBe(new BusinessDate(2025, 2, 28));
    }

    [Fact]
    public void Bitemporal_versions_answer_valid_at_and_known_at()
    {
        var t = (Func<string, Instant>)Instant.Parse;
        var original = new Bitemporal<string>("A", InstantRange.Open(t("2026-01-01T00:00:00Z")), InstantRange.Open(t("2026-01-01T00:00:00Z")));
        var superseded = original.Supersede(t("2026-03-01T00:00:00Z"));
        var correction = new Bitemporal<string>("B", InstantRange.Open(t("2026-02-01T00:00:00Z")), InstantRange.Open(t("2026-03-01T00:00:00Z")));
        var keptHead = new Bitemporal<string>("A", InstantRange.Of(t("2026-01-01T00:00:00Z"), t("2026-02-01T00:00:00Z")), InstantRange.Open(t("2026-03-01T00:00:00Z")));
        Bitemporal<string>[] versions = [superseded, correction, keptHead];

        Bitemporal.VisibleAt(versions, t("2026-02-15T00:00:00Z"), t("2026-02-20T00:00:00Z"))!.Value.ShouldBe("A");
        Bitemporal.VisibleAt(versions, t("2026-02-15T00:00:00Z"), t("2026-03-05T00:00:00Z"))!.Value.ShouldBe("B");
        Bitemporal.VisibleAt(versions, t("2026-01-15T00:00:00Z"), t("2026-03-05T00:00:00Z"))!.Value.ShouldBe("A");
        Bitemporal.VisibleAt(versions, t("2025-12-31T00:00:00Z"), t("2026-03-05T00:00:00Z")).ShouldBeNull();
        superseded.IsCurrent.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => superseded.Supersede(t("2026-04-01T00:00:00Z")));
    }
}
