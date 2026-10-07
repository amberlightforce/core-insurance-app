using System.Numerics;

namespace CoreIns.Rules.Runtime;

/// <summary>
/// Decimal <c>+ - *</c> that never lose precision silently (ruling D-ARC-10b). System.Decimal rounds a result that
/// needs more than 28–29 significant digits; here such a result is a typed <c>RULE-PRECISION-LOSS</c> error instead
/// (fail closed). The fast path is free: System.Decimal only rounds by lowering the scale, so a result that keeps the
/// exact scale is exact. Only a lowered scale is verified against an exact BigInteger computation.
/// </summary>
internal static class ExactDecimal
{
    public static decimal Add(decimal l, decimal r) => AddCore(l, r, "+");

    public static decimal Subtract(decimal l, decimal r) => AddCore(l, -r, "-");

    public static decimal Multiply(decimal l, decimal r)
    {
        decimal result;
        try
        {
            result = l * r;
        }
        catch (OverflowException)
        {
            throw Ops.Fail(RuleErrorCode.Overflow, "decimal overflow");
        }

        int exactScale = l.Scale + r.Scale;
        if (result.Scale < exactScale)
        {
            Verify(result, Mantissa(l) * Mantissa(r), exactScale, "*");
        }

        return result;
    }

    private static decimal AddCore(decimal l, decimal r, string op)
    {
        decimal result;
        try
        {
            result = l + r;
        }
        catch (OverflowException)
        {
            throw Ops.Fail(RuleErrorCode.Overflow, "decimal overflow");
        }

        int exactScale = Math.Max(l.Scale, r.Scale);
        if (result.Scale < exactScale)
        {
            var exact = (Mantissa(l) * BigInteger.Pow(10, exactScale - l.Scale)) + (Mantissa(r) * BigInteger.Pow(10, exactScale - r.Scale));
            Verify(result, exact, exactScale, op);
        }

        return result;
    }

    private static void Verify(decimal result, BigInteger exactMantissa, int exactScale, string op)
    {
        if (Mantissa(result) * BigInteger.Pow(10, exactScale - result.Scale) != exactMantissa)
        {
            throw Ops.Fail(
                RuleErrorCode.PrecisionLoss,
                $"the exact result of '{op}' needs more than 28 significant digits; round an operand explicitly with round(...)");
        }
    }

    /// <summary>The signed 96-bit integer mantissa (value = mantissa / 10^scale).</summary>
    internal static BigInteger Mantissa(decimal d)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(d, bits);
        var m = (new BigInteger((uint)bits[2]) << 64) | (new BigInteger((uint)bits[1]) << 32) | new BigInteger((uint)bits[0]);
        return bits[3] < 0 ? -m : m;
    }
}
