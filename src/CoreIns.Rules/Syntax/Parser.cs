using System;
using System.Collections.Generic;
using System.Globalization;

namespace CoreIns.Rules.Syntax;

/// <summary>
/// Recursive-descent parser for the CEL subset. Grammar (CEL precedence, lowest first):
/// <code>
/// Expr     = Or ["?" Or ":" Expr]
/// Or       = And {"||" And}
/// And      = Relation {"&amp;&amp;" Relation}
/// Relation = Add {("&lt;"|"&lt;="|"&gt;"|"&gt;="|"=="|"!="|"in") Add}
/// Add      = Mul {("+"|"-") Mul}
/// Mul      = Unary {("*"|"/"|"%") Unary}
/// Unary    = Member | "!" Unary | "-" Unary
/// Member   = Primary {"." IDENT ["(" [Args] ")"] | "[" Expr "]"}
/// Primary  = IDENT ["(" [Args] ")"] | "(" Expr ")" | "[" [Exprs] [","] "]" | "{" [Entries] [","] "}" | Literal
/// </code>
/// </summary>
internal sealed class Parser
{
    private readonly List<Token> _tokens;
    private readonly RuleLimits _limits;
    private int _p;
    private int _nextId;
    private int _recursion;

    private Parser(List<Token> tokens, RuleLimits limits)
    {
        _tokens = tokens;
        _limits = limits;
    }

    private Token Current => _tokens[_p];

    public static Expr Parse(string source, RuleLimits limits)
    {
        if (source.Length > limits.MaxExpressionLength)
        {
            throw new CompileFailure(
                RuleErrorCode.ExpressionTooLong,
                0,
                string.Create(CultureInfo.InvariantCulture, $"expression length {source.Length} exceeds the maximum of {limits.MaxExpressionLength} characters"));
        }

        var parser = new Parser(Lexer.Tokenize(source), limits);
        if (parser.Current.Kind == TokenKind.Eof)
        {
            throw new CompileFailure(RuleErrorCode.Syntax, 0, "expression is empty");
        }

        var expr = parser.ParseExpr();
        if (parser.Current.Kind != TokenKind.Eof)
        {
            throw parser.Unexpected();
        }

        return expr;
    }

    private static string Describe(Token t) => t.Kind == TokenKind.Eof ? "end of expression" : "'" + t.Text + "'";

    private CompileFailure Unexpected() => new(RuleErrorCode.Syntax, Current.Start, "unexpected " + Describe(Current));

    private Token Advance()
    {
        var t = _tokens[_p];
        if (t.Kind != TokenKind.Eof)
        {
            _p++;
        }

        return t;
    }

    private bool Match(TokenKind kind)
    {
        if (Current.Kind == kind)
        {
            _p++;
            return true;
        }

        return false;
    }

    private Token Expect(TokenKind kind, string what)
    {
        if (Current.Kind != kind)
        {
            throw new CompileFailure(RuleErrorCode.Syntax, Current.Start, $"expected {what} but found {Describe(Current)}");
        }

        return Advance();
    }

    private void Enter()
    {
        if (++_recursion > _limits.MaxAstDepth)
        {
            throw new CompileFailure(
                RuleErrorCode.DepthExceeded,
                Current.Start,
                string.Create(CultureInfo.InvariantCulture, $"expression nesting exceeds the maximum depth of {_limits.MaxAstDepth}"));
        }
    }

    private void Leave() => _recursion--;

    private T Node<T>(T node)
        where T : Expr
    {
        node.Id = _nextId++;
        int depth = 0;
        foreach (var child in node.Children())
        {
            if (child.Depth > depth)
            {
                depth = child.Depth;
            }
        }

        node.Depth = depth + 1;
        if (node.Depth > _limits.MaxAstDepth)
        {
            throw new CompileFailure(
                RuleErrorCode.DepthExceeded,
                node.Start,
                string.Create(CultureInfo.InvariantCulture, $"expression tree depth exceeds the maximum of {_limits.MaxAstDepth}"));
        }

        return node;
    }

    private CallExpr Call(string function, Expr? target, IReadOnlyList<Expr> args, int start, int end, int? opStart = null) =>
        Node(new CallExpr { Function = function, Target = target, Args = args, Start = start, End = end, OpStart = opStart ?? start });

    private Expr ParseExpr()
    {
        Enter();
        var cond = ParseOr();
        int questionAt = Current.Start;
        if (Match(TokenKind.Question))
        {
            var whenTrue = ParseOr();
            Expect(TokenKind.Colon, "':'");
            var whenFalse = ParseExpr();
            cond = Call("_?_:_", null, new[] { cond, whenTrue, whenFalse }, cond.Start, whenFalse.End, questionAt);
        }

        Leave();
        return cond;
    }

    private Expr ParseOr()
    {
        var left = ParseAnd();
        while (Current.Kind == TokenKind.OrOr)
        {
            int opAt = Advance().Start;
            var right = ParseAnd();
            left = Call("_||_", null, new[] { left, right }, left.Start, right.End, opAt);
        }

        return left;
    }

    private Expr ParseAnd()
    {
        var left = ParseRelation();
        while (Current.Kind == TokenKind.AndAnd)
        {
            int opAt = Advance().Start;
            var right = ParseRelation();
            left = Call("_&&_", null, new[] { left, right }, left.Start, right.End, opAt);
        }

        return left;
    }

    private Expr ParseRelation()
    {
        var left = ParseAddition();
        while (true)
        {
            string? op = Current.Kind switch
            {
                TokenKind.Lt => "_<_",
                TokenKind.Le => "_<=_",
                TokenKind.Gt => "_>_",
                TokenKind.Ge => "_>=_",
                TokenKind.EqEq => "_==_",
                TokenKind.NotEq => "_!=_",
                TokenKind.In => "@in",
                _ => null,
            };
            if (op is null)
            {
                return left;
            }

            int opAt = Advance().Start;
            var right = ParseAddition();
            left = Call(op, null, new[] { left, right }, left.Start, right.End, opAt);
        }
    }

    private Expr ParseAddition()
    {
        var left = ParseMultiplication();
        while (Current.Kind is TokenKind.Plus or TokenKind.Minus)
        {
            var opToken = Advance();
            string op = opToken.Kind == TokenKind.Plus ? "_+_" : "_-_";
            var right = ParseMultiplication();
            left = Call(op, null, new[] { left, right }, left.Start, right.End, opToken.Start);
        }

        return left;
    }

    private Expr ParseMultiplication()
    {
        var left = ParseUnary();
        while (Current.Kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent)
        {
            var opToken = Advance();
            string op = opToken.Kind switch
            {
                TokenKind.Star => "_*_",
                TokenKind.Slash => "_/_",
                _ => "_%_",
            };
            var right = ParseUnary();
            left = Call(op, null, new[] { left, right }, left.Start, right.End, opToken.Start);
        }

        return left;
    }

    private Expr ParseUnary()
    {
        Enter();
        Expr result;
        int start = Current.Start;
        if (Match(TokenKind.Bang))
        {
            var operand = ParseUnary();
            result = Call("!_", null, new[] { operand }, start, operand.End);
        }
        else if (Match(TokenKind.Minus))
        {
            if (Current.Kind is TokenKind.Int or TokenKind.Decimal)
            {
                var literal = MakeNumber(Advance(), negative: true, start);
                result = ParseMemberSuffix(literal);
            }
            else
            {
                var operand = ParseUnary();
                result = Call("-_", null, new[] { operand }, start, operand.End);
            }
        }
        else
        {
            result = ParseMemberSuffix(ParsePrimary());
        }

        Leave();
        return result;
    }

    private Expr ParseMemberSuffix(Expr e)
    {
        while (true)
        {
            if (Match(TokenKind.Dot))
            {
                var name = Expect(TokenKind.Ident, "a field or function name");
                CheckReserved(name);
                if (Match(TokenKind.LParen))
                {
                    var args = ParseArgs(out int end);
                    e = MakeMemberCall(e, name, args, end);
                }
                else
                {
                    e = Node(new SelectExpr { Operand = e, Field = name.Text, Start = e.Start, End = name.End });
                }
            }
            else if (Current.Kind == TokenKind.LBracket)
            {
                int bracketAt = Advance().Start;
                var index = ParseExpr();
                var close = Expect(TokenKind.RBracket, "']'");
                e = Call("_[_]", null, new[] { e, index }, e.Start, close.End, bracketAt);
            }
            else
            {
                return e;
            }
        }
    }

    private List<Expr> ParseArgs(out int end)
    {
        var args = new List<Expr>();
        if (Current.Kind != TokenKind.RParen)
        {
            do
            {
                args.Add(ParseExpr());
            }
            while (Match(TokenKind.Comma));
        }

        end = Expect(TokenKind.RParen, "')'").End;
        return args;
    }

    private Expr MakeMemberCall(Expr target, Token name, List<Expr> args, int end)
    {
        MacroKind? macro = name.Text switch
        {
            "all" => MacroKind.All,
            "exists" => MacroKind.Exists,
            "exists_one" => MacroKind.ExistsOne,
            "map" => MacroKind.Map,
            "filter" => MacroKind.Filter,
            _ => null,
        };
        if (macro is not { } kind)
        {
            return Call(name.Text, target, args, target.Start, end, name.Start);
        }

        bool arityOk = kind == MacroKind.Map ? args.Count is 2 or 3 : args.Count == 2;
        if (!arityOk)
        {
            throw new CompileFailure(
                RuleErrorCode.Syntax,
                name.Start,
                kind == MacroKind.Map
                    ? "macro 'map' takes (variable, transform) or (variable, filter, transform)"
                    : $"macro '{name.Text}' takes (variable, predicate)");
        }

        if (args[0] is not IdentExpr variable)
        {
            throw new CompileFailure(RuleErrorCode.Syntax, args[0].Start, $"the first argument of macro '{name.Text}' must be an iteration variable name");
        }

        return Node(new MacroExpr
        {
            Kind = kind,
            Target = target,
            Variable = variable.Name,
            Filter = args.Count == 3 ? args[1] : null,
            Body = args[^1],
            Start = target.Start,
            End = end,
        });
    }

    private Expr ParsePrimary()
    {
        var t = Current;
        switch (t.Kind)
        {
            case TokenKind.Int:
            case TokenKind.Decimal:
                Advance();
                return MakeNumber(t, negative: false, t.Start);
            case TokenKind.String:
                Advance();
                return Node(new LiteralExpr { Value = new StringValue(t.StringValue!), Start = t.Start, End = t.End });
            case TokenKind.True:
            case TokenKind.False:
                Advance();
                return Node(new LiteralExpr { Value = BoolValue.Of(t.Kind == TokenKind.True), Start = t.Start, End = t.End });
            case TokenKind.Null:
                Advance();
                return Node(new LiteralExpr { Value = NullValue.Instance, Start = t.Start, End = t.End });
            case TokenKind.Ident:
            {
                Advance();
                CheckReserved(t);
                if (!Match(TokenKind.LParen))
                {
                    return Node(new IdentExpr { Name = t.Text, Start = t.Start, End = t.End });
                }

                var args = ParseArgs(out int end);
                if (t.Text == "has")
                {
                    if (args.Count != 1 || args[0] is not SelectExpr select)
                    {
                        throw new CompileFailure(RuleErrorCode.Syntax, t.Start, "has() takes one field selection, for example has(vehicle.trackerFitted)");
                    }

                    return Node(new HasExpr { Select = select, Start = t.Start, End = end });
                }

                return Call(t.Text, null, args, t.Start, end);
            }

            case TokenKind.LParen:
            {
                Advance();
                var inner = ParseExpr();
                Expect(TokenKind.RParen, "')'");
                return inner;
            }

            case TokenKind.LBracket:
            {
                Advance();
                var elements = new List<Expr>();
                while (Current.Kind != TokenKind.RBracket)
                {
                    elements.Add(ParseExpr());
                    if (!Match(TokenKind.Comma))
                    {
                        break;
                    }
                }

                var close = Expect(TokenKind.RBracket, "']'");
                return Node(new ListExpr { Elements = elements, Start = t.Start, End = close.End });
            }

            case TokenKind.LBrace:
            {
                Advance();
                var keys = new List<Expr>();
                var values = new List<Expr>();
                while (Current.Kind != TokenKind.RBrace)
                {
                    keys.Add(ParseExpr());
                    Expect(TokenKind.Colon, "':'");
                    values.Add(ParseExpr());
                    if (!Match(TokenKind.Comma))
                    {
                        break;
                    }
                }

                var close = Expect(TokenKind.RBrace, "'}'");
                return Node(new MapExpr { Keys = keys, Values = values, Start = t.Start, End = close.End });
            }

            case TokenKind.Dot:
                throw new CompileFailure(RuleErrorCode.UnsupportedFeature, t.Start, "leading-dot qualified names are outside the adopted CEL subset");
            default:
                throw Unexpected();
        }
    }

    private LiteralExpr MakeNumber(Token t, bool negative, int start)
    {
        RuleValue value;
        if (t.Kind == TokenKind.Int)
        {
            value = IntValue.Of(ParseInt(t, negative));
        }
        else
        {
            string text = negative ? "-" + t.Text : t.Text;
            if (!DecimalText.TryParse(text, out decimal d))
            {
                throw new CompileFailure(
                    RuleErrorCode.InvalidLiteral,
                    start,
                    $"decimal literal '{text}' is out of range or has more than {DecimalText.MaxDigits} significant digits");
            }

            value = new DecimalValue(d);
        }

        return Node(new LiteralExpr { Value = value, Start = start, End = t.End });
    }

    private static long ParseInt(Token t, bool negative)
    {
        string text = t.Text;
        if (text.Length > 2 && text[1] is 'x' or 'X')
        {
            if (ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong u))
            {
                if (!negative && u <= long.MaxValue)
                {
                    return (long)u;
                }

                if (negative && u <= 9223372036854775808UL)
                {
                    return u == 9223372036854775808UL ? long.MinValue : -(long)u;
                }
            }
        }
        else if (long.TryParse(negative ? "-" + text : text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v))
        {
            return v;
        }

        throw new CompileFailure(RuleErrorCode.InvalidLiteral, t.Start, $"integer literal '{(negative ? "-" : string.Empty)}{text}' is out of the 64-bit range");
    }

    private static void CheckReserved(Token t)
    {
        if (Identifiers.Reserved.Contains(t.Text))
        {
            throw new CompileFailure(RuleErrorCode.ReservedWord, t.Start, $"'{t.Text}' is a reserved word");
        }
    }
}
