using System;

namespace CoreIns.Rules.DecisionTables;

/// <summary>
/// Translates a condition cell (REQ-UW-032: comparison, range, set, "any", "no value" or an expression) into an
/// expression of the rule language over the column. Syntax (<c>x</c> is any expression):
/// <list type="table">
/// <item><term><c>-</c> or empty</term><description>any value (always matches)</description></item>
/// <item><term><c>null</c> / <c>not null</c></term><description>no value / has a value</description></item>
/// <item><term><c>&lt; x</c>, <c>&lt;= x</c>, <c>&gt; x</c>, <c>&gt;= x</c>, <c>== x</c>, <c>!= x</c></term><description>comparison (never matches a null column, except <c>!=</c>)</description></item>
/// <item><term><c>[a..b]</c>, <c>[a..b)</c>, <c>(a..b]</c>, <c>(a..b)</c></term><description>range with inclusive <c>[ ]</c> or exclusive <c>( )</c> bounds</description></item>
/// <item><term><c>in [a, b]</c> / <c>not in [a, b]</c></term><description>set membership</description></item>
/// <item><term><c>? predicate</c></term><description>any boolean expression (may reference all columns, variables and inputs)</description></item>
/// <item><term>anything else</term><description>equality with the expression value</description></item>
/// </list>
/// </summary>
public static class ConditionCell
{
    private static readonly string[] ComparisonOperators = { "<=", ">=", "!=", "==", "<", ">" };

    /// <summary>Returns the boolean expression for the cell, or null when the cell matches any value.</summary>
    public static string? Translate(string cell, string column)
    {
        ArgumentNullException.ThrowIfNull(cell);
        ArgumentNullException.ThrowIfNull(column);
        string t = cell.Trim();
        if (t.Length == 0 || t == "-")
        {
            return null;
        }

        if (t == "null")
        {
            return column + " == null";
        }

        if (t == "not null")
        {
            return column + " != null";
        }

        if (StartsWithWord(t, "not in"))
        {
            return "!(" + column + " in (" + t[6..] + "))";
        }

        if (StartsWithWord(t, "in"))
        {
            return column + " in (" + t[2..] + ")";
        }

        if (t[0] == '?')
        {
            return "(" + t[1..] + ")";
        }

        foreach (var op in ComparisonOperators)
        {
            if (t.StartsWith(op, StringComparison.Ordinal))
            {
                string operand = t[op.Length..];
                return op == "!="
                    ? column + " != (" + operand + ")"
                    : column + " != null && " + column + " " + op + " (" + operand + ")";
            }
        }

        if (t.Length >= 5 && t[0] is '[' or '(' && t[^1] is ']' or ')' && TryFindRangeSeparator(t, out int sep))
        {
            string low = t[1..sep];
            string high = t[(sep + 2)..^1];
            string lowOp = t[0] == '[' ? ">=" : ">";
            string highOp = t[^1] == ']' ? "<=" : "<";
            return column + " != null && " + column + " " + lowOp + " (" + low + ") && " + column + " " + highOp + " (" + high + ")";
        }

        return column + " == (" + t + ")";
    }

    private static bool StartsWithWord(string t, string word) =>
        t.StartsWith(word, StringComparison.Ordinal) && t.Length > word.Length && (char.IsWhiteSpace(t[word.Length]) || t[word.Length] == '[');

    /// <summary>Finds a top-level <c>..</c> inside the outer brackets (ignoring strings and nested brackets).</summary>
    private static bool TryFindRangeSeparator(string t, out int index)
    {
        int depth = 0;
        for (int i = 1; i < t.Length - 1; i++)
        {
            char c = t[i];
            if (c is '"' or '\'')
            {
                i = SkipString(t, i);
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (depth == 0 && c == '.' && t[i + 1] == '.')
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    private static int SkipString(string t, int start)
    {
        char quote = t[start];
        for (int i = start + 1; i < t.Length; i++)
        {
            if (t[i] == '\\')
            {
                i++;
            }
            else if (t[i] == quote)
            {
                return i;
            }
        }

        return t.Length;
    }
}
