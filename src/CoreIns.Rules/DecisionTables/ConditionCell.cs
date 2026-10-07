using System.Text;

namespace CoreIns.Rules.DecisionTables;

/// <summary>
/// Translates a condition cell (REQ-UW-032: comparison, range, set, "any", "no value" or an expression) into an
/// expression of the rule language over the column. Syntax (<c>x</c> is any expression):
/// <list type="table">
/// <item><term><c>-</c> or empty</term><description>any value (always matches)</description></item>
/// <item><term><c>null</c> / <c>not null</c></term><description>no value / has a value</description></item>
/// <item><term><c>&lt; x</c>, <c>&lt;= x</c>, <c>&gt; x</c>, <c>&gt;= x</c>, <c>== x</c></term><description>positive comparison</description></item>
/// <item><term><c>!= x</c></term><description>negative comparison</description></item>
/// <item><term><c>[a..b]</c>, <c>[a..b)</c>, <c>(a..b]</c>, <c>(a..b)</c></term><description>range with inclusive <c>[ ]</c> or exclusive <c>( )</c> bounds</description></item>
/// <item><term><c>in [a, b]</c> / <c>not in [a, b]</c></term><description>set membership (positive / negative)</description></item>
/// <item><term><c>? predicate</c></term><description>any boolean expression (may reference all columns, variables and inputs)</description></item>
/// <item><term>anything else</term><description>equality with the expression value (positive)</description></item>
/// </list>
/// Null rule: a null column never matches a positive test and always matches a negative test (<c>!=</c>,
/// <c>not in</c>), like SQL's <c>IS DISTINCT FROM</c>; <c>null</c> / <c>not null</c> test for it explicitly.
/// </summary>
public static class ConditionCell
{
    private static readonly string[] ComparisonOperators = { "<=", ">=", "!=", "==", "<", ">" };

    /// <summary>Returns the boolean expression for the cell, or null when the cell matches any value.</summary>
    public static string? Translate(string cell, string column) => TranslateMapped(cell, column)?.Text;

    /// <summary>
    /// Translates a cell and keeps a map from offsets in the generated expression back to offsets in the cell text,
    /// so compile errors point into the cell the author wrote.
    /// </summary>
    internal static MappedText? TranslateMapped(string cell, string column)
    {
        ArgumentNullException.ThrowIfNull(cell);
        ArgumentNullException.ThrowIfNull(column);
        int lead = cell.Length - cell.TrimStart().Length;
        string t = cell.Trim();
        var b = new MappedTextBuilder(lead);
        if (t.Length == 0 || t == "-")
        {
            return null;
        }

        if (t == "null")
        {
            return b.Lit(column + " == null").Build();
        }

        if (t == "not null")
        {
            return b.Lit(column + " != null").Build();
        }

        if (StartsWithWord(t, "not in"))
        {
            return b.Lit("!(" + column + " in (").Src(t, 6, t.Length - 6).Lit("))").Build();
        }

        if (StartsWithWord(t, "in"))
        {
            return b.Lit(column + " != null && " + column + " in (").Src(t, 2, t.Length - 2).Lit(")").Build();
        }

        if (t[0] == '?')
        {
            return b.Lit("(").Src(t, 1, t.Length - 1).Lit(")").Build();
        }

        foreach (var op in ComparisonOperators)
        {
            if (t.StartsWith(op, StringComparison.Ordinal))
            {
                int n = op.Length;
                return op == "!="
                    ? b.Lit(column + " != (").Src(t, n, t.Length - n).Lit(")").Build()
                    : b.Lit(column + " != null && " + column + " " + op + " (").Src(t, n, t.Length - n).Lit(")").Build();
            }
        }

        if (t.Length >= 5 && t[0] is '[' or '(' && t[^1] is ']' or ')' && TryFindRangeSeparator(t, out int sep))
        {
            string lowOp = t[0] == '[' ? ">=" : ">";
            string highOp = t[^1] == ']' ? "<=" : "<";
            return b.Lit(column + " != null && " + column + " " + lowOp + " (").Src(t, 1, sep - 1)
                .Lit(") && " + column + " " + highOp + " (").Src(t, sep + 2, t.Length - sep - 3).Lit(")").Build();
        }

        return b.Lit(column + " == (").Src(t, 0, t.Length).Lit(")").Build();
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

/// <summary>Generated expression text plus the segments copied verbatim from the cell (for error positions).</summary>
internal sealed class MappedText
{
    private readonly (int Generated, int Cell, int Length)[] _segments;
    private readonly int _cellStart;

    public MappedText(string text, (int Generated, int Cell, int Length)[] segments, int cellStart)
    {
        Text = text;
        _segments = segments;
        _cellStart = cellStart;
    }

    public string Text { get; }

    /// <summary>Maps an offset in <see cref="Text"/> to an offset in the original cell (generated parts map to the cell start).</summary>
    public int ToCellOffset(int generatedOffset)
    {
        foreach (var (generated, cell, length) in _segments)
        {
            if (generatedOffset >= generated && generatedOffset <= generated + length)
            {
                return cell + (generatedOffset - generated);
            }
        }

        return _cellStart;
    }
}

internal sealed class MappedTextBuilder
{
    private readonly StringBuilder _text = new();
    private readonly List<(int, int, int)> _segments = new();
    private readonly int _lead;

    public MappedTextBuilder(int lead) => _lead = lead;

    public MappedTextBuilder Lit(string s)
    {
        _text.Append(s);
        return this;
    }

    /// <summary>Copies <paramref name="length"/> characters of the trimmed cell <paramref name="t"/> from <paramref name="start"/>.</summary>
    public MappedTextBuilder Src(string t, int start, int length)
    {
        _segments.Add((_text.Length, _lead + start, length));
        _text.Append(t, start, length);
        return this;
    }

    public MappedText Build() => new(_text.ToString(), _segments.ToArray(), _lead);
}
