using System.Globalization;
using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel;

/// <summary>
/// A rate as a decimal fraction (0.15 = 15 %), e.g. a tax rate, a share or a commission rate. Rates are exact decimals;
/// the 4-decimal-place convention for rates (PLAN §4.1) is applied by the configuration that supplies them, never by
/// silent rounding here. JSON: a decimal string (contracts/events common <c>Decimal</c>).
/// </summary>
[JsonConverter(typeof(RateJsonConverter))]
public readonly record struct Rate(decimal Value) : IComparable<Rate>
{
    /// <summary>The zero rate.</summary>
    public static Rate Zero { get; } = new(0m);

    /// <summary>The rate as a percentage (0.15 → 15 %).</summary>
    public Percentage ToPercentage() => new(Value * 100m);

    /// <summary>Rounds the rate explicitly.</summary>
    public Rate Round(int decimals, MidpointRounding mode) => new(decimal.Round(Value, decimals, mode));

    /// <inheritdoc />
    public int CompareTo(Rate other) => Value.CompareTo(other.Value);

    /// <summary>Invariant decimal text.</summary>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Orders by value.</summary>
    public static bool operator <(Rate left, Rate right) => left.CompareTo(right) < 0;

    /// <summary>Orders by value.</summary>
    public static bool operator >(Rate left, Rate right) => left.CompareTo(right) > 0;

    /// <summary>Orders by value.</summary>
    public static bool operator <=(Rate left, Rate right) => left.CompareTo(right) <= 0;

    /// <summary>Orders by value.</summary>
    public static bool operator >=(Rate left, Rate right) => left.CompareTo(right) >= 0;
}

/// <summary>
/// A percentage (15 = 15 %). Converts exactly to a <see cref="Rate"/> (division by 100 is exact in decimal).
/// JSON: a decimal string.
/// </summary>
[JsonConverter(typeof(PercentageJsonConverter))]
public readonly record struct Percentage(decimal Value) : IComparable<Percentage>
{
    /// <summary>The percentage as a fraction (15 % → 0.15).</summary>
    public Rate ToRate() => new(Value / 100m);

    /// <inheritdoc />
    public int CompareTo(Percentage other) => Value.CompareTo(other.Value);

    /// <summary>Invariant text with a percent sign, e.g. <c>15 %</c>.</summary>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture) + " %";

    /// <summary>Orders by value.</summary>
    public static bool operator <(Percentage left, Percentage right) => left.CompareTo(right) < 0;

    /// <summary>Orders by value.</summary>
    public static bool operator >(Percentage left, Percentage right) => left.CompareTo(right) > 0;

    /// <summary>Orders by value.</summary>
    public static bool operator <=(Percentage left, Percentage right) => left.CompareTo(right) <= 0;

    /// <summary>Orders by value.</summary>
    public static bool operator >=(Percentage left, Percentage right) => left.CompareTo(right) >= 0;
}
