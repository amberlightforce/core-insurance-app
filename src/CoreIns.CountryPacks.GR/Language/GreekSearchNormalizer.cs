using System.Globalization;
using System.Text;

namespace CoreIns.CountryPacks.GR.Language;

/// <summary>
/// Search key of the Greek language rules (REQ-MKT-178, REQ-MKT-340, REQ-PTY-065): accent-, diaeresis-, case- and
/// final-sigma-insensitive, punctuation-insensitive. "Σωτηρόπουλος" → "ΣΩΤΗΡΟΠΟΥΛΟΣ".
/// </summary>
/// <remarks>
/// <para>The same key is produced in PostgreSQL by <c>coreins_search_key(text)</c> in
/// <c>infra/database/greek-search.sql</c>; the two are kept identical by a parity test over a name corpus, so keys
/// computed by the application and keys computed by SQL (indexes, ad-hoc data fixes) agree. Steps, in order:</para>
/// <list type="number">
/// <item>Unicode NFD (canonical decomposition).</item>
/// <item>Remove the combining marks of the blocks U+0300–036F, U+1AB0–1AFF, U+1DC0–1DFF, U+20D0–20FF and U+FE20–FE2F
/// (tonos, dialytika, polytonic breathings, ypogegrammeni, Latin diacritics).</item>
/// <item>NFC.</item>
/// <item>Unicode simple upper-case mapping (σ and ς both map to Σ, which is the final-sigma folding; tonos-free upper
/// case per CLDR el-Upper because the accents are already gone). SQL: <c>upper(… COLLATE pg_c_utf8)</c>.</item>
/// <item>Every run of characters that are neither alphabetic (letters, letter numbers) nor ASCII digits becomes one
/// space; leading and trailing spaces are removed. SQL: <c>regexp_replace(…, '[^[:alnum:]]+', ' ', 'g')</c> under
/// <c>pg_c_utf8</c>, whose <c>[:alnum:]</c> is Unicode Alphabetic or ASCII digit.</item>
/// </list>
/// <para>Known divergence outside Greek, Latin and Cyrillic names: Unicode "Other_Alphabetic" marks of other scripts
/// (for example Indic vowel signs) count as alphabetic in PostgreSQL but are treated as separators here. Keys for those
/// scripts must come from their own language rules.</para>
/// </remarks>
public static class GreekSearchNormalizer
{
    /// <summary>Returns the search key of <paramref name="text"/> (empty for null or blank input).</summary>
    public static string SearchKey(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var stripped = new StringBuilder(decomposed.Length);
        foreach (var rune in decomposed.EnumerateRunes())
        {
            if (!IsStrippedMark(rune.Value))
            {
                stripped.Append(rune.ToString());
            }
        }

        // .NET invariant casing deliberately omits U+0131 (dotless i) → I; Unicode simple case mapping (and PostgreSQL) has it.
        var upper = stripped.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant().Replace('ı', 'I');

        var key = new StringBuilder(upper.Length);
        var pendingSpace = false;
        foreach (var rune in upper.EnumerateRunes())
        {
            if (IsAlphanumeric(rune))
            {
                if (pendingSpace && key.Length > 0)
                {
                    key.Append(' ');
                }

                pendingSpace = false;
                key.Append(rune.ToString());
            }
            else
            {
                pendingSpace = true;
            }
        }

        return key.ToString();
    }

    /// <summary>Combining-mark ranges removed by the key (kept identical to the SQL regular expression).</summary>
    internal static bool IsStrippedMark(int codePoint) =>
        codePoint is (>= 0x0300 and <= 0x036F)
            or (>= 0x1AB0 and <= 0x1AFF)
            or (>= 0x1DC0 and <= 0x1DFF)
            or (>= 0x20D0 and <= 0x20FF)
            or (>= 0xFE20 and <= 0xFE2F);

    /// <summary>
    /// Alphabetic (letters, letter numbers and the alphabetic symbols of the Latin script: circled and squared letters)
    /// or an ASCII digit; mirrors PostgreSQL 17 <c>[:alnum:]</c> under the builtin provider.
    /// </summary>
    internal static bool IsAlphanumeric(Rune rune)
    {
        var value = rune.Value;
        if (value is >= '0' and <= '9')
        {
            return true;
        }

        switch (Rune.GetUnicodeCategory(rune))
        {
            case UnicodeCategory.UppercaseLetter:
            case UnicodeCategory.LowercaseLetter:
            case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.ModifierLetter:
            case UnicodeCategory.OtherLetter:
            case UnicodeCategory.LetterNumber:
                return true;
            default:
                // Other_Alphabetic symbols: circled Latin letters and the enclosed alphanumeric supplement letters.
                return value is (>= 0x24B6 and <= 0x24E9)
                    or (>= 0x1F130 and <= 0x1F149)
                    or (>= 0x1F150 and <= 0x1F169)
                    or (>= 0x1F170 and <= 0x1F189);
        }
    }
}
