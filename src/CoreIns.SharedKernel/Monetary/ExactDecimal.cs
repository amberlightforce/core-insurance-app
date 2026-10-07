using System.Globalization;
using System.Numerics;

namespace CoreIns.SharedKernel;

/// <summary>
/// Decimal arithmetic that never loses digits silently (ruling D-ARC-27, as D-ARC-10b for the rule engine):
/// multiplication either returns the exact product or throws <see cref="PrecisionLossException"/>; division always
/// rounds to an explicit number of places with an explicit <see cref="MidpointRounding"/> mode, computed exactly
/// (no intermediate 28-digit rounding, so no double rounding).
/// </summary>
public static class ExactDecimal
{
    /// <summary>The exact product, or <see cref="PrecisionLossException"/> when <see cref="decimal"/> cannot hold it.</summary>
    public static decimal Multiply(decimal left, decimal right)
    {
        decimal product;
        try
        {
            product = left * right;
        }
        catch (OverflowException ex)
        {
            throw new PrecisionLossException($"{Text(left)} × {Text(right)} is outside the decimal range.", ex);
        }

        var (lm, ls) = Decompose(left);
        var (rm, rs) = Decompose(right);
        var (pm, ps) = Decompose(product);
        var exact = lm * rm;
        var exactScale = ls + rs;
        var common = Math.Max(exactScale, ps);
        if (exact * BigInteger.Pow(10, common - exactScale) != pm * BigInteger.Pow(10, common - ps))
        {
            throw new PrecisionLossException($"{Text(left)} × {Text(right)} needs more than 28-29 significant digits; round the operands explicitly first.");
        }

        return product;
    }

    /// <summary>
    /// <paramref name="dividend"/> ÷ <paramref name="divisor"/> rounded exactly to <paramref name="decimals"/> places with
    /// <paramref name="mode"/> (all <see cref="MidpointRounding"/> modes, including directed ones).
    /// </summary>
    public static decimal Divide(decimal dividend, decimal divisor, int decimals, MidpointRounding mode)
    {
        if (divisor == 0m)
        {
            throw new DivideByZeroException("Division by zero.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(decimals);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decimals, 28);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown rounding mode.");
        }

        var (am, @as) = Decompose(dividend);
        var (bm, bs) = Decompose(divisor);
        var negative = (am.Sign < 0) != (bm.Sign < 0) && !am.IsZero;
        var numerator = BigInteger.Abs(am) * BigInteger.Pow(10, bs + decimals);
        var denominator = BigInteger.Abs(bm) * BigInteger.Pow(10, @as);
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        if (!remainder.IsZero)
        {
            var twice = remainder * 2;
            var up = mode switch
            {
                MidpointRounding.ToZero => false,
                MidpointRounding.ToNegativeInfinity => negative,
                MidpointRounding.ToPositiveInfinity => !negative,
                MidpointRounding.AwayFromZero => twice >= denominator,
                _ => twice > denominator || (twice == denominator && !quotient.IsEven),
            };
            if (up)
            {
                quotient++;
            }
        }

        return Compose(negative ? -quotient : quotient, decimals);
    }

    private static (BigInteger Mantissa, int Scale) Decompose(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        var mantissa = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
        var scale = (bits[3] >> 16) & 0xFF;
        return (bits[3] < 0 ? -mantissa : mantissa, scale);
    }

    private static decimal Compose(BigInteger mantissa, int scale)
    {
        var digits = BigInteger.Abs(mantissa).ToString(CultureInfo.InvariantCulture).PadLeft(scale + 1, '0');
        var text = scale == 0 ? digits : digits[..^scale] + "." + digits[^scale..];
        if (!decimal.TryParse((mantissa.Sign < 0 ? "-" : string.Empty) + text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var result) || Decompose(result).Mantissa != mantissa)
        {
            throw new PrecisionLossException($"The rounded quotient {text} does not fit a decimal.");
        }

        return result;
    }

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>An arithmetic result would lose digits (D-ARC-27); round explicitly first.</summary>
public sealed class PrecisionLossException : ArithmeticException
{
    /// <summary>Creates the exception.</summary>
    public PrecisionLossException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public PrecisionLossException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public PrecisionLossException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
