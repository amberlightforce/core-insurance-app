using System;
using System.Linq;
using Shouldly;
using Xunit;

namespace CoreIns.Rules.Tests;

/// <summary>NX-EC-01: the one mode-to-MidpointRounding mapping every money module uses, incl. negative midpoints.</summary>
public sealed class SharedRoundingPolicyTests
{
    // value (2 places) -> expected per mode; midpoints at the third place.
    [Theory]
    [InlineData(RoundingMode.HalfUp, "2.345", "2.35", "-2.345", "-2.35")]
    [InlineData(RoundingMode.HalfEven, "2.345", "2.34", "-2.345", "-2.34")]
    [InlineData(RoundingMode.HalfEven, "2.355", "2.36", "-2.355", "-2.36")]
    [InlineData(RoundingMode.HalfDown, "2.345", "2.34", "-2.345", "-2.34")]
    [InlineData(RoundingMode.Down, "2.349", "2.34", "-2.349", "-2.34")]
    [InlineData(RoundingMode.Up, "2.341", "2.35", "-2.341", "-2.35")]
    [InlineData(RoundingMode.Ceiling, "2.341", "2.35", "-2.349", "-2.34")]
    [InlineData(RoundingMode.Floor, "2.349", "2.34", "-2.341", "-2.35")]
    public void RoundTo_is_symmetric_or_directed_as_the_mode_says(RoundingMode mode, string positive, string expectedPositive, string negative, string expectedNegative)
    {
        DecimalRounding.RoundTo(decimal.Parse(positive, System.Globalization.CultureInfo.InvariantCulture), 2, mode)
            .ShouldBe(decimal.Parse(expectedPositive, System.Globalization.CultureInfo.InvariantCulture));
        DecimalRounding.RoundTo(decimal.Parse(negative, System.Globalization.CultureInfo.InvariantCulture), 2, mode)
            .ShouldBe(decimal.Parse(expectedNegative, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(RoundingMode.HalfUp, MidpointRounding.AwayFromZero)]
    [InlineData(RoundingMode.HalfEven, MidpointRounding.ToEven)]
    [InlineData(RoundingMode.Down, MidpointRounding.ToZero)]
    [InlineData(RoundingMode.Ceiling, MidpointRounding.ToPositiveInfinity)]
    [InlineData(RoundingMode.Floor, MidpointRounding.ToNegativeInfinity)]
    public void ToMidpoint_maps_each_mode(RoundingMode mode, MidpointRounding expected) =>
        DecimalRounding.ToMidpoint(mode, 1m).ShouldBe(expected);

    [Fact]
    public void Up_follows_the_sign_and_unsupported_modes_fail_closed()
    {
        DecimalRounding.ToMidpoint(RoundingMode.Up, 1m).ShouldBe(MidpointRounding.ToPositiveInfinity);
        DecimalRounding.ToMidpoint(RoundingMode.Up, -1m).ShouldBe(MidpointRounding.ToNegativeInfinity);
        Should.Throw<ArgumentOutOfRangeException>(() => DecimalRounding.ToMidpoint(RoundingMode.HalfDown, 1m));
        Should.Throw<ArgumentOutOfRangeException>(() => DecimalRounding.ToMidpoint((RoundingMode)99, 1m));
        Should.Throw<ArgumentOutOfRangeException>(() => DecimalRounding.RoundTo(1m, 2, (RoundingMode)99));
    }

    [Fact]
    public void The_platform_default_is_commercial_half_up_and_RoundTo_agrees_with_Round()
    {
        DecimalRounding.DefaultMoneyMode.ShouldBe(RoundingMode.HalfUp);
        foreach (var mode in Enum.GetValues<RoundingMode>())
        {
            foreach (var v in new[] { 2.345m, -2.345m, 0.005m, -0.005m, 7m })
            {
                DecimalRounding.RoundTo(v, 2, mode).ShouldBe(DecimalRounding.Round(v, 2, mode));
            }
        }
    }
}
