using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CoreIns.Rules;

/// <summary>
/// A runtime value of the rule language. Values are immutable; equality is value equality
/// (int and decimal compare numerically, so <c>1 == 1.0</c>).
/// </summary>
public abstract class RuleValue : IEquatable<RuleValue>
{
    private protected RuleValue()
    {
    }

    /// <summary>The kind of the value.</summary>
    public abstract RuleTypeKind Kind { get; }

    /// <summary>The null value.</summary>
    public static RuleValue Null => NullValue.Instance;

    /// <summary>
    /// Size of the value tree (1 per scalar, 1 per 16 string characters, plus every element of a collection, counting
    /// shared sub-values once per occurrence; saturating). Operations that traverse a value are charged its weight, so
    /// the cost budget bounds work even for exponentially shared structures.
    /// </summary>
    internal virtual long Weight => 1;

    internal static long AddWeight(long total, long add) => add >= long.MaxValue - total ? long.MaxValue : total + add;

    /// <summary>Wraps an int.</summary>
    public static implicit operator RuleValue(long value) => IntValue.Of(value);

    /// <summary>Wraps an int.</summary>
    public static implicit operator RuleValue(int value) => IntValue.Of(value);

    /// <summary>Wraps a decimal.</summary>
    public static implicit operator RuleValue(decimal value) => new DecimalValue(value);

    /// <summary>Wraps a string (null becomes the null value).</summary>
    public static implicit operator RuleValue(string? value) => value is null ? NullValue.Instance : new StringValue(value);

    /// <summary>Wraps a bool.</summary>
    public static implicit operator RuleValue(bool value) => BoolValue.Of(value);

    /// <summary>Wraps a date.</summary>
    public static implicit operator RuleValue(DateOnly value) => new DateValue(value);

    /// <summary>Wraps a timestamp (normalised to UTC).</summary>
    public static implicit operator RuleValue(DateTimeOffset value) => new TimestampValue(value);

    /// <summary>Wraps a duration.</summary>
    public static implicit operator RuleValue(TimeSpan value) => new DurationValue(value);

    /// <summary>Creates a list value.</summary>
    public static ListValue List(params RuleValue[] items) => new(items.ToArray());

    /// <summary>Creates a list value.</summary>
    public static ListValue List(IEnumerable<RuleValue> items) => new(items.ToArray());

    /// <summary>Creates a map value (keys must be int, string or bool and unique). Entry order is preserved.</summary>
    public static MapValue Map(IEnumerable<KeyValuePair<RuleValue, RuleValue>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.ToArray();
        var seen = new HashSet<RuleValue>();
        foreach (var e in list)
        {
            if (e.Key.Kind is not (RuleTypeKind.Int or RuleTypeKind.String or RuleTypeKind.Bool))
            {
                throw new ArgumentException("map keys must be int, string or bool", nameof(entries));
            }

            if (!seen.Add(e.Key))
            {
                throw new ArgumentException("duplicate map key " + e.Key, nameof(entries));
            }
        }

        return new MapValue(list);
    }

    /// <inheritdoc />
    public abstract bool Equals(RuleValue? other);

    /// <inheritdoc />
    public sealed override bool Equals(object? obj) => obj is RuleValue v && Equals(v);

    /// <inheritdoc />
    public abstract override int GetHashCode();

    /// <summary>
    /// Invariant, unambiguous display text (strings are quoted), used in traces and messages. Bounded: renderings
    /// longer than 4 096 characters are cut and end in "...".
    /// </summary>
    public abstract override string ToString();

    /// <summary>Appends the display text; returns false once the rendering budget is exhausted.</summary>
    internal virtual bool AppendTo(StringBuilder sb)
    {
        sb.Append(ToString());
        return sb.Length <= ValueAlgorithms.MaxRenderLength;
    }

    internal static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}

/// <summary>64-bit integer value.</summary>
public sealed class IntValue : RuleValue
{
    private static readonly IntValue[] Small = Enumerable.Range(-16, 273).Select(i => new IntValue(i)).ToArray();

    private IntValue(long value) => Value = value;

    /// <summary>The value.</summary>
    public long Value { get; }

    /// <inheritdoc />
    public override RuleTypeKind Kind => RuleTypeKind.Int;

    /// <summary>Gets a (possibly cached) instance.</summary>
    public static IntValue Of(long value) => value is >= -16 and <= 256 ? Small[value + 16] : new IntValue(value);

    /// <inheritdoc />
    public override bool Equals(RuleValue? other) => other switch
    {
        IntValue i => i.Value == Value,
        DecimalValue d => d.Value == Value,
        _ => false,
    };

    /// <inheritdoc />
    public override int GetHashCode() => ((decimal)Value).GetHashCode();

    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Exact decimal value (System.Decimal; scale is preserved, so 15.0 and 15.00 are equal but print differently).</summary>
public sealed class DecimalValue : RuleValue
{
    /// <summary>Creates the value.</summary>
    public DecimalValue(decimal value) => Value = value;

    /// <summary>The value.</summary>
    public decimal Value { get; }

    /// <inheritdoc />
    public override RuleTypeKind Kind => RuleTypeKind.Decimal;

    /// <inheritdoc />
    public override bool Equals(RuleValue? other) => other switch
    {
        DecimalValue d => d.Value == Value,
        IntValue i => i.Value == Value,
        _ => false,
    };

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>String value.</summary>
public sealed class StringValue : RuleValue
{
    /// <summary>Creates the value.</summary>
    public StringValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>The value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override RuleTypeKind Kind => RuleTypeKind.String;

    internal override long Weight => 1 + (Value.Length / 16);

    /// <inheritdoc />
    public override bool Equals(RuleValue? other) => other is StringValue s && string.Equals(s.Value, Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => ValueAlgorithms.Render(this);

    internal override bool AppendTo(StringBuilder sb)
    {
        int room = Math.Max(0, ValueAlgorithms.MaxRenderLength - sb.Length);
        if (Value.Length <= room)
        {
            sb.Append(Quote(Value));
            return sb.Length <= ValueAlgorithms.MaxRenderLength;
        }

        int cut = room;
        if (cut > 0 && char.IsHighSurrogate(Value[cut - 1]))
        {
            cut--;
        }

        sb.Append(Quote(Value[..cut]));
        return false;
    }
}

/// <summary>Boolean value.</summary>
public sealed class BoolValue : RuleValue
{
    private BoolValue(bool value) => Value = value;

    /// <summary>true.</summary>
    public static BoolValue True { get; } = new(true);

    /// <summary>false.</summary>
    public static BoolValue False { get; } = new(false);

    /// <summary>The value.</summary>
    public bool Value { get; }

    /// <inheritdoc />
    public override RuleTypeKind Kind => RuleTypeKind.Bool;

    /// <summary>Gets the shared instance.</summary>
    public static BoolValue Of(bool value) => value ? True : False;

    /// <inheritdoc />
    public override bool Equals(RuleValue? other) => other is BoolValue b && b.Value == Value;

    /// <inheritdoc />
    public override int GetHashCode() => Value ? 1 : 0;

    /// <inheritdoc />
    public override string ToString() => Value ? "true" : "false";
}

/// <summary>The null value ("no value").</summary>
public sealed class NullValue : RuleValue
{
    private NullValue()
    {
    }

    /// <summary>The single instance.</summary>
    public static NullValue Instance { get; } = new();

    /// <inheritdoc />
    public override RuleTypeKind Kind => RuleTypeKind.Null;

    /// <inheritdoc />
    public override bool Equals(RuleValue? other) => other is NullValue;

    /// <inheritdoc />
    public override int GetHashCode() => 0;

    /// <inheritdoc />
    public override string ToString() => "null";
}

/// <summary>Calendar date value.</summary>
public sealed class DateValue : RuleValue
{
    /// <summary>Creates the value.</summary>
    public DateValue(DateOnly value) => Value = value;

    /// <summary>The value.</summary>
    public DateOnly Value { get; }

    /// <inheritdoc />
    public override RuleTypeKind Kind => RuleTypeKind.Date;

    /// <inheritdoc />
    public override bool Equals(RuleValue? other) => other is DateValue d && d.Value == Value;

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => "date(\"" + Format(Value) + "\")";

    internal static string Format(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>UTC timestamp value.</summary>
public sealed class TimestampValue : RuleValue
{
    /// <summary>Creates the value (converted to UTC).</summary>
    public TimestampValue(DateTimeOffset value) => Value = value.ToUniversalTime();

    /// <summary>The value (always offset zero).</summary>
    public DateTimeOffset Value { get; }

    /// <inheritdoc />
    public override RuleTypeKind Kind => RuleTypeKind.Timestamp;

    /// <inheritdoc />
    public override bool Equals(RuleValue? other) => other is TimestampValue t && t.Value == Value;

    /// <inheritdoc />
    public override int GetHashCode() => Value.UtcTicks.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => "timestamp(\"" + Format(Value) + "\")";

    internal static string Format(DateTimeOffset ts)
    {
        var utc = ts.UtcDateTime;
        string main = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        long fraction = utc.Ticks % TimeSpan.TicksPerSecond;
        if (fraction != 0)
        {
            main += "." + fraction.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        }

        return main + "Z";
    }
}

/// <summary>Duration value.</summary>
public sealed class DurationValue : RuleValue
{
    /// <summary>Creates the value.</summary>
    public DurationValue(TimeSpan value) => Value = value;

    /// <summary>The value.</summary>
    public TimeSpan Value { get; }

    /// <inheritdoc />
    public override RuleTypeKind Kind => RuleTypeKind.Duration;

    /// <inheritdoc />
    public override bool Equals(RuleValue? other) => other is DurationValue d && d.Value == Value;

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => "duration(\"" + Format(Value) + "\")";

    /// <summary>CEL duration text in seconds, for example <c>3600s</c> or <c>-1.5s</c>.</summary>
    internal static string Format(TimeSpan d)
    {
        long ticks = d.Ticks;
        bool negative = ticks < 0;
        ulong abs = negative ? (ulong)(-(ticks + 1)) + 1UL : (ulong)ticks;
        ulong seconds = abs / (ulong)TimeSpan.TicksPerSecond;
        ulong fraction = abs % (ulong)TimeSpan.TicksPerSecond;
        string text = seconds.ToString(CultureInfo.InvariantCulture);
        if (fraction != 0)
        {
            text += "." + fraction.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        }

        return (negative ? "-" : string.Empty) + text + "s";
    }
}

/// <summary>Immutable list value.</summary>
public sealed class ListValue : RuleValue
{
    private readonly RuleValue[] _items;
    private readonly long _weight;

    internal ListValue(RuleValue[] items)
    {
        long weight = 1;
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item, nameof(items));
            weight = AddWeight(weight, item.Weight);
        }

        _items = items;
        _weight = weight;
    }

    internal override long Weight => _weight;

    /// <summary>The elements.</summary>
    public IReadOnlyList<RuleValue> Items => _items;

    /// <inheritdoc />
    public override RuleTypeKind Kind => RuleTypeKind.List;

    internal RuleValue[] Array => _items;

    /// <inheritdoc />
    public override bool Equals(RuleValue? other) => other is not null && ValueAlgorithms.DeepEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => ValueAlgorithms.BoundedHash(this, ValueAlgorithms.HashDepth);

    /// <inheritdoc />
    public override string ToString() => ValueAlgorithms.Render(this);

    internal override bool AppendTo(StringBuilder sb)
    {
        sb.Append('[');
        for (int i = 0; i < _items.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            if (sb.Length > ValueAlgorithms.MaxRenderLength || !_items[i].AppendTo(sb))
            {
                return false;
            }
        }

        sb.Append(']');
        return sb.Length <= ValueAlgorithms.MaxRenderLength;
    }
}

/// <summary>Immutable map value; preserves insertion order for deterministic iteration.</summary>
public sealed class MapValue : RuleValue
{
    private readonly KeyValuePair<RuleValue, RuleValue>[] _entries;
    private readonly Dictionary<RuleValue, RuleValue> _lookup;

    private readonly long _weight;

    internal MapValue(KeyValuePair<RuleValue, RuleValue>[] entries)
    {
        _entries = entries;
        _lookup = new Dictionary<RuleValue, RuleValue>(entries.Length);
        long weight = 1;
        foreach (var e in entries)
        {
            _lookup[e.Key] = e.Value;
            weight = AddWeight(AddWeight(weight, e.Key.Weight), e.Value.Weight);
        }

        _weight = weight;
    }

    internal override long Weight => _weight;

    /// <summary>The entries in insertion order.</summary>
    public IReadOnlyList<KeyValuePair<RuleValue, RuleValue>> Entries => _entries;

    /// <inheritdoc />
    public override RuleTypeKind Kind => RuleTypeKind.Map;

    /// <summary>Number of entries.</summary>
    public int Count => _entries.Length;

    /// <summary>Looks up a key.</summary>
    public bool TryGetValue(RuleValue key, out RuleValue value)
    {
        if (_lookup.TryGetValue(key, out var v))
        {
            value = v;
            return true;
        }

        value = NullValue.Instance;
        return false;
    }

    /// <summary>Whether the key is present.</summary>
    public bool ContainsKey(RuleValue key) => _lookup.ContainsKey(key);

    /// <inheritdoc />
    public override bool Equals(RuleValue? other) => other is not null && ValueAlgorithms.DeepEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => ValueAlgorithms.BoundedHash(this, ValueAlgorithms.HashDepth);

    /// <inheritdoc />
    public override string ToString() => ValueAlgorithms.Render(this);

    internal override bool AppendTo(StringBuilder sb)
    {
        sb.Append('{');
        for (int i = 0; i < _entries.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            if (sb.Length > ValueAlgorithms.MaxRenderLength || !_entries[i].Key.AppendTo(sb))
            {
                return false;
            }

            sb.Append(": ");
            if (!_entries[i].Value.AppendTo(sb))
            {
                return false;
            }
        }

        sb.Append('}');
        return sb.Length <= ValueAlgorithms.MaxRenderLength;
    }
}

/// <summary>A value of an <see cref="ObjectSchema"/>. Create with <see cref="ObjectSchema.NewValue"/>.</summary>
public sealed class ObjectValue : RuleValue
{
    private readonly RuleValue[] _fields;

    private readonly long _weight;

    internal ObjectValue(ObjectSchema schema, RuleValue[] fields)
    {
        Schema = schema;
        _fields = fields;
        long weight = 1;
        foreach (var f in fields)
        {
            weight = AddWeight(weight, f.Weight);
        }

        _weight = weight;
    }

    internal override long Weight => _weight;

    /// <summary>The schema.</summary>
    public ObjectSchema Schema { get; }

    /// <inheritdoc />
    public override RuleTypeKind Kind => RuleTypeKind.Object;

    /// <summary>Gets a field value by name.</summary>
    public RuleValue Get(string field) =>
        Schema.TryGetField(field, out var def)
            ? _fields[def.Ordinal]
            : throw new ArgumentException($"schema '{Schema.Name}' has no field '{field}'", nameof(field));

    internal RuleValue GetByOrdinal(int ordinal) => _fields[ordinal];

    internal int FieldCount => _fields.Length;

    /// <inheritdoc />
    public override bool Equals(RuleValue? other) => other is not null && ValueAlgorithms.DeepEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => ValueAlgorithms.BoundedHash(this, ValueAlgorithms.HashDepth);

    /// <inheritdoc />
    public override string ToString() => ValueAlgorithms.Render(this);

    internal override bool AppendTo(StringBuilder sb)
    {
        sb.Append(Schema.Name).Append('{');
        for (int i = 0; i < _fields.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(Schema.Fields[i].Name).Append(": ");
            if (sb.Length > ValueAlgorithms.MaxRenderLength || !_fields[i].AppendTo(sb))
            {
                return false;
            }
        }

        sb.Append('}');
        return sb.Length <= ValueAlgorithms.MaxRenderLength;
    }
}

/// <summary>
/// Stands in for a value in an evaluation trace when the value is too large to record
/// (<see cref="RuleLimits.MaxTraceValueWeight"/>). It carries the kind and the size, never the content.
/// </summary>
public sealed class ElidedValue : RuleValue
{
    internal ElidedValue(RuleTypeKind kind, long originalWeight)
    {
        OriginalKind = kind;
        OriginalWeight = originalWeight;
    }

    /// <summary>The kind of the value that was elided.</summary>
    public RuleTypeKind OriginalKind { get; }

    /// <summary>The weight (tree size) of the value that was elided.</summary>
    public long OriginalWeight { get; }

    /// <summary>Always <see cref="RuleTypeKind.Dyn"/>: an elided value is a placeholder, not a value of the language.</summary>
    public override RuleTypeKind Kind => RuleTypeKind.Dyn;

    /// <inheritdoc />
    public override bool Equals(RuleValue? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"<{OriginalKind} elided: weight {OriginalWeight}>");
}

/// <summary>Bounded algorithms over value trees that may share sub-values (no exponential blow-up).</summary>
internal static class ValueAlgorithms
{
    /// <summary>Maximum length of a value's display text; longer renderings end in "...".</summary>
    public const int MaxRenderLength = 4096;

    /// <summary>Depth to which composite hashes look at elements (equal values always hash equally).</summary>
    public const int HashDepth = 3;

    private const int HashedElements = 4;

    public static string Render(RuleValue value)
    {
        var sb = new StringBuilder();
        if (!value.AppendTo(sb))
        {
            if (sb.Length > MaxRenderLength)
            {
                sb.Length = MaxRenderLength;
            }

            sb.Append("...");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Hash consistent with deep equality: count, weight (equal values have equal weights) and a few leading
    /// elements to a fixed depth. Cost is bounded by 4^depth.
    /// </summary>
    public static int BoundedHash(RuleValue value, int depth)
    {
        switch (value)
        {
            case ListValue l:
            {
                var h = new HashCode();
                h.Add(RuleTypeKind.List);
                h.Add(l.Array.Length);
                h.Add(l.Weight);
                for (int i = 0; depth > 0 && i < l.Array.Length && i < HashedElements; i++)
                {
                    h.Add(BoundedHash(l.Array[i], depth - 1));
                }

                return h.ToHashCode();
            }

            case MapValue m:
                return HashCode.Combine(RuleTypeKind.Map, m.Count, m.Weight);
            case ObjectValue o:
            {
                var h = new HashCode();
                h.Add(o.Schema.Name, StringComparer.Ordinal);
                h.Add(o.Weight);
                for (int i = 0; depth > 0 && i < o.FieldCount && i < HashedElements; i++)
                {
                    h.Add(BoundedHash(o.GetByOrdinal(i), depth - 1));
                }

                return h.ToHashCode();
            }

            default:
                return value.GetHashCode();
        }
    }

    /// <summary>
    /// Deep equality that remembers pairs of composite nodes already proven equal, so values built by sharing are
    /// compared in time proportional to their distinct nodes, not their (possibly exponential) tree size.
    /// </summary>
    public static bool DeepEquals(RuleValue a, RuleValue b)
    {
        HashSet<(RuleValue, RuleValue)>? memo = null;
        return DeepEquals(a, b, ref memo);
    }

    private static bool DeepEquals(RuleValue a, RuleValue b, ref HashSet<(RuleValue, RuleValue)>? memo)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is not (ListValue or MapValue or ObjectValue))
        {
            return a.Equals(b);
        }

        if (a.Weight != b.Weight || a.Kind != b.Kind)
        {
            return false;
        }

        if (memo is not null && memo.Contains((a, b)))
        {
            return true;
        }

        bool equal;
        switch (a)
        {
            case ListValue l:
            {
                var r = (ListValue)b;
                equal = l.Array.Length == r.Array.Length;
                for (int i = 0; equal && i < l.Array.Length; i++)
                {
                    equal = DeepEquals(l.Array[i], r.Array[i], ref memo);
                }

                break;
            }

            case MapValue m:
            {
                var r = (MapValue)b;
                equal = m.Count == r.Count;
                for (int i = 0; equal && i < m.Count; i++)
                {
                    var e = m.Entries[i];
                    equal = r.TryGetValue(e.Key, out var v) && DeepEquals(e.Value, v, ref memo);
                }

                break;
            }

            default:
            {
                var o = (ObjectValue)a;
                var r = (ObjectValue)b;
                equal = ReferenceEquals(o.Schema, r.Schema);
                for (int i = 0; equal && i < o.FieldCount; i++)
                {
                    equal = DeepEquals(o.GetByOrdinal(i), r.GetByOrdinal(i), ref memo);
                }

                break;
            }
        }

        if (equal)
        {
            (memo ??= new HashSet<(RuleValue, RuleValue)>(PairByReference.Instance)).Add((a, b));
        }

        return equal;
    }

    private sealed class PairByReference : IEqualityComparer<(RuleValue, RuleValue)>
    {
        public static readonly PairByReference Instance = new();

        public bool Equals((RuleValue, RuleValue) x, (RuleValue, RuleValue) y) =>
            ReferenceEquals(x.Item1, y.Item1) && ReferenceEquals(x.Item2, y.Item2);

        public int GetHashCode((RuleValue, RuleValue) obj) =>
            HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Item1), System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Item2));
    }
}
