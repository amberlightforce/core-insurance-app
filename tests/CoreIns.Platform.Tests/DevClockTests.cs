using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using Npgsql;

namespace CoreIns.Platform.Tests;

/// <summary>SL3-PLT-SUPPORT: the unit-level rules of the dev clock (the database and HTTP probes are in the integration tests).</summary>
public sealed class DevClockTests
{
    private sealed class FixedOffset(TimeSpan offset) : IClockOffsetSource
    {
        public TimeSpan Offset { get; set; } = offset;
    }

    [Fact]
    public void A_shiftable_clock_adds_the_shared_offset_and_follows_its_changes()
    {
        var shared = new FixedOffset(TimeSpan.FromDays(2));
        var clock = new ShiftableClock(new ManualClock(Instant.Parse("2026-10-08T00:00:00Z")), TimeSpan.FromHours(1), shared);

        clock.Now.ShouldBe(Instant.Parse("2026-10-10T01:00:00Z"));
        shared.Offset = TimeSpan.FromDays(3);
        clock.Now.ShouldBe(Instant.Parse("2026-10-11T01:00:00Z"));
    }

    [Fact]
    public void A_frozen_shiftable_clock_ignores_the_shared_offset()
    {
        var clock = new ShiftableClock(new ManualClock(Instant.Parse("2026-10-08T00:00:00Z")), TimeSpan.Zero, new FixedOffset(TimeSpan.FromDays(9)));
        clock.Freeze(Instant.Parse("2027-01-01T00:00:00Z"));

        clock.Now.ShouldBe(Instant.Parse("2027-01-01T00:00:00Z"));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(0, 0, false)]
    [InlineData(-1, null, false)]
    [InlineData(null, -1, false)]
    [InlineData(1, -1, false)]
    [InlineData(-1, 48, false)]
    [InlineData(3651, null, false)]
    [InlineData(null, 87601, false)]
    [InlineData(1, null, true)]
    [InlineData(null, 1, true)]
    [InlineData(3650, 0, true)]
    [InlineData(2, 3, true)]
    public void Only_a_positive_bounded_advance_is_valid(int? days, int? hours, bool valid)
    {
        var validator = new AdvanceDevClockValidator();

        validator.Validate(new AdvanceDevClock(new DevClockAdvanceRequest { Days = days, Hours = hours })).IsValid.ShouldBe(valid);
    }

    [Fact]
    public void A_missing_body_is_invalid()
    {
        new AdvanceDevClockValidator().Validate(new AdvanceDevClock(null)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void The_reader_keeps_a_zero_offset_when_the_database_is_unreachable_instead_of_failing_the_clock()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=1;Command Timeout=1");
        var store = new DevClockStore(dataSource);

        store.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void The_cache_is_at_most_a_second_and_the_limit_is_a_hundred_years()
    {
        DevClockStore.CacheTtl.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(1));
        TimeSpan.FromTicks(DevClockStore.MaxOffsetMicros * 10).Days.ShouldBe(36500);
    }
}
