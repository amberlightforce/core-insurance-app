using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CoreIns.Rules.Runtime;
using CoreIns.Rules.Syntax;

namespace CoreIns.Rules.Checking;

/// <summary>
/// Type checker and binder: resolves identifiers, fields and functions against the declared input schema and the
/// adopted function set, checks types, inserts implicit int-to-decimal widening, and produces the bound tree.
/// Any unknown name or type error fails compilation with a position.
/// </summary>
internal sealed class Binder
{
    private static readonly HashSet<string> NonDeterministic = new(RuleLanguage.NonDeterministicNames, StringComparer.Ordinal);
    private static readonly HashSet<string> Unsupported = new(RuleLanguage.UnsupportedCelFunctions, StringComparer.Ordinal);
    private static readonly HashSet<string> Globals = new(RuleLanguage.GlobalFunctions, StringComparer.Ordinal);

    private readonly InputSchema _schema;
    private readonly IReadOnlyDictionary<string, HostFunction> _functions;
    private readonly RuleLimits _limits;
    private readonly List<Local> _locals = new();
    private int _nextSlot;

    public Binder(InputSchema schema, IReadOnlyDictionary<string, HostFunction> functions, RuleLimits limits)
    {
        _schema = schema;
        _functions = functions;
        _limits = limits;
        _nextSlot = schema.Variables.Count;
        SlotCount = _nextSlot;
    }

    /// <summary>Total slots needed (inputs plus comprehension variables).</summary>
    public int SlotCount { get; private set; }

    private readonly record struct Local(string Name, int Slot, RuleType Type);

    // ------------------------------------------------------------------ type relations

    public static RuleType? Unify(RuleType a, RuleType b)
    {
        if (a.Equals(b))
        {
            return a;
        }

        if (a.Kind == RuleTypeKind.Dyn || a.Kind == RuleTypeKind.Null)
        {
            return b;
        }

        if (b.Kind == RuleTypeKind.Dyn || b.Kind == RuleTypeKind.Null)
        {
            return a;
        }

        if (a.IsNumeric && b.IsNumeric)
        {
            return RuleType.Decimal;
        }

        if (a.Kind == RuleTypeKind.List && b.Kind == RuleTypeKind.List)
        {
            var e = Unify(a.ElementType!, b.ElementType!);
            return e is null ? null : RuleType.ListOf(e);
        }

        if (a.Kind == RuleTypeKind.Map && b.Kind == RuleTypeKind.Map)
        {
            var k = Unify(a.KeyType!, b.KeyType!);
            var v = Unify(a.ElementType!, b.ElementType!);
            return k is null || v is null || k.Kind is RuleTypeKind.Null ? null : RuleType.MapOf(k, v);
        }

        return null;
    }

    public static bool IsAssignable(RuleType from, RuleType to)
    {
        if (from.Equals(to) || from.Kind is RuleTypeKind.Dyn or RuleTypeKind.Null || to.Kind == RuleTypeKind.Dyn)
        {
            return true;
        }

        if (from.Kind == RuleTypeKind.Int && to.Kind == RuleTypeKind.Decimal)
        {
            return true;
        }

        if (from.Kind == RuleTypeKind.List && to.Kind == RuleTypeKind.List)
        {
            return IsAssignable(from.ElementType!, to.ElementType!);
        }

        return from.Kind == RuleTypeKind.Map && to.Kind == RuleTypeKind.Map
            && IsAssignable(from.KeyType!, to.KeyType!) && IsAssignable(from.ElementType!, to.ElementType!);
    }

    private static bool NeedsWidening(RuleType from, RuleType to) =>
        (from.Kind == RuleTypeKind.Int && to.Kind == RuleTypeKind.Decimal)
        || (from.Kind == RuleTypeKind.List && to.Kind == RuleTypeKind.List && NeedsWidening(from.ElementType!, to.ElementType!))
        || (from.Kind == RuleTypeKind.Map && to.Kind == RuleTypeKind.Map && NeedsWidening(from.ElementType!, to.ElementType!));

    /// <summary>Converts a node to <paramref name="target"/> (caller has checked assignability).</summary>
    public static BoundNode Coerce(BoundNode node, RuleType target)
    {
        if (node.Type.Kind == RuleTypeKind.Dyn && target.Kind is not (RuleTypeKind.Dyn or RuleTypeKind.Null))
        {
            return new CheckTypeNode(node, target);
        }

        if (!NeedsWidening(node.Type, target))
        {
            return node;
        }

        return node.Type.Kind == RuleTypeKind.Int ? new ToDecimalNode(node) : new WidenNode(node, target);
    }

    private static bool Comparable(RuleType a, RuleType b)
    {
        if (a.Equals(b) || a.Kind is RuleTypeKind.Dyn or RuleTypeKind.Null || b.Kind is RuleTypeKind.Dyn or RuleTypeKind.Null)
        {
            return true;
        }

        if (a.IsNumeric && b.IsNumeric)
        {
            return true;
        }

        if (a.Kind == RuleTypeKind.List && b.Kind == RuleTypeKind.List)
        {
            return Comparable(a.ElementType!, b.ElementType!);
        }

        return a.Kind == RuleTypeKind.Map && b.Kind == RuleTypeKind.Map
            && Comparable(a.KeyType!, b.KeyType!) && Comparable(a.ElementType!, b.ElementType!);
    }

    private static bool IsOrderable(RuleType t) =>
        t.Kind is RuleTypeKind.Int or RuleTypeKind.Decimal or RuleTypeKind.String or RuleTypeKind.Bool
            or RuleTypeKind.Date or RuleTypeKind.Timestamp or RuleTypeKind.Duration or RuleTypeKind.Dyn;

    private static bool Orderable(RuleType a, RuleType b)
    {
        if (!IsOrderable(a) || !IsOrderable(b))
        {
            return false;
        }

        return a.Kind == b.Kind || a.Kind == RuleTypeKind.Dyn || b.Kind == RuleTypeKind.Dyn || (a.IsNumeric && b.IsNumeric);
    }

    private static CompileFailure Fail(Expr e, RuleErrorCode code, string message) => new(code, e.Start, message);

    private static string PathOf(Expr e) => e switch
    {
        IdentExpr i => i.Name,
        SelectExpr s => PathOf(s.Operand) + "." + s.Field,
        _ => "(expression)",
    };

    // ------------------------------------------------------------------ binding

    public BoundNode Bind(Expr e) => e switch
    {
        LiteralExpr l => new ConstNode(l, LiteralType(l.Value), l.Value),
        IdentExpr i => BindIdent(i),
        SelectExpr s => BindSelect(s),
        HasExpr h => BindHas(h),
        ListExpr l => BindList(l),
        MapExpr m => BindMap(m),
        MacroExpr m => BindMacro(m),
        CallExpr c => BindCall(c),
        _ => throw new InvalidOperationException("unknown syntax node"),
    };

    private static RuleType LiteralType(RuleValue v) => v.Kind switch
    {
        RuleTypeKind.Int => RuleType.Int,
        RuleTypeKind.Decimal => RuleType.Decimal,
        RuleTypeKind.String => RuleType.String,
        RuleTypeKind.Bool => RuleType.Bool,
        _ => RuleType.Null,
    };

    private bool IsDeclared(string name) => _locals.Any(l => l.Name == name) || _schema.TryGetVariable(name, out _);

    private BoundNode BindIdent(IdentExpr i)
    {
        for (int k = _locals.Count - 1; k >= 0; k--)
        {
            if (_locals[k].Name == i.Name)
            {
                return new SlotNode(i, _locals[k].Type, _locals[k].Slot);
            }
        }

        if (_schema.TryGetVariable(i.Name, out var v))
        {
            return new SlotNode(i, v.Type, v.Slot);
        }

        if (NonDeterministic.Contains(i.Name))
        {
            throw Fail(i, RuleErrorCode.NonDeterministic, $"'{i.Name}' is not deterministic; pass the current date or time as a declared input from the time service");
        }

        throw Fail(i, RuleErrorCode.UnknownIdentifier, $"undeclared input '{i.Name}'");
    }

    private BoundNode BindSelect(SelectExpr s)
    {
        var operand = Bind(s.Operand);
        var t = operand.Type;
        if (t.Kind == RuleTypeKind.Object)
        {
            if (!t.Schema!.TryGetField(s.Field, out var f))
            {
                throw Fail(s, RuleErrorCode.UnknownField, $"unknown field '{PathOf(s)}': type {t.Schema.Name} has no field '{s.Field}'");
            }

            return new FieldNode(s, f.Type, operand, f.Ordinal, f.Name);
        }

        if (t.Kind == RuleTypeKind.Map && t.KeyType!.Kind is RuleTypeKind.String or RuleTypeKind.Dyn)
        {
            return new MapSelectNode(s, t.ElementType!, operand, new StringValue(s.Field));
        }

        throw Fail(s, RuleErrorCode.UnknownField, $"cannot select field '{PathOf(s)}' from a value of type {t}");
    }

    private BoundNode BindHas(HasExpr h)
    {
        var operand = Bind(h.Select.Operand);
        var t = operand.Type;
        if (t.Kind == RuleTypeKind.Object)
        {
            if (!t.Schema!.TryGetField(h.Select.Field, out var f))
            {
                throw Fail(h.Select, RuleErrorCode.UnknownField, $"unknown field '{PathOf(h.Select)}': type {t.Schema.Name} has no field '{h.Select.Field}'");
            }

            return new HasNode(h, operand, f.Ordinal, f.Name);
        }

        if (t.Kind == RuleTypeKind.Map && t.KeyType!.Kind is RuleTypeKind.String or RuleTypeKind.Dyn)
        {
            return new HasNode(h, operand, -1, h.Select.Field);
        }

        throw Fail(h, RuleErrorCode.NoMatchingOverload, $"has() requires an object or map with string keys, found {t}");
    }

    private BoundNode BindList(ListExpr l)
    {
        var elements = l.Elements.Select(Bind).ToArray();
        RuleType elementType = RuleType.Dyn;
        foreach (var el in elements)
        {
            elementType = Unify(elementType, el.Type)
                ?? throw Fail(el.Syntax, RuleErrorCode.TypeMismatch, $"list elements must have a common type; found {elementType} and {el.Type}");
        }

        for (int i = 0; i < elements.Length; i++)
        {
            elements[i] = Coerce(elements[i], elementType);
        }

        return new ListNode(l, RuleType.ListOf(elementType), elements);
    }

    private BoundNode BindMap(MapExpr m)
    {
        var keys = m.Keys.Select(Bind).ToArray();
        var values = m.Values.Select(Bind).ToArray();
        RuleType keyType = RuleType.Dyn, valueType = RuleType.Dyn;
        var constantKeys = new HashSet<RuleValue>();
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i].Type.Kind is not (RuleTypeKind.Int or RuleTypeKind.String or RuleTypeKind.Bool or RuleTypeKind.Dyn))
            {
                throw Fail(keys[i].Syntax, RuleErrorCode.TypeMismatch, $"map keys must be int, string or bool, found {keys[i].Type}");
            }

            if (keys[i] is ConstNode c && !constantKeys.Add(c.Value))
            {
                throw Fail(keys[i].Syntax, RuleErrorCode.DuplicateKey, $"duplicate map key {c.Value}");
            }

            keyType = Unify(keyType, keys[i].Type)
                ?? throw Fail(keys[i].Syntax, RuleErrorCode.TypeMismatch, $"map keys must have a common type; found {keyType} and {keys[i].Type}");
            valueType = Unify(valueType, values[i].Type)
                ?? throw Fail(values[i].Syntax, RuleErrorCode.TypeMismatch, $"map values must have a common type; found {valueType} and {values[i].Type}");
        }

        for (int i = 0; i < values.Length; i++)
        {
            values[i] = Coerce(values[i], valueType);
        }

        return new MapNode(m, RuleType.MapOf(keyType, valueType), keys, values);
    }

    private BoundNode BindMacro(MacroExpr m)
    {
        string name = MacroExpr.NameOf(m.Kind);
        var target = Bind(m.Target);
        RuleType iterType = target.Type.Kind switch
        {
            RuleTypeKind.List => target.Type.ElementType!,
            RuleTypeKind.Map => target.Type.KeyType!,
            RuleTypeKind.Dyn => RuleType.Dyn,
            _ => throw Fail(m.Target, RuleErrorCode.NoMatchingOverload, $"macro '{name}' requires a list or map, found {target.Type}"),
        };

        int slot = _nextSlot++;
        SlotCount = Math.Max(SlotCount, _nextSlot);
        _locals.Add(new Local(m.Variable, slot, iterType));
        try
        {
            var filter = m.Filter is null ? null : RequireBool(Bind(m.Filter), $"the filter of macro '{name}'");
            var body = Bind(m.Body);
            RuleType resultType;
            switch (m.Kind)
            {
                case MacroKind.Map:
                    resultType = RuleType.ListOf(body.Type);
                    break;
                case MacroKind.Filter:
                    body = RequireBool(body, $"the predicate of macro '{name}'");
                    resultType = RuleType.ListOf(iterType);
                    break;
                default:
                    body = RequireBool(body, $"the predicate of macro '{name}'");
                    resultType = RuleType.Bool;
                    break;
            }

            return new ComprehensionNode(m, resultType, m.Kind, target, slot, filter, body);
        }
        finally
        {
            _locals.RemoveAt(_locals.Count - 1);
            _nextSlot--;
        }
    }

    private static BoundNode RequireBool(BoundNode node, string what) => node.Type.Kind switch
    {
        RuleTypeKind.Bool => node,
        RuleTypeKind.Dyn => new CheckTypeNode(node, RuleType.Bool),
        _ => throw Fail(node.Syntax, RuleErrorCode.TypeMismatch, $"{what} must be bool but has type {node.Type}"),
    };

    // ------------------------------------------------------------------ calls and operators

    private BoundNode BindCall(CallExpr c)
    {
        switch (c.Function)
        {
            case "_&&_":
                return new AndNode(c, RequireBool(Bind(c.Args[0]), "the left operand of '&&'"), RequireBool(Bind(c.Args[1]), "the right operand of '&&'"));
            case "_||_":
                return new OrNode(c, RequireBool(Bind(c.Args[0]), "the left operand of '||'"), RequireBool(Bind(c.Args[1]), "the right operand of '||'"));
            case "!_":
                return new NotNode(c, RequireBool(Bind(c.Args[0]), "the operand of '!'"));
            case "_?_:_":
            {
                var cond = RequireBool(Bind(c.Args[0]), "the condition of '?:'");
                var whenTrue = Bind(c.Args[1]);
                var whenFalse = Bind(c.Args[2]);
                var type = Unify(whenTrue.Type, whenFalse.Type)
                    ?? throw Fail(c, RuleErrorCode.TypeMismatch, $"the branches of '?:' have incompatible types {whenTrue.Type} and {whenFalse.Type}");
                return new ConditionalNode(c, type, cond, Coerce(whenTrue, type), Coerce(whenFalse, type));
            }

            case "-_":
            {
                var operand = Bind(c.Args[0]);
                if (operand.Type.Kind is not (RuleTypeKind.Int or RuleTypeKind.Decimal or RuleTypeKind.Duration or RuleTypeKind.Dyn))
                {
                    throw Fail(c, RuleErrorCode.NoMatchingOverload, $"unary '-' is not defined for {operand.Type}");
                }

                return new UnaryNode(c, operand.Type, operand, Ops.Negate);
            }

            case "_+_":
            case "_-_":
            case "_*_":
            case "_/_":
            case "_%_":
                return BindArithmetic(c);
            case "_==_":
            case "_!=_":
            {
                var l = Bind(c.Args[0]);
                var r = Bind(c.Args[1]);
                if (!Comparable(l.Type, r.Type))
                {
                    throw Fail(c, RuleErrorCode.NoMatchingOverload, $"cannot compare {l.Type} with {r.Type}");
                }

                return new BinaryNode(c, RuleType.Bool, l, r, c.Function == "_==_" ? Ops.Equal : Ops.NotEqual);
            }

            case "_<_":
            case "_<=_":
            case "_>_":
            case "_>=_":
            {
                var l = Bind(c.Args[0]);
                var r = Bind(c.Args[1]);
                if (!Orderable(l.Type, r.Type))
                {
                    throw Fail(c, RuleErrorCode.NoMatchingOverload, $"operator '{c.Function.Trim('_')}' is not defined for ({l.Type}, {r.Type})");
                }

                Func<RuleValue, RuleValue, EvalState, RuleValue> op = c.Function switch
                {
                    "_<_" => Ops.Less,
                    "_<=_" => Ops.LessOrEqual,
                    "_>_" => Ops.Greater,
                    _ => Ops.GreaterOrEqual,
                };
                return new BinaryNode(c, RuleType.Bool, l, r, op);
            }

            case "@in":
            {
                var element = Bind(c.Args[0]);
                var collection = Bind(c.Args[1]);
                bool ok = collection.Type.Kind switch
                {
                    RuleTypeKind.List => Comparable(element.Type, collection.Type.ElementType!),
                    RuleTypeKind.Map => Comparable(element.Type, collection.Type.KeyType!),
                    RuleTypeKind.Dyn => true,
                    _ => false,
                };
                if (!ok)
                {
                    throw Fail(c, RuleErrorCode.NoMatchingOverload, $"operator 'in' is not defined for ({element.Type}, {collection.Type})");
                }

                return new BinaryNode(c, RuleType.Bool, element, collection, Ops.In);
            }

            case "_[_]":
            {
                var collection = Bind(c.Args[0]);
                var index = Bind(c.Args[1]);
                RuleType? type = collection.Type.Kind switch
                {
                    RuleTypeKind.List when index.Type.Kind is RuleTypeKind.Int or RuleTypeKind.Dyn => collection.Type.ElementType!,
                    RuleTypeKind.Map when Comparable(index.Type, collection.Type.KeyType!) && index.Type.Kind != RuleTypeKind.Null => collection.Type.ElementType!,
                    RuleTypeKind.Dyn => RuleType.Dyn,
                    _ => null,
                };
                if (type is null)
                {
                    throw Fail(c, RuleErrorCode.NoMatchingOverload, $"cannot index {collection.Type} with {index.Type}");
                }

                return new BinaryNode(c, type, collection, index, Ops.Index);
            }

            default:
                return c.Target is null ? BindGlobal(c) : BindMember(c);
        }
    }

    private BoundNode BindArithmetic(CallExpr c)
    {
        var l = Bind(c.Args[0]);
        var r = Bind(c.Args[1]);
        RuleType lt = l.Type, rt = r.Type;
        string op = c.Function.Trim('_');
        RuleType? result = null;
        if (lt.Kind == RuleTypeKind.Dyn || rt.Kind == RuleTypeKind.Dyn)
        {
            result = lt.Kind == RuleTypeKind.Null || rt.Kind == RuleTypeKind.Null ? null : RuleType.Dyn;
        }
        else if (lt.IsNumeric && rt.IsNumeric)
        {
            bool bothInt = lt.Kind == RuleTypeKind.Int && rt.Kind == RuleTypeKind.Int;
            result = op == "%" ? (bothInt ? RuleType.Int : null) : (bothInt ? RuleType.Int : RuleType.Decimal);
        }
        else if (op == "+")
        {
            result = (lt.Kind, rt.Kind) switch
            {
                (RuleTypeKind.String, RuleTypeKind.String) => RuleType.String,
                (RuleTypeKind.List, RuleTypeKind.List) => Unify(lt, rt),
                (RuleTypeKind.Timestamp, RuleTypeKind.Duration) => RuleType.Timestamp,
                (RuleTypeKind.Duration, RuleTypeKind.Timestamp) => RuleType.Timestamp,
                (RuleTypeKind.Duration, RuleTypeKind.Duration) => RuleType.Duration,
                _ => null,
            };
        }
        else if (op == "-")
        {
            result = (lt.Kind, rt.Kind) switch
            {
                (RuleTypeKind.Timestamp, RuleTypeKind.Timestamp) => RuleType.Duration,
                (RuleTypeKind.Timestamp, RuleTypeKind.Duration) => RuleType.Timestamp,
                (RuleTypeKind.Duration, RuleTypeKind.Duration) => RuleType.Duration,
                _ => null,
            };
        }

        if (result is null)
        {
            throw Fail(c, RuleErrorCode.NoMatchingOverload, $"operator '{op}' is not defined for ({lt}, {rt})");
        }

        if (result.Kind == RuleTypeKind.List)
        {
            l = Coerce(l, result);
            r = Coerce(r, result);
        }

        Func<RuleValue, RuleValue, EvalState, RuleValue> fn = op switch
        {
            "+" => Ops.Add,
            "-" => Ops.Subtract,
            "*" => Ops.Multiply,
            "/" => Ops.Divide,
            _ => Ops.Modulo,
        };
        return new BinaryNode(c, result, l, r, fn);
    }

    private void RequireArity(CallExpr c, int count)
    {
        if (c.Args.Count != count)
        {
            throw Fail(c, RuleErrorCode.NoMatchingOverload, string.Create(CultureInfo.InvariantCulture, $"function '{c.Function}' takes {count} argument(s) but got {c.Args.Count}"));
        }
    }

    private static BoundNode Expect(BoundNode node, string function, params RuleTypeKind[] kinds)
    {
        if (node.Type.Kind == RuleTypeKind.Dyn || kinds.Contains(node.Type.Kind))
        {
            return node;
        }

        throw Fail(node.Syntax, RuleErrorCode.NoMatchingOverload, $"function '{function}' does not accept an argument of type {node.Type}");
    }

    private static bool TryQualifiedName(Expr e, out string name, out string root)
    {
        switch (e)
        {
            case IdentExpr i:
                name = i.Name;
                root = i.Name;
                return true;
            case SelectExpr s when TryQualifiedName(s.Operand, out var prefix, out root):
                name = prefix + "." + s.Field;
                return true;
            default:
                name = string.Empty;
                root = string.Empty;
                return false;
        }
    }

    private BoundNode BindMember(CallExpr c)
    {
        if (TryQualifiedName(c.Target!, out var qualifier, out var root) && !IsDeclared(root))
        {
            string full = qualifier + "." + c.Function;
            if (_functions.TryGetValue(full, out var host))
            {
                return BindHost(c, host);
            }

            if (NonDeterministic.Contains(root))
            {
                throw Fail(c, RuleErrorCode.NonDeterministic, $"'{full}' is not deterministic; pass the current date or time as a declared input");
            }

            throw Fail(c, RuleErrorCode.UnknownFunction, $"undefined function '{full}'");
        }

        var target = Bind(c.Target!);
        switch (c.Function)
        {
            case "size":
                RequireArity(c, 0);
                return new UnaryNode(c, RuleType.Int, Expect(target, "size", RuleTypeKind.String, RuleTypeKind.List, RuleTypeKind.Map), Ops.Size);
            case "startsWith":
            case "endsWith":
            case "contains":
            {
                RequireArity(c, 1);
                var arg = Expect(Bind(c.Args[0]), c.Function, RuleTypeKind.String);
                Func<RuleValue, RuleValue, EvalState, RuleValue> op = c.Function switch
                {
                    "startsWith" => Ops.StartsWith,
                    "endsWith" => Ops.EndsWith,
                    _ => Ops.Contains,
                };
                return new BinaryNode(c, RuleType.Bool, Expect(target, c.Function, RuleTypeKind.String), arg, op);
            }

            case "matches":
                RequireArity(c, 1);
                return BindMatches(c, target, c.Args[0]);
            case "lowerAscii":
            case "upperAscii":
            case "trim":
            {
                RequireArity(c, 0);
                Func<RuleValue, EvalState, RuleValue> op = c.Function switch
                {
                    "lowerAscii" => Ops.LowerAscii,
                    "upperAscii" => Ops.UpperAscii,
                    _ => Ops.Trim,
                };
                return new UnaryNode(c, RuleType.String, Expect(target, c.Function, RuleTypeKind.String), op);
            }

            default:
                if (Unsupported.Contains(c.Function))
                {
                    throw Fail(c, RuleErrorCode.UnsupportedFeature, $"function '{c.Function}' is outside the adopted CEL subset");
                }

                if (Globals.Contains(c.Function))
                {
                    throw Fail(c, RuleErrorCode.UnknownFunction, $"function '{c.Function}' is a global function; call it as {c.Function}(...)");
                }

                throw Fail(c, RuleErrorCode.UnknownFunction, $"undefined member function '{c.Function}' on type {target.Type}");
        }
    }

    private BoundNode BindMatches(CallExpr c, BoundNode target, Expr patternSyntax)
    {
        target = Expect(target, "matches", RuleTypeKind.String);
        var pattern = Bind(patternSyntax);
        if (pattern is not ConstNode { Value: StringValue p })
        {
            throw Fail(patternSyntax, RuleErrorCode.InvalidArgument, "the regular expression of matches() must be a string literal");
        }

        if (p.Value.Length > _limits.MaxRegexPatternLength)
        {
            throw Fail(patternSyntax, RuleErrorCode.InvalidRegex, string.Create(CultureInfo.InvariantCulture, $"regular expression longer than {_limits.MaxRegexPatternLength} characters"));
        }

        Regex regex;
        try
        {
            regex = new Regex(p.Value, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, _limits.RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw Fail(patternSyntax, RuleErrorCode.InvalidRegex, "invalid regular expression: " + ex.Message);
        }
        catch (NotSupportedException ex)
        {
            throw Fail(patternSyntax, RuleErrorCode.InvalidRegex, "regular expression construct not supported (linear-time subset): " + ex.Message);
        }

        return new RegexMatchNode(c, target, regex);
    }

    private BoundNode BindGlobal(CallExpr c)
    {
        string name = c.Function;
        if (NonDeterministic.Contains(name))
        {
            throw Fail(c, RuleErrorCode.NonDeterministic, $"'{name}()' is not deterministic; pass the current date or time as a declared input from the time service");
        }

        if (Unsupported.Contains(name))
        {
            string hint = name == "double" ? " (numbers with a fraction are decimal; use decimal())" : string.Empty;
            throw Fail(c, RuleErrorCode.UnsupportedFeature, $"function '{name}' is outside the adopted CEL subset{hint}");
        }

        switch (name)
        {
            case "size":
                RequireArity(c, 1);
                return new UnaryNode(c, RuleType.Int, Expect(Bind(c.Args[0]), name, RuleTypeKind.String, RuleTypeKind.List, RuleTypeKind.Map), Ops.Size);
            case "int":
                RequireArity(c, 1);
                return new UnaryNode(c, RuleType.Int, Expect(Bind(c.Args[0]), name, RuleTypeKind.Int, RuleTypeKind.Decimal, RuleTypeKind.String), Ops.ToInt);
            case "decimal":
                RequireArity(c, 1);
                return new UnaryNode(c, RuleType.Decimal, Expect(Bind(c.Args[0]), name, RuleTypeKind.Int, RuleTypeKind.Decimal, RuleTypeKind.String), Ops.ToDecimal);
            case "string":
                RequireArity(c, 1);
                return new UnaryNode(
                    c,
                    RuleType.String,
                    Expect(Bind(c.Args[0]), name, RuleTypeKind.String, RuleTypeKind.Int, RuleTypeKind.Decimal, RuleTypeKind.Bool, RuleTypeKind.Date, RuleTypeKind.Timestamp, RuleTypeKind.Duration),
                    Ops.ToStringValue);
            case "date":
                RequireArity(c, 1);
                return FoldOrCall(c, RuleType.Date, Expect(Bind(c.Args[0]), name, RuleTypeKind.String, RuleTypeKind.Timestamp, RuleTypeKind.Date), Ops.ToDate);
            case "timestamp":
                RequireArity(c, 1);
                return FoldOrCall(c, RuleType.Timestamp, Expect(Bind(c.Args[0]), name, RuleTypeKind.String, RuleTypeKind.Timestamp), Ops.ToTimestamp);
            case "duration":
                RequireArity(c, 1);
                return FoldOrCall(c, RuleType.Duration, Expect(Bind(c.Args[0]), name, RuleTypeKind.String, RuleTypeKind.Duration), Ops.ToDuration);
            case "round":
                return BindRound(c);
            case "abs":
            {
                RequireArity(c, 1);
                var arg = Expect(Bind(c.Args[0]), name, RuleTypeKind.Int, RuleTypeKind.Decimal);
                return new UnaryNode(c, arg.Type, arg, Ops.Abs);
            }

            case "min":
            case "max":
                return BindMinMax(c);
            case "sum":
            {
                RequireArity(c, 1);
                var list = Bind(c.Args[0]);
                if (list.Type.Kind != RuleTypeKind.List || !(list.Type.ElementType!.IsNumeric || list.Type.ElementType.Kind == RuleTypeKind.Dyn))
                {
                    throw Fail(c.Args[0], RuleErrorCode.NoMatchingOverload, $"function 'sum' requires a list of numbers, found {list.Type}");
                }

                var elementType = list.Type.ElementType.Kind == RuleTypeKind.Dyn ? RuleType.Dyn : list.Type.ElementType;
                bool asDecimal = elementType.Kind == RuleTypeKind.Decimal;
                return new UnaryNode(c, elementType, list, (v, s) => Ops.Sum(v, asDecimal, s));
            }

            case "ageAt":
            case "yearsBetween":
            case "monthsBetween":
            case "daysBetween":
            {
                RequireArity(c, 2);
                var a = Expect(Bind(c.Args[0]), name, RuleTypeKind.Date);
                var b = Expect(Bind(c.Args[1]), name, RuleTypeKind.Date);
                Func<RuleValue, RuleValue, EvalState, RuleValue> op = name switch
                {
                    "ageAt" => Ops.AgeAt,
                    "yearsBetween" => Ops.YearsBetween,
                    "monthsBetween" => Ops.MonthsBetween,
                    _ => Ops.DaysBetween,
                };
                return new BinaryNode(c, RuleType.Int, a, b, op);
            }

            case "addDays":
            case "addMonths":
            case "addYears":
            {
                RequireArity(c, 2);
                var a = Expect(Bind(c.Args[0]), name, RuleTypeKind.Date);
                var b = Expect(Bind(c.Args[1]), name, RuleTypeKind.Int);
                Func<RuleValue, RuleValue, EvalState, RuleValue> op = name switch
                {
                    "addDays" => Ops.AddDays,
                    "addMonths" => Ops.AddMonths,
                    _ => Ops.AddYears,
                };
                return new BinaryNode(c, RuleType.Date, a, b, op);
            }

            case "year":
            case "month":
            case "day":
            case "dayOfWeek":
            {
                RequireArity(c, 1);
                Func<RuleValue, EvalState, RuleValue> op = name switch
                {
                    "year" => Ops.Year,
                    "month" => Ops.Month,
                    "day" => Ops.Day,
                    _ => Ops.DayOfWeek,
                };
                return new UnaryNode(c, RuleType.Int, Expect(Bind(c.Args[0]), name, RuleTypeKind.Date), op);
            }

            case "matches":
                RequireArity(c, 2);
                return BindMatches(c, Bind(c.Args[0]), c.Args[1]);
            default:
                if (_functions.TryGetValue(name, out var host))
                {
                    return BindHost(c, host);
                }

                if (RuleLanguage.MemberFunctions.Contains(name))
                {
                    throw Fail(c, RuleErrorCode.UnknownFunction, $"function '{name}' is a member function; call it as value.{name}(...)");
                }

                throw Fail(c, RuleErrorCode.UnknownFunction, $"undefined function '{name}'");
        }
    }

    private static BoundNode FoldOrCall(CallExpr c, RuleType type, BoundNode arg, Func<RuleValue, EvalState, RuleValue> op)
    {
        if (arg is ConstNode { Value: StringValue text })
        {
            RuleValue? folded = type.Kind switch
            {
                RuleTypeKind.Date => Ops.ParseDate(text.Value),
                RuleTypeKind.Timestamp => Ops.ParseTimestamp(text.Value),
                _ => Ops.ParseDuration(text.Value),
            };
            if (folded is null)
            {
                throw Fail(arg.Syntax, RuleErrorCode.InvalidLiteral, $"invalid {type} literal {text}");
            }

            return new ConstNode(c, type, folded);
        }

        return new UnaryNode(c, type, arg, op);
    }

    private BoundNode BindRound(CallExpr c)
    {
        RequireArity(c, 3);
        var value = Expect(Bind(c.Args[0]), "round", RuleTypeKind.Int, RuleTypeKind.Decimal);
        var places = Expect(Bind(c.Args[1]), "round", RuleTypeKind.Int);
        var modeNode = Bind(c.Args[2]);
        if (modeNode is not ConstNode { Value: StringValue modeText } || !DecimalRounding.TryParseMode(modeText.Value, out var mode))
        {
            throw Fail(c.Args[2], RuleErrorCode.InvalidArgument, "the rounding mode must be a string literal, one of: " + string.Join(", ", RuleLanguage.RoundingModes));
        }

        if (places is ConstNode { Value: IntValue p } && (p.Value < 0 || p.Value > DecimalRounding.MaxPlaces))
        {
            throw Fail(c.Args[1], RuleErrorCode.InvalidArgument, string.Create(CultureInfo.InvariantCulture, $"round places must be between 0 and {DecimalRounding.MaxPlaces}"));
        }

        return new BinaryNode(c, RuleType.Decimal, Coerce(value, RuleType.Decimal), places, (x, n, s) => Ops.Round(x, n, mode));
    }

    private BoundNode BindMinMax(CallExpr c)
    {
        string name = c.Function;
        bool max = name == "max";
        if (c.Args.Count == 1)
        {
            var list = Bind(c.Args[0]);
            if (list.Type.Kind != RuleTypeKind.List || !IsOrderable(list.Type.ElementType!))
            {
                throw Fail(c.Args[0], RuleErrorCode.NoMatchingOverload, $"function '{name}' requires a list of orderable values or two or more arguments, found {list.Type}");
            }

            return new UnaryNode(c, list.Type.ElementType!, list, (v, s) => Ops.MinOfList(v, name, max));
        }

        if (c.Args.Count < 2)
        {
            throw Fail(c, RuleErrorCode.NoMatchingOverload, $"function '{name}' takes a list or two or more arguments");
        }

        var args = c.Args.Select(Bind).ToArray();
        RuleType type = RuleType.Dyn;
        foreach (var a in args)
        {
            if (!IsOrderable(a.Type))
            {
                throw Fail(a.Syntax, RuleErrorCode.NoMatchingOverload, $"function '{name}' does not accept an argument of type {a.Type}");
            }

            type = Unify(type, a.Type)
                ?? throw Fail(a.Syntax, RuleErrorCode.NoMatchingOverload, $"function '{name}' arguments must have a common type; found {type} and {a.Type}");
        }

        for (int i = 0; i < args.Length; i++)
        {
            args[i] = Coerce(args[i], type);
        }

        return new CallNode(c, type, args, (v, s) => Ops.MinOf(v, name, max));
    }

    private BoundNode BindHost(CallExpr c, HostFunction host)
    {
        if (c.Args.Count != host.Parameters.Count)
        {
            throw Fail(c, RuleErrorCode.NoMatchingOverload, string.Create(CultureInfo.InvariantCulture, $"function '{host.Name}' takes {host.Parameters.Count} argument(s) but got {c.Args.Count}"));
        }

        var args = new BoundNode[c.Args.Count];
        for (int i = 0; i < args.Length; i++)
        {
            var a = Bind(c.Args[i]);
            if (!IsAssignable(a.Type, host.Parameters[i]))
            {
                throw Fail(c.Args[i], RuleErrorCode.NoMatchingOverload, $"argument {i + 1} of '{host.Name}' must be {host.Parameters[i]} but is {a.Type}");
            }

            args[i] = Coerce(a, host.Parameters[i]);
        }

        return new CallNode(c, host.ReturnType, args, (values, state) => InvokeHost(host, values));
    }

    private static RuleValue InvokeHost(HostFunction host, RuleValue[] values)
    {
        RuleValue result;
        try
        {
            result = host.Implementation(values);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw Ops.Fail(RuleErrorCode.HostFunctionFailed, $"function '{host.Name}' failed: {ex.Message}");
        }

        if (result is null || !ValueConformance.Matches(result, host.ReturnType))
        {
            throw Ops.Fail(RuleErrorCode.HostFunctionFailed, $"function '{host.Name}' returned a value that is not {host.ReturnType}");
        }

        return ValueConformance.Widen(result, host.ReturnType);
    }
}
