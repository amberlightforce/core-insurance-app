using System.Text.Json;
using CoreIns.SharedKernel.Json;
using FsCheck.Xunit;

namespace CoreIns.SharedKernel.Tests;

public sealed class MoneyTests
{
    private static readonly Currency Eur = Currency.EUR;

    [Property(Arbitrary = [typeof(Generators)])]
    public bool Addition_is_commutative_and_associative(MoneyTriple m) =>
        m.A + m.B == m.B + m.A && (m.A + m.B) + m.C == m.A + (m.B + m.C);

    [Property(Arbitrary = [typeof(Generators)])]
    public bool Subtraction_inverts_addition(MoneyTriple m) => (m.A + m.B) - m.B == m.A;

    [Property(Arbitrary = [typeof(Generators)])]
    public bool Negation_and_zero_are_neutral(Money m) =>
        m + Money.Zero(m.Currency) == m && m + (-m) == Money.Zero(m.Currency) && (-(-m)) == m;

    [Property(Arbitrary = [typeof(Generators)])]
    public bool Rounding_is_idempotent_and_within_half_a_unit(Money m)
    {
        foreach (var mode in new[] { MidpointRounding.ToEven, MidpointRounding.AwayFromZero, MidpointRounding.ToZero })
        {
            var once = m.RoundToMinorUnits(mode);
            var unit = 1m / Pow10(m.Currency.MinorUnits);
            if (once.RoundToMinorUnits(mode) != once || Math.Abs(once.Amount - m.Amount) > unit || !once.IsRoundedToMinorUnits)
            {
                return false;
            }

            if (mode != MidpointRounding.ToZero && Math.Abs(once.Amount - m.Amount) > unit / 2)
            {
                return false;
            }
        }

        return true;
    }

    [Property(Arbitrary = [typeof(Generators)])]
    public bool Multiplication_is_not_rounded(Money m) => (m * 1.0005m).Amount == m.Amount * 1.0005m;

    [Property(Arbitrary = [typeof(Generators)])]
    public bool Json_round_trips_exactly_with_the_amount_as_a_string(Money m)
    {
        var json = JsonSerializer.Serialize(m, SharedKernelJson.Options);
        var back = JsonSerializer.Deserialize<Money>(json, SharedKernelJson.Options);
        using var document = JsonDocument.Parse(json);
        return back == m && back.Amount.Scale == m.Amount.Scale && document.RootElement.GetProperty("amount").ValueKind == JsonValueKind.String;
    }

    [Theory]
    [InlineData("2.5", MidpointRounding.ToEven, "2")]
    [InlineData("3.5", MidpointRounding.ToEven, "4")]
    [InlineData("2.5", MidpointRounding.AwayFromZero, "3")]
    [InlineData("-2.5", MidpointRounding.AwayFromZero, "-3")]
    [InlineData("2.9", MidpointRounding.ToZero, "2")]
    [InlineData("-2.1", MidpointRounding.ToNegativeInfinity, "-3")]
    public void Rounding_mode_is_explicit(string amount, MidpointRounding mode, string expected) =>
        new Money(DecimalText.Parse(amount), Eur).Round(0, mode).Amount.ShouldBe(DecimalText.Parse(expected));

    [Fact]
    public void Minor_units_follow_ISO_4217()
    {
        Money.Of(1.2345m, "EUR").RoundToMinorUnits(MidpointRounding.ToEven).Amount.ShouldBe(1.23m);
        Money.Of(1.5m, "JPY").RoundToMinorUnits(MidpointRounding.ToEven).Amount.ShouldBe(2m);
        Money.Of(1.23456m, "KWD").RoundToMinorUnits(MidpointRounding.AwayFromZero).Amount.ShouldBe(1.235m);
        Currency.FromCode("CLF").MinorUnits.ShouldBe(4);
    }

    [Fact]
    public void Different_currencies_never_mix()
    {
        var eur = Money.Of(10m, "EUR");
        var usd = Money.Of(10m, "USD");

        Should.Throw<CurrencyMismatchException>(() => eur + usd);
        Should.Throw<CurrencyMismatchException>(() => eur - usd);
        Should.Throw<CurrencyMismatchException>(() => eur < usd);
        Should.Throw<CurrencyMismatchException>(() => Money.Sum([eur, usd], Currency.EUR));
    }

    [Fact]
    public void Money_needs_a_currency_and_division_needs_a_divisor()
    {
        Should.Throw<ArgumentException>(() => new Money(1m, default));
        Should.Throw<DivideByZeroException>(() => Money.Of(1m, "EUR") / 0m);
        (Money.Of(10m, "EUR") / 3m).Amount.ShouldBe(10m / 3m);
    }

    [Fact]
    public void Rate_and_percentage_convert_exactly()
    {
        new Percentage(15m).ToRate().ShouldBe(new Rate(0.15m));
        new Rate(0.2m).ToPercentage().ShouldBe(new Percentage(20m));
        (Money.Of(200m, "EUR") * new Rate(0.15m)).Amount.ShouldBe(30.00m);
        JsonSerializer.Serialize(new Rate(0.1500m), SharedKernelJson.Options).ShouldBe("\"0.1500\"");
    }

    [Theory]
    [InlineData("{\"amount\":12.5,\"currency\":\"EUR\"}")]
    [InlineData("{\"amount\":\"12,5\",\"currency\":\"EUR\"}")]
    [InlineData("{\"amount\":\"1e3\",\"currency\":\"EUR\"}")]
    [InlineData("{\"amount\":\"012\",\"currency\":\"EUR\"}")]
    [InlineData("{\"amount\":\"12\",\"currency\":\"XXX\"}")]
    [InlineData("{\"amount\":\"12\"}")]
    [InlineData("{\"amount\":\"12\",\"currency\":\"EUR\",\"extra\":1}")]
    public void Money_json_is_strict(string json) =>
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Money>(json, SharedKernelJson.Options));

    [Fact]
    public void Unknown_currencies_are_rejected() =>
        Should.Throw<FormatException>(() => Currency.FromCode("EURO"));

    private static decimal Pow10(int exponent)
    {
        var value = 1m;
        for (var i = 0; i < exponent; i++)
        {
            value *= 10m;
        }

        return value;
    }
}
