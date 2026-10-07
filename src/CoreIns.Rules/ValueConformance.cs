using System;
using System.Collections.Generic;
using System.Globalization;

namespace CoreIns.Rules;

/// <summary>Checks and coerces runtime values against static types.</summary>
internal static class ValueConformance
{
    /// <summary>Validates an input value against a declared type, widening int to decimal. Throws <see cref="RuleInputException"/>.</summary>
    /// <summary>Largest weight accepted for one input or field value (inputs are traversed when validated).</summary>
    public const long MaxInputWeight = 10_000_000;

    /// <summary>Validates a top-level input or field value: refuses oversized values before traversing them.</summary>
    public static RuleValue ConformInput(RuleValue value, RuleType type, bool nullable, string path)
    {
        if (value.Depth > RuleLimits.InputDepthCeiling)
        {
            throw new RuleInputException(path, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"value nesting depth {value.Depth} exceeds the maximum of {RuleLimits.InputDepthCeiling}"));
        }

        if (value.Weight > MaxInputWeight)
        {
            throw new RuleInputException(path, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"value weight {value.Weight} exceeds the maximum of {MaxInputWeight}"));
        }

        return Conform(value, type, nullable, path);
    }

    public static RuleValue Conform(RuleValue value, RuleType type, bool nullable, string path)
    {
        if (value is NullValue)
        {
            if (nullable || type.Kind is RuleTypeKind.Null or RuleTypeKind.Dyn)
            {
                return value;
            }

            throw new RuleInputException(path, $"null is not allowed for non-nullable type {type}");
        }

        switch (type.Kind)
        {
            case RuleTypeKind.Dyn:
                return value;
            case RuleTypeKind.Decimal when value is IntValue i:
                return new DecimalValue(i.Value);
            case RuleTypeKind.List when value is ListValue list:
            {
                RuleValue[]? copy = null;
                var items = list.Array;
                for (int k = 0; k < items.Length; k++)
                {
                    var c = Conform(items[k], type.ElementType!, false, path + "[" + k.ToString(CultureInfo.InvariantCulture) + "]");
                    if (!ReferenceEquals(c, items[k]))
                    {
                        copy ??= (RuleValue[])items.Clone();
                        copy[k] = c;
                    }
                }

                return copy is null ? value : new ListValue(copy);
            }

            case RuleTypeKind.Map when value is MapValue map:
            {
                var entries = new KeyValuePair<RuleValue, RuleValue>[map.Count];
                bool changed = false;
                for (int k = 0; k < map.Count; k++)
                {
                    var e = map.Entries[k];
                    var key = Conform(e.Key, type.KeyType!, false, path + ".key");
                    var val = Conform(e.Value, type.ElementType!, true, path + "[" + e.Key + "]");
                    changed |= !ReferenceEquals(key, e.Key) || !ReferenceEquals(val, e.Value);
                    entries[k] = new KeyValuePair<RuleValue, RuleValue>(key, val);
                }

                return changed ? new MapValue(entries) : value;
            }

            case RuleTypeKind.Object when value is ObjectValue o:
                if (!ReferenceEquals(o.Schema, type.Schema))
                {
                    throw new RuleInputException(path, $"expected an object of schema {type.Schema!.Name} but got {o.Schema.Name}");
                }

                return value;
            default:
                if (value.Kind == type.Kind)
                {
                    return value;
                }

                throw new RuleInputException(path, $"expected {type} but got a value of kind {value.Kind}");
        }
    }

    /// <summary>Deep int-to-decimal widening used at run time by coercion nodes (no validation).</summary>
    public static RuleValue Widen(RuleValue value, RuleType type)
    {
        switch (type.Kind)
        {
            case RuleTypeKind.Decimal when value is IntValue i:
                return new DecimalValue(i.Value);
            case RuleTypeKind.List when value is ListValue list:
            {
                var items = list.Array;
                var copy = new RuleValue[items.Length];
                for (int k = 0; k < items.Length; k++)
                {
                    copy[k] = Widen(items[k], type.ElementType!);
                }

                return new ListValue(copy);
            }

            case RuleTypeKind.Map when value is MapValue map:
            {
                var entries = new KeyValuePair<RuleValue, RuleValue>[map.Count];
                for (int k = 0; k < map.Count; k++)
                {
                    var e = map.Entries[k];
                    entries[k] = new KeyValuePair<RuleValue, RuleValue>(e.Key, Widen(e.Value, type.ElementType!));
                }

                return new MapValue(entries);
            }

            default:
                return value;
        }
    }

    /// <summary>Whether a runtime value matches a static type (null matches every type).</summary>
    public static bool Matches(RuleValue value, RuleType type)
    {
        if (value is NullValue || type.Kind == RuleTypeKind.Dyn)
        {
            return true;
        }

        return type.Kind switch
        {
            RuleTypeKind.Decimal => value is DecimalValue or IntValue,
            RuleTypeKind.List => value is ListValue l && AllMatch(l.Items, type.ElementType!),
            RuleTypeKind.Map => value is MapValue m && AllMatch(m, type),
            RuleTypeKind.Object => value is ObjectValue o && ReferenceEquals(o.Schema, type.Schema),
            _ => value.Kind == type.Kind,
        };
    }

    private static bool AllMatch(IReadOnlyList<RuleValue> items, RuleType element)
    {
        foreach (var i in items)
        {
            if (!Matches(i, element))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AllMatch(MapValue map, RuleType type)
    {
        foreach (var e in map.Entries)
        {
            if (!Matches(e.Key, type.KeyType!) || !Matches(e.Value, type.ElementType!))
            {
                return false;
            }
        }

        return true;
    }
}
