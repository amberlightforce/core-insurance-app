using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CoreIns.Rules.Syntax;

namespace CoreIns.Rules.Runtime;

/// <summary>
/// Runtime operator and function implementations. They dispatch on runtime kinds (so dyn values work); the type
/// checker guarantees statically-typed calls only reach valid combinations. int overflow and decimal overflow are
/// errors, never silent wrap-around or rounding.
/// </summary>
internal static class Ops
{
    // ------------------------------------------------------------------ helpers

    public static EvalFailure Fail(RuleErrorCode code, string message) => new(code, message);

    /// <summary>Longest value excerpt an error message may contain.</summary>
    public const int MaxEchoLength = 64;

    /// <summary>
    /// Describes a value for an error message without leaking data: string content (which may be personal data) is
    /// never echoed, only its length; other values are shown up to 64 characters, then "...".
    /// </summary>
    public static string Describe(RuleValue value)
    {
        if (value is StringValue s)
        {
            return string.Create(CultureInfo.InvariantCulture, $"a string of length {s.Value.Length}");
        }

        return Excerpt(value.ToString());
    }

    /// <summary>Cuts display text to 64 characters plus "..." (for expression text written by rule authors).</summary>
    public static string Excerpt(string text) => text.Length <= MaxEchoLength ? text : text[..MaxEchoLength] + "...";

    private static EvalFailure NoOverload(string op, RuleValue a)
        => a is NullValue
            ? Fail(RuleErrorCode.NullValue, $"'{op}' cannot be applied to null")
            : Fail(RuleErrorCode.InvalidValue, $"no overload of '{op}' for a value of kind {a.Kind}");

    private static EvalFailure NoOverload(string op, RuleValue a, RuleValue b)
        => a is NullValue || b is NullValue
            ? Fail(RuleErrorCode.NullValue, $"'{op}' cannot be applied to null")
            : Fail(RuleErrorCode.InvalidValue, $"no overload of '{op}' for ({a.Kind}, {b.Kind})");

    private static EvalFailure IntOverflow() => Fail(RuleErrorCode.Overflow, "integer overflow");

    private static EvalFailure DecimalOverflow() => Fail(RuleErrorCode.Overflow, "decimal overflow");

    private static bool IsNum(RuleValue v) => v is IntValue or DecimalValue;

    private static decimal Dec(RuleValue v) => v is IntValue i ? i.Value : ((DecimalValue)v).Value;

    public static bool AsBool(RuleValue v, string what) => v switch
    {
        BoolValue b => b.Value,
        NullValue => throw Fail(RuleErrorCode.NullValue, what + " is null, expected bool"),
        _ => throw Fail(RuleErrorCode.InvalidValue, what + $" has kind {v.Kind}, expected bool"),
    };

    private static long AsInt(RuleValue v, string what) => v switch
    {
        IntValue i => i.Value,
        NullValue => throw Fail(RuleErrorCode.NullValue, what + " is null, expected int"),
        _ => throw Fail(RuleErrorCode.InvalidValue, what + $" has kind {v.Kind}, expected int"),
    };

    private static string AsString(RuleValue v, string what) => v switch
    {
        StringValue s => s.Value,
        NullValue => throw Fail(RuleErrorCode.NullValue, what + " is null, expected string"),
        _ => throw Fail(RuleErrorCode.InvalidValue, what + $" has kind {v.Kind}, expected string"),
    };

    private static DateOnly AsDate(RuleValue v, string what) => v switch
    {
        DateValue d => d.Value,
        NullValue => throw Fail(RuleErrorCode.NullValue, what + " is null, expected date"),
        _ => throw Fail(RuleErrorCode.InvalidValue, what + $" has kind {v.Kind}, expected date"),
    };

    private static decimal AsNumber(RuleValue v, string what) => v switch
    {
        IntValue i => i.Value,
        DecimalValue d => d.Value,
        NullValue => throw Fail(RuleErrorCode.NullValue, what + " is null, expected a number"),
        _ => throw Fail(RuleErrorCode.InvalidValue, what + $" has kind {v.Kind}, expected a number"),
    };

    private static IReadOnlyList<RuleValue> AsList(RuleValue v, string what) => v switch
    {
        ListValue l => l.Items,
        NullValue => throw Fail(RuleErrorCode.NullValue, what + " is null, expected list"),
        _ => throw Fail(RuleErrorCode.InvalidValue, what + $" has kind {v.Kind}, expected list"),
    };

    // ------------------------------------------------------------------ arithmetic

    public static RuleValue Add(RuleValue a, RuleValue b, EvalState s)
    {
        if (a is IntValue x && b is IntValue y)
        {
            try
            {
                return IntValue.Of(checked(x.Value + y.Value));
            }
            catch (OverflowException)
            {
                throw IntOverflow();
            }
        }

        if (IsNum(a) && IsNum(b))
        {
            return new DecimalValue(ExactDecimal.Add(Dec(a), Dec(b)));
        }

        switch (a)
        {
            case StringValue sa when b is StringValue sb:
                if ((long)sa.Value.Length + sb.Value.Length > s.Limits.MaxStringLength)
                {
                    throw Fail(RuleErrorCode.LimitExceeded, "string concatenation exceeds the maximum string length");
                }

                s.Allocate(1 + ((sa.Value.Length + sb.Value.Length) / 16));
                return new StringValue(sa.Value + sb.Value);
            case ListValue la when b is ListValue lb:
                if ((long)la.Array.Length + lb.Array.Length > s.Limits.MaxCollectionSize)
                {
                    throw Fail(RuleErrorCode.LimitExceeded, "list concatenation exceeds the maximum collection size");
                }

                s.Allocate(RuleValue.AddWeight(la.Weight, lb.Weight));
                return new ListValue(la.Array.Concat(lb.Array).ToArray());
            case TimestampValue ta when b is DurationValue db:
                return AddTimestamp(ta.Value, db.Value);
            case DurationValue da when b is TimestampValue tb:
                return AddTimestamp(tb.Value, da.Value);
            case DurationValue da when b is DurationValue db:
                return AddDuration(da.Value, db.Value);
            default:
                throw NoOverload("+", a, b);
        }
    }

    public static RuleValue Subtract(RuleValue a, RuleValue b, EvalState s)
    {
        if (a is IntValue x && b is IntValue y)
        {
            try
            {
                return IntValue.Of(checked(x.Value - y.Value));
            }
            catch (OverflowException)
            {
                throw IntOverflow();
            }
        }

        if (IsNum(a) && IsNum(b))
        {
            return new DecimalValue(ExactDecimal.Subtract(Dec(a), Dec(b)));
        }

        switch (a)
        {
            case TimestampValue ta when b is TimestampValue tb:
                // The timestamp range (years 1-9999) always fits in a TimeSpan.
                return new DurationValue(ta.Value - tb.Value);

            case TimestampValue ta when b is DurationValue db:
                if (db.Value == TimeSpan.MinValue)
                {
                    throw Fail(RuleErrorCode.Overflow, "duration overflow");
                }

                return AddTimestamp(ta.Value, -db.Value);
            case DurationValue da when b is DurationValue db:
                try
                {
                    return new DurationValue(da.Value - db.Value);
                }
                catch (OverflowException)
                {
                    throw Fail(RuleErrorCode.Overflow, "duration overflow");
                }

            default:
                throw NoOverload("-", a, b);
        }
    }

    public static RuleValue Multiply(RuleValue a, RuleValue b, EvalState s)
    {
        if (a is IntValue x && b is IntValue y)
        {
            try
            {
                return IntValue.Of(checked(x.Value * y.Value));
            }
            catch (OverflowException)
            {
                throw IntOverflow();
            }
        }

        if (IsNum(a) && IsNum(b))
        {
            return new DecimalValue(ExactDecimal.Multiply(Dec(a), Dec(b)));
        }

        throw NoOverload("*", a, b);
    }

    public static RuleValue Divide(RuleValue a, RuleValue b, EvalState s)
    {
        if (a is IntValue x && b is IntValue y)
        {
            if (y.Value == 0)
            {
                throw Fail(RuleErrorCode.DivisionByZero, "division by zero");
            }

            if (x.Value == long.MinValue && y.Value == -1)
            {
                throw IntOverflow();
            }

            return IntValue.Of(x.Value / y.Value);
        }

        if (IsNum(a) && IsNum(b))
        {
            decimal l = Dec(a), r = Dec(b);
            if (r == 0m)
            {
                throw Fail(RuleErrorCode.DivisionByZero, "division by zero");
            }

            // Division is the one operation that rounds: System.Decimal keeps 28-29 significant digits (documented);
            // rules must pass the quotient through round(...) before it becomes money.
            try
            {
                return new DecimalValue(l / r);
            }
            catch (OverflowException)
            {
                throw DecimalOverflow();
            }
        }

        throw NoOverload("/", a, b);
    }

    public static RuleValue Modulo(RuleValue a, RuleValue b, EvalState s)
    {
        if (a is IntValue x && b is IntValue y)
        {
            if (y.Value == 0)
            {
                throw Fail(RuleErrorCode.DivisionByZero, "modulus by zero");
            }

            return IntValue.Of(y.Value == -1 ? 0 : x.Value % y.Value);
        }

        throw NoOverload("%", a, b);
    }

    public static RuleValue Negate(RuleValue a, EvalState s)
    {
        switch (a)
        {
            case IntValue i:
                if (i.Value == long.MinValue)
                {
                    throw IntOverflow();
                }

                return IntValue.Of(-i.Value);
            case DecimalValue d:
                return new DecimalValue(-d.Value);
            case DurationValue du:
                if (du.Value == TimeSpan.MinValue)
                {
                    throw Fail(RuleErrorCode.Overflow, "duration overflow");
                }

                return new DurationValue(-du.Value);
            default:
                throw NoOverload("-", a);
        }
    }

    private static TimestampValue AddTimestamp(DateTimeOffset ts, TimeSpan d)
    {
        try
        {
            return new TimestampValue(ts.Add(d));
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Fail(RuleErrorCode.Overflow, "timestamp out of range");
        }
    }

    private static DurationValue AddDuration(TimeSpan a, TimeSpan b)
    {
        try
        {
            return new DurationValue(a + b);
        }
        catch (OverflowException)
        {
            throw Fail(RuleErrorCode.Overflow, "duration overflow");
        }
    }

    // ------------------------------------------------------------------ comparison

    public static RuleValue Equal(RuleValue a, RuleValue b, EvalState s)
    {
        s.ChargeCompare(a, b);
        return BoolValue.Of(a.Equals(b));
    }

    public static RuleValue NotEqual(RuleValue a, RuleValue b, EvalState s)
    {
        s.ChargeCompare(a, b);
        return BoolValue.Of(!a.Equals(b));
    }

    public static int Compare(RuleValue a, RuleValue b, string op)
    {
        if (a is IntValue x && b is IntValue y)
        {
            return x.Value.CompareTo(y.Value);
        }

        if (IsNum(a) && IsNum(b))
        {
            return Dec(a).CompareTo(Dec(b));
        }

        return a switch
        {
            StringValue sa when b is StringValue sb => Math.Sign(string.CompareOrdinal(sa.Value, sb.Value)),
            BoolValue ba when b is BoolValue bb => ba.Value.CompareTo(bb.Value),
            DateValue da when b is DateValue db => da.Value.CompareTo(db.Value),
            TimestampValue ta when b is TimestampValue tb => ta.Value.CompareTo(tb.Value),
            DurationValue ua when b is DurationValue ub => ua.Value.CompareTo(ub.Value),
            _ => throw NoOverload(op, a, b),
        };
    }

    public static RuleValue Less(RuleValue a, RuleValue b, EvalState s)
    {
        s.ChargeCompare(a, b);
        return BoolValue.Of(Compare(a, b, "<") < 0);
    }

    public static RuleValue LessOrEqual(RuleValue a, RuleValue b, EvalState s)
    {
        s.ChargeCompare(a, b);
        return BoolValue.Of(Compare(a, b, "<=") <= 0);
    }

    public static RuleValue Greater(RuleValue a, RuleValue b, EvalState s)
    {
        s.ChargeCompare(a, b);
        return BoolValue.Of(Compare(a, b, ">") > 0);
    }

    public static RuleValue GreaterOrEqual(RuleValue a, RuleValue b, EvalState s)
    {
        s.ChargeCompare(a, b);
        return BoolValue.Of(Compare(a, b, ">=") >= 0);
    }

    public static RuleValue In(RuleValue element, RuleValue collection, EvalState s)
    {
        switch (collection)
        {
            case ListValue l:
                foreach (var item in l.Array)
                {
                    s.ChargeCompare(item, element);
                    if (item.Equals(element))
                    {
                        return BoolValue.True;
                    }
                }

                return BoolValue.False;
            case MapValue m:
                s.Charge(element.Weight);
                return BoolValue.Of(m.ContainsKey(element));
            default:
                throw NoOverload("in", collection);
        }
    }

    public static RuleValue Index(RuleValue collection, RuleValue index, EvalState s)
    {
        switch (collection)
        {
            case ListValue l:
            {
                long i = AsInt(index, "list index");
                if (i < 0 || i >= l.Array.Length)
                {
                    throw Fail(
                        RuleErrorCode.IndexOutOfRange,
                        string.Create(CultureInfo.InvariantCulture, $"index {i} is out of range for a list of size {l.Array.Length}"));
                }

                return l.Array[i];
            }

            case MapValue m:
                if (m.TryGetValue(index, out var v))
                {
                    return v;
                }

                throw Fail(RuleErrorCode.NoSuchKey, "no such key: " + Describe(index));
            default:
                throw NoOverload("[]", collection, index);
        }
    }

    // ------------------------------------------------------------------ size and strings

    public static RuleValue Size(RuleValue v, EvalState s) => v switch
    {
        StringValue str => CountRunes(str.Value, s),
        ListValue l => IntValue.Of(l.Array.Length),
        MapValue m => IntValue.Of(m.Count),
        _ => throw NoOverload("size", v),
    };

    private static IntValue CountRunes(string text, EvalState s)
    {
        s.ChargeText(text);
        return IntValue.Of(text.EnumerateRunes().Count());
    }

    private static string Scanned(RuleValue v, string what, EvalState s)
    {
        string text = AsString(v, what);
        s.ChargeText(text);
        return text;
    }

    private static string Produced(RuleValue v, EvalState s)
    {
        string text = AsString(v, "receiver");
        s.Allocate(1 + (text.Length / 16));
        return text;
    }

    public static RuleValue StartsWith(RuleValue a, RuleValue b, EvalState s) =>
        BoolValue.Of(Scanned(a, "receiver", s).StartsWith(AsString(b, "argument"), StringComparison.Ordinal));

    public static RuleValue EndsWith(RuleValue a, RuleValue b, EvalState s) =>
        BoolValue.Of(Scanned(a, "receiver", s).EndsWith(AsString(b, "argument"), StringComparison.Ordinal));

    public static RuleValue Contains(RuleValue a, RuleValue b, EvalState s) =>
        BoolValue.Of(Scanned(a, "receiver", s).Contains(AsString(b, "argument"), StringComparison.Ordinal));

    public static RuleValue LowerAscii(RuleValue a, EvalState s) => new StringValue(MapAscii(Produced(a, s), upper: false));

    public static RuleValue UpperAscii(RuleValue a, EvalState s) => new StringValue(MapAscii(Produced(a, s), upper: true));

    public static RuleValue Trim(RuleValue a, EvalState s) => new StringValue(Produced(a, s).Trim());

    private static string MapAscii(string text, bool upper)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (upper && c is >= 'a' and <= 'z')
            {
                chars[i] = (char)(c - 32);
            }
            else if (!upper && c is >= 'A' and <= 'Z')
            {
                chars[i] = (char)(c + 32);
            }
        }

        return new string(chars);
    }

    // ------------------------------------------------------------------ conversions

    public static RuleValue ToInt(RuleValue v, EvalState s)
    {
        switch (v)
        {
            case IntValue:
                return v;
            case DecimalValue d:
                try
                {
                    return IntValue.Of(decimal.ToInt64(decimal.Truncate(d.Value)));
                }
                catch (OverflowException)
                {
                    throw IntOverflow();
                }

            case StringValue str:
                if (long.TryParse(str.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
                {
                    return IntValue.Of(l);
                }

                throw Fail(RuleErrorCode.InvalidValue, "cannot convert " + Describe(str) + " to int");
            default:
                throw NoOverload("int", v);
        }
    }

    public static RuleValue ToDecimal(RuleValue v, EvalState s) => v switch
    {
        IntValue i => new DecimalValue(i.Value),
        DecimalValue => v,
        StringValue str => DecimalText.TryParse(str.Value, out decimal d)
            ? new DecimalValue(d)
            : throw Fail(RuleErrorCode.InvalidValue, "cannot convert " + Describe(str) + " to decimal"),
        _ => throw NoOverload("decimal", v),
    };

    public static RuleValue ToStringValue(RuleValue v, EvalState s) => v switch
    {
        StringValue => v,
        IntValue or DecimalValue or BoolValue => new StringValue(v.ToString()),
        DateValue d => new StringValue(DateValue.Format(d.Value)),
        TimestampValue t => new StringValue(TimestampValue.Format(t.Value)),
        DurationValue du => new StringValue(DurationValue.Format(du.Value)),
        _ => throw NoOverload("string", v),
    };

    public static RuleValue ToDate(RuleValue v, EvalState s) => v switch
    {
        StringValue str => ParseDate(str.Value) ?? throw Fail(RuleErrorCode.InvalidValue, "cannot convert " + Describe(str) + " to date (expected yyyy-MM-dd)"),
        TimestampValue t => new DateValue(DateOnly.FromDateTime(t.Value.UtcDateTime)),
        DateValue => v,
        _ => throw NoOverload("date", v),
    };

    public static RuleValue ToTimestamp(RuleValue v, EvalState s) => v switch
    {
        StringValue str => ParseTimestamp(str.Value) ?? throw Fail(RuleErrorCode.InvalidValue, "cannot convert " + Describe(str) + " to timestamp (expected RFC 3339)"),
        TimestampValue => v,
        _ => throw NoOverload("timestamp", v),
    };

    public static RuleValue ToDuration(RuleValue v, EvalState s) => v switch
    {
        StringValue str => ParseDuration(str.Value) ?? throw Fail(RuleErrorCode.InvalidValue, "cannot convert " + Describe(str) + " to duration (expected for example \"90s\", \"1h30m\")"),
        DurationValue => v,
        _ => throw NoOverload("duration", v),
    };

    public static DateValue? ParseDate(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? new DateValue(d) : null;

    private static readonly string[] TimestampFormats =
    {
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    };

    public static TimestampValue? ParseTimestamp(string text)
    {
        if (!text.EndsWith('Z') && !HasOffset(text))
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(text, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts)
            ? new TimestampValue(ts)
            : null;
    }

    private static bool HasOffset(string text) =>
        text.Length > 6 && text[^6] is '+' or '-' && text[^3] == ':';

    /// <summary>Parses CEL duration text: optional sign, then one or more decimal-number + unit (h, m, s, ms, us, ns).</summary>
    public static DurationValue? ParseDuration(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        int i = 0;
        bool negative = false;
        if (text[0] is '-' or '+')
        {
            negative = text[0] == '-';
            i = 1;
        }

        if (i >= text.Length)
        {
            return null;
        }

        decimal totalTicks = 0m;
        while (i < text.Length)
        {
            int numStart = i;
            while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
            {
                i++;
            }

            if (i == numStart || !DecimalText.TryParse(text[numStart..i], out decimal amount))
            {
                return null;
            }

            int unitStart = i;
            while (i < text.Length && char.IsAsciiLetter(text[i]))
            {
                i++;
            }

            decimal ticksPerUnit = text[unitStart..i] switch
            {
                "h" => TimeSpan.TicksPerHour,
                "m" => TimeSpan.TicksPerMinute,
                "s" => TimeSpan.TicksPerSecond,
                "ms" => TimeSpan.TicksPerMillisecond,
                "us" => 10m,
                "ns" => 0.01m,
                _ => -1m,
            };
            if (ticksPerUnit < 0m)
            {
                return null;
            }

            try
            {
                totalTicks += amount * ticksPerUnit;
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        if (totalTicks != decimal.Truncate(totalTicks) || totalTicks > long.MaxValue)
        {
            return null;
        }

        long ticks = (long)totalTicks;
        return new DurationValue(TimeSpan.FromTicks(negative ? -ticks : ticks));
    }

    // ------------------------------------------------------------------ math

    public static RuleValue Round(RuleValue x, RuleValue places, RoundingMode mode)
    {
        decimal value = AsNumber(x, "round value");
        long p = AsInt(places, "round places");
        if (p is < 0 or > DecimalRounding.MaxPlaces)
        {
            throw Fail(RuleErrorCode.InvalidValue, string.Create(CultureInfo.InvariantCulture, $"round places must be between 0 and {DecimalRounding.MaxPlaces}, got {p}"));
        }

        return new DecimalValue(DecimalRounding.Round(value, (int)p, mode));
    }

    public static RuleValue Abs(RuleValue v, EvalState s)
    {
        switch (v)
        {
            case IntValue i:
                if (i.Value == long.MinValue)
                {
                    throw IntOverflow();
                }

                return IntValue.Of(Math.Abs(i.Value));
            case DecimalValue d:
                return new DecimalValue(Math.Abs(d.Value));
            default:
                throw NoOverload("abs", v);
        }
    }

    public static RuleValue MinOf(IReadOnlyList<RuleValue> values, string name, bool max, EvalState s)
    {
        s.Charge(values.Count);
        if (values.Count == 0)
        {
            throw Fail(RuleErrorCode.InvalidValue, name + " of an empty list");
        }

        var best = values[0];
        for (int i = 1; i < values.Count; i++)
        {
            int c = Compare(values[i], best, name);
            if (max ? c > 0 : c < 0)
            {
                best = values[i];
            }
        }

        if (best is NullValue)
        {
            throw Fail(RuleErrorCode.NullValue, name + " argument is null");
        }

        return best;
    }

    public static RuleValue MinOfList(RuleValue list, string name, bool max, EvalState s) => MinOf(AsList(list, name + " argument"), name, max, s);

    public static RuleValue Sum(RuleValue list, bool asDecimal, EvalState s)
    {
        RuleValue total = asDecimal ? new DecimalValue(0m) : IntValue.Of(0);
        var items = AsList(list, "sum argument");
        s.Charge(items.Count);
        foreach (var item in items)
        {
            total = Add(total, item, s);
        }

        return total;
    }

    // ------------------------------------------------------------------ dates

    /// <summary>Whole years from <paramref name="from"/> to <paramref name="to"/> (negative when to &lt; from).</summary>
    public static long WholeYears(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            return -WholeYears(to, from);
        }

        int years = to.Year - from.Year;
        if (from.AddYears(years) > to)
        {
            years--;
        }

        return years;
    }

    /// <summary>Whole months from <paramref name="from"/> to <paramref name="to"/> (negative when to &lt; from).</summary>
    public static long WholeMonths(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            return -WholeMonths(to, from);
        }

        int months = ((to.Year - from.Year) * 12) + to.Month - from.Month;
        if (from.AddMonths(months) > to)
        {
            months--;
        }

        return months;
    }

    public static RuleValue AgeAt(RuleValue birth, RuleValue asOf, EvalState s)
    {
        var b = AsDate(birth, "ageAt birth date");
        var a = AsDate(asOf, "ageAt as-of date");
        if (a < b)
        {
            throw Fail(RuleErrorCode.InvalidValue, "ageAt: the as-of date is before the birth date");
        }

        return IntValue.Of(WholeYears(b, a));
    }

    public static RuleValue YearsBetween(RuleValue from, RuleValue to, EvalState s) =>
        IntValue.Of(WholeYears(AsDate(from, "yearsBetween from"), AsDate(to, "yearsBetween to")));

    public static RuleValue MonthsBetween(RuleValue from, RuleValue to, EvalState s) =>
        IntValue.Of(WholeMonths(AsDate(from, "monthsBetween from"), AsDate(to, "monthsBetween to")));

    public static RuleValue DaysBetween(RuleValue from, RuleValue to, EvalState s) =>
        IntValue.Of((long)AsDate(to, "daysBetween to").DayNumber - AsDate(from, "daysBetween from").DayNumber);

    public static RuleValue AddDays(RuleValue date, RuleValue n, EvalState s) =>
        DateArithmetic(AsDate(date, "addDays date"), AsInt(n, "addDays days"), (d, k) => d.AddDays(k));

    public static RuleValue AddMonths(RuleValue date, RuleValue n, EvalState s) =>
        DateArithmetic(AsDate(date, "addMonths date"), AsInt(n, "addMonths months"), (d, k) => d.AddMonths(k));

    public static RuleValue AddYears(RuleValue date, RuleValue n, EvalState s) =>
        DateArithmetic(AsDate(date, "addYears date"), AsInt(n, "addYears years"), (d, k) => d.AddYears(k));

    private static DateValue DateArithmetic(DateOnly date, long amount, Func<DateOnly, int, DateOnly> op)
    {
        if (amount is > int.MaxValue or < int.MinValue)
        {
            throw Fail(RuleErrorCode.Overflow, "date out of range");
        }

        try
        {
            return new DateValue(op(date, (int)amount));
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Fail(RuleErrorCode.Overflow, "date out of range");
        }
    }

    public static RuleValue Year(RuleValue d, EvalState s) => IntValue.Of(AsDate(d, "year argument").Year);

    public static RuleValue Month(RuleValue d, EvalState s) => IntValue.Of(AsDate(d, "month argument").Month);

    public static RuleValue Day(RuleValue d, EvalState s) => IntValue.Of(AsDate(d, "day argument").Day);

    /// <summary>ISO day of week: Monday = 1 … Sunday = 7.</summary>
    public static RuleValue DayOfWeek(RuleValue d, EvalState s) => IntValue.Of((((int)AsDate(d, "dayOfWeek argument").DayOfWeek + 6) % 7) + 1);
}
