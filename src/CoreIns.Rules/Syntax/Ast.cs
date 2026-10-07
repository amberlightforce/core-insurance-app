using System;
using System.Collections.Generic;

namespace CoreIns.Rules.Syntax;

/// <summary>Syntax tree node. Operators are represented as calls with CEL operator names (<c>_+_</c>, <c>_&amp;&amp;_</c>, <c>@in</c>, ...).</summary>
internal abstract class Expr
{
    public int Id { get; set; }

    public int Start { get; init; }

    public int End { get; init; }

    public int Depth { get; set; }

    public abstract IEnumerable<Expr> Children();
}

internal sealed class LiteralExpr : Expr
{
    public required RuleValue Value { get; init; }

    public override IEnumerable<Expr> Children() => Array.Empty<Expr>();
}

internal sealed class IdentExpr : Expr
{
    public required string Name { get; init; }

    public override IEnumerable<Expr> Children() => Array.Empty<Expr>();
}

internal sealed class SelectExpr : Expr
{
    public required Expr Operand { get; init; }

    public required string Field { get; init; }

    public override IEnumerable<Expr> Children()
    {
        yield return Operand;
    }
}

internal sealed class CallExpr : Expr
{
    public required string Function { get; init; }

    /// <summary>Receiver for member-style calls (<c>x.f(y)</c>); null for global calls and operators.</summary>
    public Expr? Target { get; init; }

    public required IReadOnlyList<Expr> Args { get; init; }

    public override IEnumerable<Expr> Children()
    {
        if (Target is not null)
        {
            yield return Target;
        }

        foreach (var a in Args)
        {
            yield return a;
        }
    }
}

internal sealed class ListExpr : Expr
{
    public required IReadOnlyList<Expr> Elements { get; init; }

    public override IEnumerable<Expr> Children() => Elements;
}

internal sealed class MapExpr : Expr
{
    public required IReadOnlyList<Expr> Keys { get; init; }

    public required IReadOnlyList<Expr> Values { get; init; }

    public override IEnumerable<Expr> Children()
    {
        for (int i = 0; i < Keys.Count; i++)
        {
            yield return Keys[i];
            yield return Values[i];
        }
    }
}

/// <summary><c>has(a.b)</c>: presence test.</summary>
internal sealed class HasExpr : Expr
{
    public required SelectExpr Select { get; init; }

    public override IEnumerable<Expr> Children()
    {
        yield return Select;
    }
}

internal enum MacroKind
{
    All,
    Exists,
    ExistsOne,
    Map,
    Filter,
}

/// <summary>Comprehension macros: <c>t.all(x, p)</c>, <c>t.exists(x, p)</c>, <c>t.exists_one(x, p)</c>, <c>t.map(x, f)</c>, <c>t.map(x, p, f)</c>, <c>t.filter(x, p)</c>.</summary>
internal sealed class MacroExpr : Expr
{
    public required MacroKind Kind { get; init; }

    public required Expr Target { get; init; }

    public required string Variable { get; init; }

    /// <summary>Filter predicate of the three-argument <c>map</c>; otherwise null.</summary>
    public Expr? Filter { get; init; }

    /// <summary>The predicate (all/exists/exists_one/filter) or the transform (map).</summary>
    public required Expr Body { get; init; }

    public override IEnumerable<Expr> Children()
    {
        yield return Target;
        if (Filter is not null)
        {
            yield return Filter;
        }

        yield return Body;
    }

    public static string NameOf(MacroKind kind) => kind switch
    {
        MacroKind.All => "all",
        MacroKind.Exists => "exists",
        MacroKind.ExistsOne => "exists_one",
        MacroKind.Map => "map",
        MacroKind.Filter => "filter",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
