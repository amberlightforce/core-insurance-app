using System;
using System.Collections.Generic;
using System.Globalization;

namespace CoreIns.Rules.Syntax;

/// <summary>Identifier rules shared by the lexer and the schema builders.</summary>
internal static class Identifiers
{
    /// <summary>CEL reserved words (cannot be identifiers or field names).</summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        "as", "break", "const", "continue", "else", "for", "function", "if", "import", "let", "loop",
        "package", "namespace", "return", "var", "void", "while",
    };

    /// <summary>Keywords that are literals or operators.</summary>
    public static readonly IReadOnlySet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "true", "false", "null", "in",
    };

    public static bool IsStart(char c) => char.IsAsciiLetter(c) || c == '_';

    public static bool IsPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || !IsStart(name[0]))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!IsPart(c))
            {
                return false;
            }
        }

        return !Reserved.Contains(name) && !Keywords.Contains(name);
    }

    public static void Validate(string name, string paramName)
    {
        ArgumentNullException.ThrowIfNull(name, paramName);
        if (!IsValid(name))
        {
            throw new ArgumentException($"'{name}' is not a valid identifier (ASCII letter or '_' then letters, digits or '_'; not a reserved word)", paramName);
        }
    }
}

/// <summary>Strict, culture-invariant decimal text parsing that never silently rounds.</summary>
internal static class DecimalText
{
    /// <summary>Maximum significant digits accepted (System.Decimal holds 28 to 29).</summary>
    public const int MaxDigits = 28;

    /// <summary>Parses <c>[-]digits[.digits]</c> (or <c>.digits</c>). Rejects exponents, grouping, more than 28 significant or fractional digits.</summary>
    public static bool TryParse(string text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        int i = 0;
        if (text[0] is '-' or '+')
        {
            i = 1;
        }

        int intDigits = 0, fracDigits = 0, significant = 0;
        bool seenNonZero = false, seenDot = false;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '.')
            {
                if (seenDot)
                {
                    return false;
                }

                seenDot = true;
                continue;
            }

            if (!char.IsAsciiDigit(c))
            {
                return false;
            }

            if (seenDot)
            {
                fracDigits++;
            }
            else
            {
                intDigits++;
            }

            if (c != '0')
            {
                seenNonZero = true;
            }

            if (seenNonZero)
            {
                significant++;
            }
        }

        if (intDigits + fracDigits == 0 || (seenDot && fracDigits == 0) || fracDigits > MaxDigits || significant > MaxDigits)
        {
            return false;
        }

        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }
}
