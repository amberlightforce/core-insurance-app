using System.Globalization;
using System.Text;

namespace CoreIns.ContractGen;

/// <summary>Identifier helpers: deterministic, culture-invariant.</summary>
internal static class Names
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while", "record", "required", "init", "value", "var", "dynamic", "async",
        "await", "nameof", "when", "where", "yield", "global", "partial", "file", "scoped",
    };

    /// <summary>PascalCase from camelCase, snake_case, kebab-case, dotted or UPPER_CASE text.</summary>
    public static string Pascal(string text)
    {
        var parts = Split(text);
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            var allUpper = part.All(c => !char.IsLetter(c) || char.IsUpper(c));
            var word = allUpper && part.Length > 1 ? part[0] + part[1..].ToLowerInvariant() : part;
            sb.Append(char.ToUpperInvariant(word[0])).Append(word.AsSpan(1));
        }

        var result = sb.ToString();
        if (result.Length == 0)
        {
            result = "Value";
        }

        return char.IsDigit(result[0]) ? "V" + result : result;
    }

    /// <summary>camelCase identifier (escaped when it is a keyword).</summary>
    public static string Camel(string text)
    {
        var pascal = Pascal(text);
        var camel = char.ToLowerInvariant(pascal[0]) + pascal[1..];
        return Keywords.Contains(camel) ? "@" + camel : camel;
    }

    /// <summary>Splits an identifier into words at separators and case boundaries (keeps "Ids", "V1" sensible).</summary>
    private static List<string> Split(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsAsciiLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (current.Length > 0 && char.IsUpper(c) && char.IsLower(current[^1]))
            {
                Flush();
            }

            current.Append(c);
        }

        Flush();
        return parts;

        void Flush()
        {
            if (current.Length > 0)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
        }
    }

    /// <summary>Escapes text for an XML doc comment and collapses whitespace.</summary>
    public static string Xml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    /// <summary>C# string literal.</summary>
    public static string Literal(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when c < ' ' => string.Format(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c),
                _ => c.ToString(),
            });
        }

        return sb.Append('"').ToString();
    }

    /// <summary>First sentence-ish line of a description (for summaries).</summary>
    public static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        return line.Length > 400 ? line[..400] + "…" : line;
    }

    /// <summary>Singular of a plural property name for array item ids (<c>chargeIds</c> → <c>chargeId</c>).</summary>
    public static string Singular(string name) =>
        name.EndsWith("Ids", StringComparison.Ordinal) ? name[..^1]
        : name.EndsWith("ies", StringComparison.Ordinal) ? name[..^3] + "y"
        : name.EndsWith('s') && !name.EndsWith("ss", StringComparison.Ordinal) ? name[..^1]
        : name;
}

/// <summary>Hands out member names that are unique within one type scope.</summary>
internal sealed class NameScope
{
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public NameScope(params IEnumerable<string> reserved)
    {
        foreach (var name in reserved)
        {
            _used.Add(name);
        }
    }

    public string Take(string wanted, string suffix = "Value")
    {
        var name = wanted;
        if (!_used.Add(name))
        {
            name = wanted + suffix;
            for (var i = 2; !_used.Add(name); i++)
            {
                name = wanted + suffix + i.ToString(CultureInfo.InvariantCulture);
            }
        }

        return name;
    }
}
