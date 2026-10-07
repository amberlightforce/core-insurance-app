using System;
using System.Security.Cryptography;
using System.Text;

namespace CoreIns.Rules.Syntax;

/// <summary>
/// Prints the canonical, whitespace- and comment-insensitive text of a syntax tree (an S-expression prefixed by the
/// language version). The SHA-256 of this text is the expression's content hash, which feeds the configuration hash.
/// </summary>
internal static class CanonicalPrinter
{
    public static string Print(Expr e)
    {
        var sb = new StringBuilder(RuleLanguage.CanonicalPrefix);
        Write(sb, e);
        return sb.ToString();
    }

    /// <summary>Lower-case hexadecimal SHA-256 of the UTF-8 bytes of <paramref name="canonicalText"/>.</summary>
    public static string Hash(string canonicalText)
    {
        const string digits = "0123456789abcdef";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText));
        var chars = new char[hash.Length * 2];
        for (int i = 0; i < hash.Length; i++)
        {
            chars[2 * i] = digits[hash[i] >> 4];
            chars[(2 * i) + 1] = digits[hash[i] & 0xF];
        }

        return new string(chars);
    }

    private static void Write(StringBuilder sb, Expr e)
    {
        switch (e)
        {
            case LiteralExpr l:
                WriteLiteral(sb, l.Value);
                break;
            case IdentExpr i:
                sb.Append("(id ").Append(i.Name).Append(')');
                break;
            case SelectExpr s:
                sb.Append("(sel ");
                Write(sb, s.Operand);
                sb.Append(' ').Append(s.Field).Append(')');
                break;
            case HasExpr h:
                sb.Append("(has ");
                Write(sb, h.Select);
                sb.Append(')');
                break;
            case CallExpr c:
                sb.Append(c.Target is null ? "(call " : "(mcall ").Append(c.Function);
                if (c.Target is not null)
                {
                    sb.Append(' ');
                    Write(sb, c.Target);
                }

                foreach (var a in c.Args)
                {
                    sb.Append(' ');
                    Write(sb, a);
                }

                sb.Append(')');
                break;
            case ListExpr l:
                sb.Append("(list");
                foreach (var el in l.Elements)
                {
                    sb.Append(' ');
                    Write(sb, el);
                }

                sb.Append(')');
                break;
            case MapExpr m:
                sb.Append("(map");
                for (int i = 0; i < m.Keys.Count; i++)
                {
                    sb.Append(" (");
                    Write(sb, m.Keys[i]);
                    sb.Append(' ');
                    Write(sb, m.Values[i]);
                    sb.Append(')');
                }

                sb.Append(')');
                break;
            case MacroExpr m:
                sb.Append("(macro ").Append(MacroExpr.NameOf(m.Kind)).Append(' ').Append(m.Variable).Append(' ');
                Write(sb, m.Target);
                if (m.Filter is not null)
                {
                    sb.Append(' ');
                    Write(sb, m.Filter);
                }

                sb.Append(' ');
                Write(sb, m.Body);
                sb.Append(')');
                break;
            default:
                throw new InvalidOperationException("unknown syntax node");
        }
    }

    private static void WriteLiteral(StringBuilder sb, RuleValue v)
    {
        switch (v)
        {
            case IntValue i:
                sb.Append("(int ").Append(i.ToString()).Append(')');
                break;
            case DecimalValue d:
                sb.Append("(dec ").Append(d.ToString()).Append(')');
                break;
            case StringValue s:
                sb.Append("(str ").Append(RuleValue.Quote(s.Value)).Append(')');
                break;
            case BoolValue b:
                sb.Append("(bool ").Append(b.ToString()).Append(')');
                break;
            case NullValue:
                sb.Append("(null)");
                break;
            default:
                throw new InvalidOperationException("unexpected literal kind");
        }
    }
}
