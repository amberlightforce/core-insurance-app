using System.Globalization;
using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel;

/// <summary>
/// An amount of money: a <see cref="decimal"/> plus its <see cref="Currency"/> (contract §3.2.1).
/// <list type="bullet">
/// <item>Arithmetic is exact <see cref="decimal"/> arithmetic and only between amounts of the same currency
/// (<see cref="CurrencyMismatchException"/> otherwise). Nothing is ever rounded implicitly (ADR §2 rule 2, D-ARC-27):
/// a product is exact or throws <see cref="PrecisionLossException"/>; division always takes the precision and the
/// <see cref="MidpointRounding"/> mode; callers round explicitly with <see cref="Round"/> or
/// <see cref="RoundToMinorUnits"/>, naming what configuration prescribes.</item>
/// <item>JSON: <c>{"amount": "&lt;decimal string&gt;", "currency": "&lt;ISO 4217&gt;"}</c>, as contracts/events
/// common <c>Money</c>; amounts are never JSON numbers.</item>
/// </list>
/// </summary>
[JsonConverter(typeof(MoneyJsonConverter))]
public readonly record struct Money : IComparable<Money>
{
    /// <summary>Creates an amount; the currency must be a real currency.</summary>
    public Money(decimal amount, Currency currency)
    {
        if (currency.IsDefault)
        {
            throw new ArgumentException("Money needs a currency.", nameof(currency));
        }

        Amount = amount;
        Currency = currency;
    }

    /// <summary>The amount, exactly as computed (no implicit rounding).</summary>
    public decimal Amount { get; }

    /// <summary>The ISO 4217 currency.</summary>
    public Currency Currency { get; }

    /// <summary>Zero in the given currency.</summary>
    public static Money Zero(Currency currency) => new(0m, currency);

    /// <summary>Shorthand for an amount in a currency given by code.</summary>
    public static Money Of(decimal amount, string currencyCode) => new(amount, Currency.FromCode(currencyCode));

    /// <summary>True when the amount is zero.</summary>
    public bool IsZero => Amount == 0m;

    /// <summary>True when the amount is below zero.</summary>
    public bool IsNegative => Amount < 0m;

    /// <summary>True when the amount is above zero.</summary>
    public bool IsPositive => Amount > 0m;

    /// <summary>Number of decimal places the amount carries (its <see cref="decimal"/> scale).</summary>
    public int Scale => Amount.Scale;

    /// <summary>True when the amount has no more decimal places than the currency's minor unit.</summary>
    public bool IsRoundedToMinorUnits => decimal.Round(Amount, Currency.MinorUnits, MidpointRounding.ToZero) == Amount;

    /// <summary>Sum of same-currency amounts; zero in <paramref name="currency"/> when empty.</summary>
    public static Money Sum(IEnumerable<Money> amounts, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        var total = Zero(currency);
        foreach (var amount in amounts)
        {
            total += amount;
        }

        return total;
    }

    /// <summary>Same-currency addition.</summary>
    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    /// <summary>Same-currency subtraction.</summary>
    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    /// <summary>
    /// Multiplies by a factor. The result is exact and not rounded; a product that <see cref="decimal"/> cannot hold
    /// exactly throws <see cref="PrecisionLossException"/> instead of losing digits (D-ARC-27).
    /// </summary>
    public Money Multiply(decimal factor) => new(ExactDecimal.Multiply(Amount, factor), Currency);

    /// <summary>Applies a rate (e.g. a tax rate): exact, not rounded, or <see cref="PrecisionLossException"/>.</summary>
    public Money Multiply(Rate rate) => Multiply(rate.Value);

    /// <summary>
    /// Divides by a non-zero divisor, rounding the exact quotient to <paramref name="decimals"/> places with
    /// <paramref name="mode"/>. Division always names its rounding (D-ARC-27): there is no unrounded money division.
    /// </summary>
    public Money Divide(decimal divisor, int decimals, MidpointRounding mode) => new(ExactDecimal.Divide(Amount, divisor, decimals, mode), Currency);

    /// <summary>The amount with the opposite sign.</summary>
    public Money Negate() => new(-Amount, Currency);

    /// <summary>The absolute amount.</summary>
    public Money Abs() => new(Math.Abs(Amount), Currency);

    /// <summary>
    /// Rounds to <paramref name="decimals"/> places with the given midpoint rule. Both are explicit: they come from
    /// configuration (currency rules, tax line rules), never from a default.
    /// </summary>
    public Money Round(int decimals, MidpointRounding mode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decimals);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decimals, 28);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown rounding mode.");
        }

        return new Money(decimal.Round(Amount, decimals, mode), Currency);
    }

    /// <summary>Rounds to the currency's ISO 4217 minor units with the given midpoint rule.</summary>
    public Money RoundToMinorUnits(MidpointRounding mode) => Round(Currency.MinorUnits, mode);

    /// <inheritdoc />
    public int CompareTo(Money other)
    {
        EnsureSameCurrency(other);
        return Amount.CompareTo(other.Amount);
    }

    /// <summary>Invariant text, e.g. <c>1234.50 EUR</c>.</summary>
    public override string ToString() => $"{Amount.ToString(CultureInfo.InvariantCulture)} {Currency.Code}";

    private void EnsureSameCurrency(Money other)
    {
        if (Currency != other.Currency)
        {
            throw new CurrencyMismatchException(Currency, other.Currency);
        }
    }

    /// <summary>Same-currency addition.</summary>
    public static Money operator +(Money left, Money right) => left.Add(right);

    /// <summary>Same-currency subtraction.</summary>
    public static Money operator -(Money left, Money right) => left.Subtract(right);

    /// <summary>The amount with the opposite sign.</summary>
    public static Money operator -(Money value) => value.Negate();

    /// <summary>Multiplies by a factor (exact; <see cref="PrecisionLossException"/> instead of lost digits).</summary>
    public static Money operator *(Money left, decimal right) => left.Multiply(right);

    /// <summary>Multiplies by a factor (exact; <see cref="PrecisionLossException"/> instead of lost digits).</summary>
    public static Money operator *(decimal left, Money right) => right.Multiply(left);

    /// <summary>Applies a rate (exact; <see cref="PrecisionLossException"/> instead of lost digits).</summary>
    public static Money operator *(Money left, Rate right) => left.Multiply(right);

    /// <summary>Same-currency comparison.</summary>
    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    /// <summary>Same-currency comparison.</summary>
    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    /// <summary>Same-currency comparison.</summary>
    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    /// <summary>Same-currency comparison.</summary>
    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;
}

/// <summary>Raised when amounts of two different currencies meet in one operation (money is never converted implicitly).</summary>
public sealed class CurrencyMismatchException : InvalidOperationException
{
    /// <summary>Creates the exception for two currencies.</summary>
    public CurrencyMismatchException(Currency left, Currency right)
        : base($"Cannot combine {left} and {right} amounts; convert explicitly with an FX rate first.")
    {
        Left = left;
        Right = right;
    }

    /// <summary>Creates the exception with a message.</summary>
    public CurrencyMismatchException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public CurrencyMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public CurrencyMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The left-hand currency.</summary>
    public Currency Left { get; }

    /// <summary>The right-hand currency.</summary>
    public Currency Right { get; }
}
