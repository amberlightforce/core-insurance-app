using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using CoreIns.Rules.Syntax;

namespace CoreIns.Rules.Runtime;

/// <summary>
/// A type-checked, resolved node: the compiled form of an expression. Immutable and therefore safe to share between
/// threads; all per-evaluation state lives in <see cref="EvalState"/>.
/// </summary>
internal abstract class BoundNode
{
    protected BoundNode(Expr syntax, RuleType type)
    {
        Syntax = syntax;
        Type = type;
    }

    public Expr Syntax { get; }

    public RuleType Type { get; }

    /// <summary>Whether the node's value appears in the evaluation trace (constants and implicit conversions do not).</summary>
    public virtual bool Traceable => true;

    public RuleValue Eval(EvalState state)
    {
        state.Tick(this);
        RuleValue value;
        try
        {
            value = EvalCore(state);
        }
        catch (EvalFailure failure)
        {
            failure.Node ??= this;
            throw;
        }

        state.Record(this, value);
        return value;
    }

    protected abstract RuleValue EvalCore(EvalState state);
}

internal sealed class ConstNode : BoundNode
{
    private readonly RuleValue _value;

    public ConstNode(Expr syntax, RuleType type, RuleValue value)
        : base(syntax, type) => _value = value;

    public RuleValue Value => _value;

    public override bool Traceable => false;

    protected override RuleValue EvalCore(EvalState state) => _value;
}

internal sealed class SlotNode : BoundNode
{
    private readonly int _slot;

    public SlotNode(Expr syntax, RuleType type, int slot)
        : base(syntax, type) => _slot = slot;

    protected override RuleValue EvalCore(EvalState state) => state.Slots[_slot];
}

internal sealed class FieldNode : BoundNode
{
    private readonly BoundNode _operand;
    private readonly int _ordinal;
    private readonly string _field;

    public FieldNode(Expr syntax, RuleType type, BoundNode operand, int ordinal, string field)
        : base(syntax, type)
    {
        _operand = operand;
        _ordinal = ordinal;
        _field = field;
    }

    protected override RuleValue EvalCore(EvalState state) => _operand.Eval(state) switch
    {
        ObjectValue o => o.GetByOrdinal(_ordinal),

        // The checker binds FieldNode only to object-typed operands, so anything else is null.
        _ => throw Ops.Fail(RuleErrorCode.NullValue, $"cannot select field '{_field}' from null"),
    };
}

internal sealed class MapSelectNode : BoundNode
{
    private readonly BoundNode _operand;
    private readonly StringValue _key;

    public MapSelectNode(Expr syntax, RuleType type, BoundNode operand, StringValue key)
        : base(syntax, type)
    {
        _operand = operand;
        _key = key;
    }

    protected override RuleValue EvalCore(EvalState state) => _operand.Eval(state) switch
    {
        MapValue m => m.TryGetValue(_key, out var v) ? v : throw Ops.Fail(RuleErrorCode.NoSuchKey, $"no such key: {_key}"),

        // The checker binds MapSelectNode only to map-typed operands, so anything else is null.
        _ => throw Ops.Fail(RuleErrorCode.NullValue, $"cannot select key {_key} from null"),
    };
}

internal sealed class HasNode : BoundNode
{
    private readonly BoundNode _operand;
    private readonly int _ordinal;
    private readonly StringValue _name;

    public HasNode(Expr syntax, BoundNode operand, int ordinal, string name)
        : base(syntax, RuleType.Bool)
    {
        _operand = operand;
        _ordinal = ordinal;
        _name = new StringValue(name);
    }

    protected override RuleValue EvalCore(EvalState state) => _operand.Eval(state) switch
    {
        ObjectValue o => BoolValue.Of(o.GetByOrdinal(_ordinal) is not NullValue),
        MapValue m => BoolValue.Of(m.ContainsKey(_name)),

        // The checker binds HasNode only to object- or map-typed operands, so anything else is null.
        _ => throw Ops.Fail(RuleErrorCode.NullValue, $"has(): cannot test field {_name} on null"),
    };
}

internal sealed class AndNode : BoundNode
{
    private readonly BoundNode _left;
    private readonly BoundNode _right;

    public AndNode(Expr syntax, BoundNode left, BoundNode right)
        : base(syntax, RuleType.Bool)
    {
        _left = left;
        _right = right;
    }

    protected override RuleValue EvalCore(EvalState state)
    {
        EvalFailure? leftError = null;
        try
        {
            if (!Ops.AsBool(_left.Eval(state), "left operand of '&&'"))
            {
                return BoolValue.False;
            }
        }
        catch (EvalFailure f) when (f.Absorbable)
        {
            leftError = f;
        }

        bool right;
        try
        {
            right = Ops.AsBool(_right.Eval(state), "right operand of '&&'");
        }
        catch (EvalFailure f) when (leftError is not null && f.Absorbable)
        {
            throw leftError;
        }

        if (!right)
        {
            return BoolValue.False;
        }

        return leftError is null ? BoolValue.True : throw leftError;
    }
}

internal sealed class OrNode : BoundNode
{
    private readonly BoundNode _left;
    private readonly BoundNode _right;

    public OrNode(Expr syntax, BoundNode left, BoundNode right)
        : base(syntax, RuleType.Bool)
    {
        _left = left;
        _right = right;
    }

    protected override RuleValue EvalCore(EvalState state)
    {
        EvalFailure? leftError = null;
        try
        {
            if (Ops.AsBool(_left.Eval(state), "left operand of '||'"))
            {
                return BoolValue.True;
            }
        }
        catch (EvalFailure f) when (f.Absorbable)
        {
            leftError = f;
        }

        bool right;
        try
        {
            right = Ops.AsBool(_right.Eval(state), "right operand of '||'");
        }
        catch (EvalFailure f) when (leftError is not null && f.Absorbable)
        {
            throw leftError;
        }

        if (right)
        {
            return BoolValue.True;
        }

        return leftError is null ? BoolValue.False : throw leftError;
    }
}

internal sealed class NotNode : BoundNode
{
    private readonly BoundNode _operand;

    public NotNode(Expr syntax, BoundNode operand)
        : base(syntax, RuleType.Bool) => _operand = operand;

    protected override RuleValue EvalCore(EvalState state) => BoolValue.Of(!Ops.AsBool(_operand.Eval(state), "operand of '!'"));
}

internal sealed class ConditionalNode : BoundNode
{
    private readonly BoundNode _condition;
    private readonly BoundNode _whenTrue;
    private readonly BoundNode _whenFalse;

    public ConditionalNode(Expr syntax, RuleType type, BoundNode condition, BoundNode whenTrue, BoundNode whenFalse)
        : base(syntax, type)
    {
        _condition = condition;
        _whenTrue = whenTrue;
        _whenFalse = whenFalse;
    }

    protected override RuleValue EvalCore(EvalState state) =>
        Ops.AsBool(_condition.Eval(state), "condition of '?:'") ? _whenTrue.Eval(state) : _whenFalse.Eval(state);
}

internal sealed class UnaryNode : BoundNode
{
    private readonly BoundNode _operand;
    private readonly Func<RuleValue, EvalState, RuleValue> _op;

    public UnaryNode(Expr syntax, RuleType type, BoundNode operand, Func<RuleValue, EvalState, RuleValue> op)
        : base(syntax, type)
    {
        _operand = operand;
        _op = op;
    }

    protected override RuleValue EvalCore(EvalState state) => _op(_operand.Eval(state), state);
}

internal sealed class BinaryNode : BoundNode
{
    private readonly BoundNode _left;
    private readonly BoundNode _right;
    private readonly Func<RuleValue, RuleValue, EvalState, RuleValue> _op;

    public BinaryNode(Expr syntax, RuleType type, BoundNode left, BoundNode right, Func<RuleValue, RuleValue, EvalState, RuleValue> op)
        : base(syntax, type)
    {
        _left = left;
        _right = right;
        _op = op;
    }

    protected override RuleValue EvalCore(EvalState state)
    {
        var l = _left.Eval(state);
        var r = _right.Eval(state);
        return _op(l, r, state);
    }
}

internal sealed class CallNode : BoundNode
{
    private readonly BoundNode[] _args;
    private readonly Func<RuleValue[], EvalState, RuleValue> _impl;

    public CallNode(Expr syntax, RuleType type, BoundNode[] args, Func<RuleValue[], EvalState, RuleValue> impl)
        : base(syntax, type)
    {
        _args = args;
        _impl = impl;
    }

    protected override RuleValue EvalCore(EvalState state)
    {
        var values = new RuleValue[_args.Length];
        for (int i = 0; i < _args.Length; i++)
        {
            values[i] = _args[i].Eval(state);
        }

        return _impl(values, state);
    }
}

internal sealed class ListNode : BoundNode
{
    private readonly BoundNode[] _elements;

    public ListNode(Expr syntax, RuleType type, BoundNode[] elements)
        : base(syntax, type) => _elements = elements;

    protected override RuleValue EvalCore(EvalState state)
    {
        state.Allocate(_elements.Length);
        var values = new RuleValue[_elements.Length];
        for (int i = 0; i < _elements.Length; i++)
        {
            values[i] = _elements[i].Eval(state);
        }

        return new ListValue(values);
    }
}

internal sealed class MapNode : BoundNode
{
    private readonly BoundNode[] _keys;
    private readonly BoundNode[] _values;

    public MapNode(Expr syntax, RuleType type, BoundNode[] keys, BoundNode[] values)
        : base(syntax, type)
    {
        _keys = keys;
        _values = values;
    }

    protected override RuleValue EvalCore(EvalState state)
    {
        state.Allocate(_keys.Length);
        var entries = new KeyValuePair<RuleValue, RuleValue>[_keys.Length];
        var seen = new HashSet<RuleValue>();
        for (int i = 0; i < _keys.Length; i++)
        {
            var k = _keys[i].Eval(state);
            if (k is not (IntValue or StringValue or BoolValue))
            {
                throw Ops.Fail(k is NullValue ? RuleErrorCode.NullValue : RuleErrorCode.InvalidValue, "map keys must be int, string or bool");
            }

            if (!seen.Add(k))
            {
                throw Ops.Fail(RuleErrorCode.DuplicateKey, $"duplicate map key {k}");
            }

            entries[i] = new KeyValuePair<RuleValue, RuleValue>(k, _values[i].Eval(state));
        }

        return new MapValue(entries);
    }
}

internal sealed class ComprehensionNode : BoundNode
{
    private readonly MacroKind _kind;
    private readonly BoundNode _target;
    private readonly int _slot;
    private readonly BoundNode? _filter;
    private readonly BoundNode _body;

    public ComprehensionNode(Expr syntax, RuleType type, MacroKind kind, BoundNode target, int slot, BoundNode? filter, BoundNode body)
        : base(syntax, type)
    {
        _kind = kind;
        _target = target;
        _slot = slot;
        _filter = filter;
        _body = body;
    }

    protected override RuleValue EvalCore(EvalState state)
    {
        var target = _target.Eval(state);
        IReadOnlyList<RuleValue> items = target switch
        {
            ListValue l => l.Items,
            MapValue m => KeysOf(m, state),
            NullValue => throw Ops.Fail(RuleErrorCode.NullValue, $"macro '{MacroExpr.NameOf(_kind)}' applied to null"),
            _ => throw Ops.Fail(RuleErrorCode.InvalidValue, $"macro '{MacroExpr.NameOf(_kind)}' requires a list or map"),
        };

        var saved = state.Slots[_slot];
        try
        {
            return _kind switch
            {
                MacroKind.All => (RuleValue)Quantify(state, items, decisive: false),
                MacroKind.Exists => Quantify(state, items, decisive: true),
                MacroKind.ExistsOne => ExistsOne(state, items),
                MacroKind.Filter => Filter(state, items),
                _ => Map(state, items),
            };
        }
        finally
        {
            state.Slots[_slot] = saved;
        }
    }

    private static RuleValue[] KeysOf(MapValue m, EvalState state)
    {
        state.Allocate(m.Count);
        var keys = new RuleValue[m.Count];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = m.Entries[i].Key;
        }

        return keys;
    }

    private static bool Test(EvalState state, BoundNode predicate, string what) => Ops.AsBool(predicate.Eval(state), what);

    /// <summary>all (decisive = false) and exists (decisive = true), with CEL error absorption.</summary>
    private BoolValue Quantify(EvalState state, IReadOnlyList<RuleValue> items, bool decisive)
    {
        EvalFailure? deferred = null;
        foreach (var item in items)
        {
            state.Tick(this);
            state.Slots[_slot] = item;
            try
            {
                if (Test(state, _body, "predicate") == decisive)
                {
                    return BoolValue.Of(decisive);
                }
            }
            catch (EvalFailure f) when (f.Absorbable)
            {
                deferred ??= f;
            }
        }

        return deferred is null ? BoolValue.Of(!decisive) : throw deferred;
    }

    private BoolValue ExistsOne(EvalState state, IReadOnlyList<RuleValue> items)
    {
        int count = 0;
        foreach (var item in items)
        {
            state.Tick(this);
            state.Slots[_slot] = item;
            if (Test(state, _body, "predicate"))
            {
                count++;
            }
        }

        return BoolValue.Of(count == 1);
    }

    private ListValue Filter(EvalState state, IReadOnlyList<RuleValue> items)
    {
        var result = new List<RuleValue>();
        foreach (var item in items)
        {
            state.Tick(this);
            state.Slots[_slot] = item;
            if (Test(state, _body, "filter predicate"))
            {
                state.Allocate(1);
                result.Add(item);
            }
        }

        return new ListValue(result.ToArray());
    }

    private ListValue Map(EvalState state, IReadOnlyList<RuleValue> items)
    {
        var result = new List<RuleValue>(items.Count);
        foreach (var item in items)
        {
            state.Tick(this);
            state.Slots[_slot] = item;
            if (_filter is not null && !Test(state, _filter, "map filter"))
            {
                continue;
            }

            var value = _body.Eval(state);
            state.Allocate(1);
            result.Add(value);
        }

        return new ListValue(result.ToArray());
    }
}

/// <summary>Implicit int to decimal widening (inserted by the checker).</summary>
internal sealed class ToDecimalNode : BoundNode
{
    private readonly BoundNode _operand;

    public ToDecimalNode(BoundNode operand)
        : base(operand.Syntax, RuleType.Decimal) => _operand = operand;

    public override bool Traceable => false;

    protected override RuleValue EvalCore(EvalState state)
    {
        var v = _operand.Eval(state);
        return v is IntValue i ? new DecimalValue(i.Value) : v;
    }
}

/// <summary>Deep widening of collections (for example list(int) to list(decimal)).</summary>
internal sealed class WidenNode : BoundNode
{
    private readonly BoundNode _operand;

    public WidenNode(BoundNode operand, RuleType type)
        : base(operand.Syntax, type) => _operand = operand;

    public override bool Traceable => false;

    protected override RuleValue EvalCore(EvalState state)
    {
        var value = _operand.Eval(state);
        state.Allocate(value.Weight);
        return ValueConformance.Widen(value, Type);
    }
}

/// <summary>Run-time type check where the static type is dyn.</summary>
internal sealed class CheckTypeNode : BoundNode
{
    private readonly BoundNode _operand;

    public CheckTypeNode(BoundNode operand, RuleType type)
        : base(operand.Syntax, type) => _operand = operand;

    public override bool Traceable => false;

    protected override RuleValue EvalCore(EvalState state)
    {
        var raw = _operand.Eval(state);
        state.Allocate(raw.Weight);
        var v = ValueConformance.Widen(raw, Type);
        return ValueConformance.Matches(v, Type)
            ? v
            : throw Ops.Fail(RuleErrorCode.InvalidValue, $"value of kind {v.Kind} where {Type} is required");
    }
}

internal sealed class RegexMatchNode : BoundNode
{
    private readonly BoundNode _target;
    private readonly Regex _regex;

    public RegexMatchNode(Expr syntax, BoundNode target, Regex regex)
        : base(syntax, RuleType.Bool)
    {
        _target = target;
        _regex = regex;
    }

    protected override RuleValue EvalCore(EvalState state)
    {
        var v = _target.Eval(state);
        if (v is not StringValue s)
        {
            throw Ops.Fail(v is NullValue ? RuleErrorCode.NullValue : RuleErrorCode.InvalidValue, "matches() requires a string");
        }

        state.ChargeText(s.Value);
        try
        {
            return BoolValue.Of(_regex.IsMatch(s.Value));
        }
        catch (RegexMatchTimeoutException)
        {
            throw Ops.Fail(
                RuleErrorCode.RegexTimeout,
                string.Create(CultureInfo.InvariantCulture, $"regular expression match exceeded {_regex.MatchTimeout.Ticks / TimeSpan.TicksPerMillisecond} ms"));
        }
    }
}

/// <summary>Enforces a non-nullable expected result type at run time (ruling D-ARC-10c).</summary>
internal sealed class NonNullNode : BoundNode
{
    private readonly BoundNode _operand;

    public NonNullNode(BoundNode operand)
        : base(operand.Syntax, operand.Type) => _operand = operand;

    public override bool Traceable => false;

    protected override RuleValue EvalCore(EvalState state)
    {
        var v = _operand.Eval(state);
        return v is NullValue
            ? throw Ops.Fail(RuleErrorCode.NullValue, $"the expression produced null but its declared result type {Type} is not nullable")
            : v;
    }
}
