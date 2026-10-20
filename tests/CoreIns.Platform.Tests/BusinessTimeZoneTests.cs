using CoreIns.Platform.Time;

namespace CoreIns.Platform.Tests;

/// <summary>NX-EC-03: the business time zone never falls back to UTC.</summary>
public sealed class BusinessTimeZoneTests
{
    [Fact]
    public void Athens_resolves_to_a_real_zone_not_utc()
    {
        var zone = BusinessTimeZone.Athens;

        Assert.NotEqual(TimeZoneInfo.Utc.BaseUtcOffset, zone.BaseUtcOffset);
        Assert.Equal(TimeSpan.FromHours(2), zone.BaseUtcOffset);
    }

    [Fact]
    public void An_unknown_zone_throws_instead_of_falling_back()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BusinessTimeZone.Resolve("Mars/Olympus_Mons"));

        Assert.Contains("Mars/Olympus_Mons", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_startup_check_passes_when_tz_data_is_present()
    {
        var check = new BusinessTimeZoneStartupCheck();

        await check.StartingAsync(CancellationToken.None);
    }
}
