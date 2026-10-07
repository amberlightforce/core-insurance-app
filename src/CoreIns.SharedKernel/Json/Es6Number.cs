using System.Globalization;
using System.Numerics;
using System.Text;

namespace CoreIns.SharedKernel.Json;

/// <summary>
/// ECMAScript <c>Number.prototype.toString</c> serialisation of JSON numbers, as RFC 8785 §3.2.2.3 requires, computed
/// with exact integer arithmetic only. The number text is rounded to the nearest IEEE 754 binary64 value
/// (round-half-even), and the shortest decimal that rounds back to that value (closest one on ties, then even) is
/// printed in ES notation. No <c>double</c> is used, so the COREINS001 floating-point ban holds in production code.
/// </summary>
internal static class Es6Number
{
    private const int MantissaBits = 52;
    private const int MinExponent = -1074;
    private const int MaxExponent = 971;

    private static readonly BigInteger Hidden = BigInteger.One << MantissaBits;
    private static readonly BigInteger Limit = BigInteger.One << (MantissaBits + 1);

    /// <summary>Canonical text of a JSON number literal (RFC 8259 grammar, already validated by the parser).</summary>
    public static string Canonicalize(string literal)
    {
        var (negative, digits, exponent10) = Decompose(literal);
        if (digits.IsZero)
        {
            return "0";
        }

        var (mantissa, exponent2) = ToBinary64(digits, exponent10);
        if (mantissa.IsZero)
        {
            return "0";
        }

        var text = Format(mantissa, exponent2);
        return negative ? "-" + text : text;
    }

    /// <summary>Canonical text of a binary64 value given by its IEEE 754 bit pattern (used by the RFC 8785 test vectors).</summary>
    public static string FormatBits(ulong bits)
    {
        var negative = (bits >> 63) != 0;
        var exponentField = (int)((bits >> MantissaBits) & 0x7FF);
        var fraction = bits & ((1UL << MantissaBits) - 1);
        if (exponentField == 0x7FF)
        {
            throw new CanonicalJsonException("NaN and Infinity are not JSON numbers (RFC 8785 §3.2.2.3).");
        }

        BigInteger mantissa = exponentField == 0 ? fraction : fraction | (1UL << MantissaBits);
        var exponent = exponentField == 0 ? MinExponent : exponentField - 1075;
        if (mantissa.IsZero)
        {
            return "0";
        }

        var text = Format(mantissa, exponent);
        return negative ? "-" + text : text;
    }

    /// <summary>The nearest binary64 (as mantissa and exponent, value = m·2^e) of a JSON number literal.</summary>
    public static (BigInteger Mantissa, int Exponent) Parse(string literal)
    {
        var (_, digits, exponent10) = Decompose(literal);
        return digits.IsZero ? (BigInteger.Zero, 0) : ToBinary64(digits, exponent10);
    }

    private static (bool Negative, BigInteger Digits, int Exponent10) Decompose(string literal)
    {
        var index = 0;
        var negative = literal.Length > 0 && literal[0] == '-';
        if (negative)
        {
            index++;
        }

        var digits = new StringBuilder(literal.Length);
        var exponent10 = 0;
        var seenPoint = false;
        for (; index < literal.Length; index++)
        {
            var c = literal[index];
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
                if (seenPoint)
                {
                    exponent10--;
                }
            }
            else if (c == '.' && !seenPoint)
            {
                seenPoint = true;
            }
            else if (c is 'e' or 'E')
            {
                break;
            }
            else
            {
                throw new CanonicalJsonException($"'{literal}' is not a JSON number.");
            }
        }

        if (digits.Length == 0)
        {
            throw new CanonicalJsonException($"'{literal}' is not a JSON number.");
        }

        if (index < literal.Length)
        {
            var exponentText = literal[(index + 1)..];
            if (!BigInteger.TryParse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var explicitExponent))
            {
                throw new CanonicalJsonException($"'{literal}' is not a JSON number.");
            }

            // Anything beyond ±100000 is far outside binary64 range; clamp to keep the arithmetic bounded.
            explicitExponent = BigInteger.Clamp(explicitExponent, -100_000, 100_000);
            exponent10 += (int)explicitExponent;
        }

        var value = BigInteger.Parse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
        return (negative, value, exponent10);
    }

    private static (BigInteger Mantissa, int Exponent) ToBinary64(BigInteger digits, int exponent10)
    {
        var magnitude = DecimalDigits(digits) - 1 + exponent10;
        if (magnitude > 309)
        {
            throw new CanonicalJsonException("The number is outside the IEEE 754 binary64 range (RFC 8785 requires I-JSON numbers).");
        }

        if (magnitude < -400)
        {
            return (BigInteger.Zero, 0);
        }

        BigInteger numerator;
        BigInteger denominator;
        if (exponent10 >= 0)
        {
            numerator = digits * BigInteger.Pow(10, exponent10);
            denominator = BigInteger.One;
        }
        else
        {
            numerator = digits;
            denominator = BigInteger.Pow(10, -exponent10);
        }

        var exponent = Math.Max((int)(numerator.GetBitLength() - denominator.GetBitLength()) - (MantissaBits + 1), MinExponent);
        BigInteger quotient;
        BigInteger remainder;
        BigInteger divisor;
        while (true)
        {
            (quotient, remainder, divisor) = Divide(numerator, denominator, exponent);
            if (quotient >= Limit)
            {
                exponent++;
            }
            else if (quotient < Hidden && exponent > MinExponent)
            {
                exponent--;
            }
            else
            {
                break;
            }
        }

        var twice = remainder * 2;
        if (twice > divisor || (twice == divisor && !quotient.IsEven))
        {
            quotient++;
        }

        if (quotient == Limit)
        {
            quotient = Hidden;
            exponent++;
        }

        if (exponent > MaxExponent)
        {
            throw new CanonicalJsonException("The number is outside the IEEE 754 binary64 range (RFC 8785 requires I-JSON numbers).");
        }

        return (quotient, exponent);
    }

    /// <summary>numerator / (denominator · 2^exponent) as quotient, remainder and divisor (remainder/divisor is the fraction).</summary>
    private static (BigInteger Quotient, BigInteger Remainder, BigInteger Divisor) Divide(BigInteger numerator, BigInteger denominator, int exponent)
    {
        if (exponent >= 0)
        {
            var divisor = denominator << exponent;
            return (BigInteger.DivRem(numerator, divisor, out var remainder), remainder, divisor);
        }

        var scaled = numerator << -exponent;
        return (BigInteger.DivRem(scaled, denominator, out var rest), rest, denominator);
    }

    /// <summary>Shortest round-tripping decimal of m·2^e in ECMAScript notation (m &gt; 0).</summary>
    private static string Format(BigInteger mantissa, int exponent)
    {
        // Exact decimal value: digits · 10^point.
        BigInteger exact;
        int point;
        if (exponent >= 0)
        {
            exact = mantissa << exponent;
            point = 0;
        }
        else
        {
            exact = mantissa * BigInteger.Pow(5, -exponent);
            point = exponent;
        }

        // Rounding interval [low, high] around v as integers over 2^shift (inclusive when the mantissa is even).
        var shift = Math.Max(0, 2 - exponent);
        var value = mantissa << (exponent + shift);
        var halfUp = BigInteger.One << (exponent - 1 + shift);
        var halfDown = mantissa == Hidden && exponent > MinExponent ? BigInteger.One << (exponent - 2 + shift) : halfUp;
        var low = value - halfDown;
        var high = value + halfUp;
        var inclusive = mantissa.IsEven;

        var length = DecimalDigits(exact);
        for (var n = 1; n < length; n++)
        {
            var dropped = length - n;
            var unit = BigInteger.Pow(10, dropped);
            var lower = BigInteger.DivRem(exact, unit, out var rest);
            var upper = lower + 1;
            var scale = point + dropped;

            var lowerFits = InInterval(lower, scale, shift, low, high, inclusive);
            var upperFits = InInterval(upper, scale, shift, low, high, inclusive);
            if (!lowerFits && !upperFits)
            {
                continue;
            }

            BigInteger chosen;
            if (lowerFits && upperFits)
            {
                var comparison = (rest * 2).CompareTo(unit);
                chosen = comparison < 0 ? lower : comparison > 0 ? upper : (lower.IsEven ? lower : upper);
            }
            else
            {
                chosen = lowerFits ? lower : upper;
            }

            return ToEcmaScript(chosen, scale);
        }

        return ToEcmaScript(exact, point);
    }

    /// <summary>True when candidate · 10^scale lies in the rounding interval [low, high] / 2^shift.</summary>
    private static bool InInterval(BigInteger candidate, int scale, int shift, BigInteger low, BigInteger high, bool inclusive)
    {
        BigInteger left;
        BigInteger lowBound;
        BigInteger highBound;
        if (scale >= 0)
        {
            left = (candidate * BigInteger.Pow(10, scale)) << shift;
            lowBound = low;
            highBound = high;
        }
        else
        {
            var factor = BigInteger.Pow(10, -scale);
            left = candidate << shift;
            lowBound = low * factor;
            highBound = high * factor;
        }

        var aboveLow = inclusive ? left >= lowBound : left > lowBound;
        var belowHigh = inclusive ? left <= highBound : left < highBound;
        return aboveLow && belowHigh;
    }

    /// <summary>ES2015 Number::toString steps 6–10 for digits · 10^scale.</summary>
    private static string ToEcmaScript(BigInteger digits, int scale)
    {
        while (!digits.IsZero && (digits % 10).IsZero)
        {
            digits /= 10;
            scale++;
        }

        var s = digits.ToString(CultureInfo.InvariantCulture);
        var k = s.Length;
        var n = scale + k;

        if (k <= n && n <= 21)
        {
            return s + new string('0', n - k);
        }

        if (n > 0 && n <= 21)
        {
            return s[..n] + "." + s[n..];
        }

        if (n > -6 && n <= 0)
        {
            return "0." + new string('0', -n) + s;
        }

        var exponent = n - 1;
        var sign = exponent < 0 ? "-" : "+";
        var magnitude = Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
        return k == 1 ? $"{s}e{sign}{magnitude}" : $"{s[0]}.{s[1..]}e{sign}{magnitude}";
    }

    private static int DecimalDigits(BigInteger value) =>
        value.IsZero ? 1 : BigInteger.Abs(value).ToString(CultureInfo.InvariantCulture).Length;
}
