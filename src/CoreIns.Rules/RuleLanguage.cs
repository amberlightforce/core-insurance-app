using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreIns.Rules;

/// <summary>
/// The adopted CEL subset, per language version (REQ-PLT-364). Conformance tests assert that the checker accepts
/// exactly these functions; anything else is rejected at compile time.
/// </summary>
public static class RuleLanguage
{
    /// <summary>Language version. Part of every canonical text, so a version change changes every content hash.</summary>
    public const string Version = "1.0";

    /// <summary>Operators (CEL internal names).</summary>
    public static IReadOnlyList<string> Operators { get; } = new[]
    {
        "_?_:_", "_||_", "_&&_", "!_", "-_", "_+_", "_-_", "_*_", "_/_", "_%_",
        "_==_", "_!=_", "_<_", "_<=_", "_>_", "_>=_", "@in", "_[_]",
    };

    /// <summary>Macros (comprehensions and presence test).</summary>
    public static IReadOnlyList<string> Macros { get; } = new[] { "has", "all", "exists", "exists_one", "map", "filter" };

    /// <summary>Global functions.</summary>
    public static IReadOnlyList<string> GlobalFunctions { get; } = new[]
    {
        "size", "int", "decimal", "string", "date", "timestamp", "duration",
        "round", "abs", "min", "max", "sum",
        "ageAt", "yearsBetween", "monthsBetween", "daysBetween", "addDays", "addMonths", "addYears",
        "year", "month", "day", "dayOfWeek", "matches",
    };

    /// <summary>Member (receiver-style) functions.</summary>
    public static IReadOnlyList<string> MemberFunctions { get; } = new[]
    {
        "size", "startsWith", "endsWith", "contains", "matches", "lowerAscii", "upperAscii", "trim",
    };

    /// <summary>Rounding mode names accepted by <c>round(x, places, mode)</c>.</summary>
    public static IReadOnlyList<string> RoundingModes { get; } = Enum.GetNames<RoundingMode>();

    /// <summary>Names that would introduce non-determinism; using them fails compilation with RULE-NONDETERMINISTIC.</summary>
    public static IReadOnlyList<string> NonDeterministicNames { get; } = new[]
    {
        "now", "today", "random", "rand", "uuid", "guid", "clock", "currentDate", "currentTime", "sysdate", "utcNow",
    };

    /// <summary>CEL standard functions deliberately outside the subset (RULE-UNSUPPORTED).</summary>
    public static IReadOnlyList<string> UnsupportedCelFunctions { get; } = new[]
    {
        "double", "uint", "bytes", "dyn", "type",
        "getFullYear", "getMonth", "getDate", "getDayOfMonth", "getDayOfWeek", "getDayOfYear",
        "getHours", "getMinutes", "getSeconds", "getMilliseconds",
    };

    internal const string CanonicalPrefix = "cel-subset/" + Version + ":";

    internal static readonly IReadOnlySet<string> AllBuiltinNames =
        new HashSet<string>(Macros.Concat(GlobalFunctions).Concat(MemberFunctions), StringComparer.Ordinal);
}

/// <summary>Explicit rounding modes for <c>round(x, places, mode)</c> and <see cref="DecimalRounding"/>.</summary>
public enum RoundingMode
{
    /// <summary>Round half to even (banker's rounding).</summary>
    HalfEven,

    /// <summary>Round half away from zero (commercial rounding).</summary>
    HalfUp,

    /// <summary>Round half toward zero.</summary>
    HalfDown,

    /// <summary>Always away from zero.</summary>
    Up,

    /// <summary>Always toward zero (truncate).</summary>
    Down,

    /// <summary>Toward positive infinity.</summary>
    Ceiling,

    /// <summary>Toward negative infinity.</summary>
    Floor,
}

/// <summary>Decimal rounding with explicit mode; the result has exactly <c>places</c> fractional digits.</summary>
public static class DecimalRounding
{
    /// <summary>Maximum decimal places (System.Decimal scale limit).</summary>
    public const int MaxPlaces = 28;

    /// <summary>PaddedOnes[p] is 1 written with p fractional zeros (1, 1.0, 1.00, ...); multiplying by it pads the scale.</summary>
    private static readonly decimal[] PaddedOnes = BuildPaddedOnes();

    /// <summary>Rounds <paramref name="value"/> to <paramref name="places"/> fractional digits using <paramref name="mode"/>.</summary>
    public static decimal Round(decimal value, int places, RoundingMode mode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(places);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(places, MaxPlaces);
        decimal rounded = mode switch
        {
            RoundingMode.HalfEven => decimal.Round(value, places, MidpointRounding.ToEven),
            RoundingMode.HalfUp => decimal.Round(value, places, MidpointRounding.AwayFromZero),
            RoundingMode.Down => decimal.Round(value, places, MidpointRounding.ToZero),
            RoundingMode.Ceiling => decimal.Round(value, places, MidpointRounding.ToPositiveInfinity),
            RoundingMode.Floor => decimal.Round(value, places, MidpointRounding.ToNegativeInfinity),
            RoundingMode.Up => decimal.Round(value, places, value >= 0 ? MidpointRounding.ToPositiveInfinity : MidpointRounding.ToNegativeInfinity),
            RoundingMode.HalfDown => HalfDown(value, places),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        return WithScale(rounded, places);
    }

    /// <summary>Parses a rounding mode name (exact, case-sensitive).</summary>
    public static bool TryParseMode(string name, out RoundingMode mode)
    {
        foreach (var m in Enum.GetValues<RoundingMode>())
        {
            if (string.Equals(m.ToString(), name, StringComparison.Ordinal))
            {
                mode = m;
                return true;
            }
        }

        mode = default;
        return false;
    }

    private static decimal[] BuildPaddedOnes()
    {
        var ones = new decimal[MaxPlaces + 1];
        ones[0] = 1m;
        for (int p = 1; p <= MaxPlaces; p++)
        {
            ones[p] = ones[p - 1] * 1.0m;
        }

        return ones;
    }

    private static decimal HalfDown(decimal value, int places)
    {
        decimal truncated = decimal.Round(value, places, MidpointRounding.ToZero);
        if (truncated == value)
        {
            return truncated;
        }

        decimal unit = new(1, 0, 0, false, (byte)places);
        decimal remainder = Math.Abs(value - truncated);
        decimal half = unit / 2m;
        if (remainder > half)
        {
            return value > 0 ? truncated + unit : truncated - unit;
        }

        return truncated;
    }

    /// <summary>Pads the scale up to <paramref name="places"/> (15 → 15.00) without changing the value.</summary>
    private static decimal WithScale(decimal value, int places)
    {
        int scale = value.Scale;
        if (scale >= places)
        {
            return value;
        }

        // Multiplying by 1.00…0 never changes the magnitude, so it cannot overflow (System.Decimal drops scale if needed).
        return value * PaddedOnes[places - scale];
    }
}
