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

    /// <summary>Invariant, unambiguous display text (strings are quoted), used in traces.</summary>
    public abstract override string ToString();

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

    /// <inheritdoc />
    public override bool Equals(RuleValue? other) => other is StringValue s && string.Equals(s.Value, Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Quote(Value);
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

    internal ListValue(RuleValue[] items)
    {
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item, nameof(items));
        }

        _items = items;
    }

    /// <summary>The elements.</summary>
    public IReadOnlyList<RuleValue> Items => _items;

    /// <inheritdoc />
    public override RuleTypeKind Kind => RuleTypeKind.List;

    internal RuleValue[] Array => _items;

    /// <inheritdoc />
    public override bool Equals(RuleValue? other)
    {
        if (other is not ListValue l || l._items.Length != _items.Length)
        {
            return false;
        }

        for (int i = 0; i < _items.Length; i++)
        {
            if (!_items[i].Equals(l._items[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var item in _items)
        {
            h.Add(item);
        }

        return h.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => "[" + string.Join(", ", _items.Select(i => i.ToString())) + "]";
}

/// <summary>Immutable map value; preserves insertion order for deterministic iteration.</summary>
public sealed class MapValue : RuleValue
{
    private readonly KeyValuePair<RuleValue, RuleValue>[] _entries;
    private readonly Dictionary<RuleValue, RuleValue> _lookup;

    internal MapValue(KeyValuePair<RuleValue, RuleValue>[] entries)
    {
        _entries = entries;
        _lookup = new Dictionary<RuleValue, RuleValue>(entries.Length);
        foreach (var e in entries)
        {
            _lookup[e.Key] = e.Value;
        }
    }

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
    public override bool Equals(RuleValue? other)
    {
        if (other is not MapValue m || m._entries.Length != _entries.Length)
        {
            return false;
        }

        foreach (var e in _entries)
        {
            if (!m._lookup.TryGetValue(e.Key, out var v) || !v.Equals(e.Value))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        int h = _entries.Length;
        foreach (var e in _entries)
        {
            h ^= HashCode.Combine(e.Key, e.Value);
        }

        return h;
    }

    /// <inheritdoc />
    public override string ToString() => "{" + string.Join(", ", _entries.Select(e => e.Key + ": " + e.Value)) + "}";
}

/// <summary>A value of an <see cref="ObjectSchema"/>. Create with <see cref="ObjectSchema.NewValue"/>.</summary>
public sealed class ObjectValue : RuleValue
{
    private readonly RuleValue[] _fields;

    internal ObjectValue(ObjectSchema schema, RuleValue[] fields)
    {
        Schema = schema;
        _fields = fields;
    }

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

    /// <inheritdoc />
    public override bool Equals(RuleValue? other)
    {
        if (other is not ObjectValue o || !ReferenceEquals(o.Schema, Schema))
        {
            return false;
        }

        for (int i = 0; i < _fields.Length; i++)
        {
            if (!_fields[i].Equals(o._fields[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Schema.Name, StringComparer.Ordinal);
        foreach (var f in _fields)
        {
            h.Add(f);
        }

        return h.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() =>
        Schema.Name + "{" + string.Join(", ", Schema.Fields.Select(f => f.Name + ": " + _fields[f.Ordinal])) + "}";
}
