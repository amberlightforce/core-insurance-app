using CoreIns.Modules.Claims.Domain;

namespace CoreIns.ArchitectureTests;

/// <summary>
/// NX-EC-02: pins the money results that changed when the Claims sites moved from banker's rounding (ToEven) to the platform's
/// commercial default (half away from zero). Only exact midpoints differ.
/// </summary>
public sealed class MoneyRoundingPinTests
{
    [Theory]
    [InlineData("0.125", "0.13")]
    [InlineData("0.135", "0.14")]
    [InlineData("2.675", "2.68")]
    [InlineData("-0.125", "-0.13")]
    [InlineData("10.004", "10.00")]
    public void Claim_money_rounds_midpoints_away_from_zero(string input, string expected)
    {
        var amount = decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture);
        ClaimMoney.Eur(amount).Amount.ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        ClaimMoney.Of(amount, "EUR").Amount.ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        SetHashing.Fixed(amount).ShouldBe(expected);
    }
}
