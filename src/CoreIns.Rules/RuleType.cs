using System;

namespace CoreIns.Rules;

/// <summary>The kinds of types in the rule language.</summary>
public enum RuleTypeKind
{
    Int,
    Decimal,
    String,
    Bool,
    Null,
    Date,
    Timestamp,
    Duration,
    List,
    Map,
    Object,

    /// <summary>Statically unknown element type (only the element type of an empty list literal); checked at run time.</summary>
    Dyn,
}

/// <summary>A static type of the rule language. Instances are immutable and compared structurally.</summary>
public sealed class RuleType : IEquatable<RuleType>
{
    private RuleType(RuleTypeKind kind, RuleType? keyType, RuleType? elementType, ObjectSchema? schema)
    {
        Kind = kind;
        KeyType = keyType;
        ElementType = elementType;
        Schema = schema;
    }

    /// <summary>64-bit signed integer (CEL <c>int</c>).</summary>
    public static RuleType Int { get; } = new(RuleTypeKind.Int, null, null, null);

    /// <summary>Exact base-10 decimal (System.Decimal). Used for all money, rates and factors.</summary>
    public static RuleType Decimal { get; } = new(RuleTypeKind.Decimal, null, null, null);

    /// <summary>Unicode string.</summary>
    public static RuleType String { get; } = new(RuleTypeKind.String, null, null, null);

    /// <summary>Boolean.</summary>
    public static RuleType Bool { get; } = new(RuleTypeKind.Bool, null, null, null);

    /// <summary>The type of the <c>null</c> literal.</summary>
    public static RuleType Null { get; } = new(RuleTypeKind.Null, null, null, null);

    /// <summary>Calendar date without time or zone (business date).</summary>
    public static RuleType Date { get; } = new(RuleTypeKind.Date, null, null, null);

    /// <summary>UTC instant (CEL <c>google.protobuf.Timestamp</c>).</summary>
    public static RuleType Timestamp { get; } = new(RuleTypeKind.Timestamp, null, null, null);

    /// <summary>Signed time span (CEL <c>google.protobuf.Duration</c>).</summary>
    public static RuleType Duration { get; } = new(RuleTypeKind.Duration, null, null, null);

    /// <summary>Dynamic type (empty list element type).</summary>
    public static RuleType Dyn { get; } = new(RuleTypeKind.Dyn, null, null, null);

    /// <summary>The kind of this type.</summary>
    public RuleTypeKind Kind { get; }

    /// <summary>Key type of a map; otherwise null.</summary>
    public RuleType? KeyType { get; }

    /// <summary>Element type of a list, or value type of a map; otherwise null.</summary>
    public RuleType? ElementType { get; }

    /// <summary>Schema of an object type; otherwise null.</summary>
    public ObjectSchema? Schema { get; }

    /// <summary>True for <c>int</c> and <c>decimal</c>.</summary>
    public bool IsNumeric => Kind is RuleTypeKind.Int or RuleTypeKind.Decimal;

    /// <summary>Creates <c>list(element)</c>.</summary>
    public static RuleType ListOf(RuleType element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return new RuleType(RuleTypeKind.List, null, element, null);
    }

    /// <summary>Creates <c>map(key, value)</c>. Keys must be int, string or bool.</summary>
    public static RuleType MapOf(RuleType key, RuleType value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (key.Kind is not (RuleTypeKind.Int or RuleTypeKind.String or RuleTypeKind.Bool or RuleTypeKind.Dyn))
        {
            throw new ArgumentException("map keys must be int, string or bool", nameof(key));
        }

        return new RuleType(RuleTypeKind.Map, key, value, null);
    }

    /// <summary>Creates the type of objects conforming to <paramref name="schema"/>.</summary>
    public static RuleType ObjectOf(ObjectSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return new RuleType(RuleTypeKind.Object, null, null, schema);
    }

    /// <inheritdoc />
    public bool Equals(RuleType? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null
            && Kind == other.Kind
            && Equals(KeyType, other.KeyType)
            && Equals(ElementType, other.ElementType)
            && ReferenceEquals(Schema, other.Schema);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as RuleType);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, KeyType, ElementType, Schema?.Name);

    /// <summary>The canonical type name, for example <c>list(decimal)</c>.</summary>
    public override string ToString() => Kind switch
    {
        RuleTypeKind.Int => "int",
        RuleTypeKind.Decimal => "decimal",
        RuleTypeKind.String => "string",
        RuleTypeKind.Bool => "bool",
        RuleTypeKind.Null => "null_type",
        RuleTypeKind.Date => "date",
        RuleTypeKind.Timestamp => "timestamp",
        RuleTypeKind.Duration => "duration",
        RuleTypeKind.Dyn => "dyn",
        RuleTypeKind.List => "list(" + ElementType + ")",
        RuleTypeKind.Map => "map(" + KeyType + ", " + ElementType + ")",
        RuleTypeKind.Object => Schema!.Name,
        _ => throw new InvalidOperationException("unknown type kind"),
    };
}
