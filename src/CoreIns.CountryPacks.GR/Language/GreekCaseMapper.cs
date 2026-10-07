using System.Globalization;
using System.Text;

namespace CoreIns.CountryPacks.GR.Language;

/// <summary>
/// Greek case mapping (REQ-MKT-340: CLDR <c>el-Upper</c> with tonos removed in upper case, context-sensitive final sigma
/// in lower case; DESIGN-B §D.3 <c>toGreekUpper()</c>). "οδός" → "ΟΔΟΣ"; "Μάιος" → "ΜΑΪΟΣ"; "ΟΔΟΣ" → "οδος" (lower
/// case cannot restore the tonos, but restores the final sigma).
/// </summary>
public static class GreekCaseMapper
{
    private const char CombiningGrave = '̀';
    private const char CombiningAcute = '́';
    private const char CombiningDiaeresis = '̈';
    private const char CombiningPsili = '̓';
    private const char CombiningDasia = '̔';
    private const char CombiningPerispomeni = '͂';
    private const char CombiningYpogegrammeni = 'ͅ';

    private static readonly CultureInfo Greek = CultureInfo.GetCultureInfo("el-GR");

    /// <summary>
    /// Upper case without tonos (DESIGN-B §D.3): NFD; an accented vowel followed by ι or υ gives that ι/υ a dialytika
    /// (the two letters are not a digraph); grave, acute, perispomeni and breathings are removed; ypogegrammeni
    /// becomes ι; then upper-case in el-GR and NFC.
    /// </summary>
    public static string ToUpper(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var output = new StringBuilder(decomposed.Length + 4);
        var previousBaseAccented = false;

        for (var index = 0; index < decomposed.Length; index++)
        {
            var character = decomposed[index];
            if (IsCombining(character))
            {
                if (character == CombiningYpogegrammeni)
                {
                    output.Append('ι');
                }
                else if (!IsRemovedOnUpper(character))
                {
                    output.Append(character);
                }

                continue;
            }

            output.Append(character);

            // Marks attached to this base character.
            var markEnd = index + 1;
            while (markEnd < decomposed.Length && IsCombining(decomposed[markEnd]))
            {
                markEnd++;
            }

            var accented = false;
            var hasDiaeresis = false;
            for (var mark = index + 1; mark < markEnd; mark++)
            {
                accented |= decomposed[mark] is CombiningAcute or CombiningPerispomeni;
                hasDiaeresis |= decomposed[mark] == CombiningDiaeresis;
            }

            if (previousBaseAccented && IsIotaOrUpsilon(character) && !accented && !hasDiaeresis)
            {
                output.Append(CombiningDiaeresis);
            }

            previousBaseAccented = accented && IsGreekVowel(character);
        }

        return Greek.TextInfo.ToUpper(output.ToString()).Normalize(NormalizationForm.FormC);
    }

    /// <summary>Lower case in el-GR with context-sensitive final sigma: σ at the end of a word becomes ς.</summary>
    public static string ToLower(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var lower = Greek.TextInfo.ToLower(text.Normalize(NormalizationForm.FormC)).ToCharArray();
        for (var index = 0; index < lower.Length; index++)
        {
            if (lower[index] is 'σ' or 'ς')
            {
                var previousIsLetter = index > 0 && char.IsLetter(PreviousBase(lower, index));
                var nextIsLetter = NextBaseIsLetter(lower, index);
                lower[index] = previousIsLetter && !nextIsLetter ? 'ς' : 'σ';
            }
        }

        return new string(lower);
    }

    /// <summary>Title case: lower case (with final sigma) and the first letter of each word upper-cased (keeps tonos).</summary>
    public static string ToTitle(string? text)
    {
        var lower = ToLower(text).ToCharArray();
        var atWordStart = true;
        for (var index = 0; index < lower.Length; index++)
        {
            if (char.IsLetter(lower[index]))
            {
                if (atWordStart)
                {
                    lower[index] = Greek.TextInfo.ToUpper(lower[index]);
                }

                atWordStart = false;
            }
            else if (!IsCombining(lower[index]))
            {
                atWordStart = lower[index] is not ('\'' or '’');
            }
        }

        return new string(lower);
    }

    private static char PreviousBase(char[] text, int index)
    {
        var position = index - 1;
        while (position > 0 && IsCombining(text[position]))
        {
            position--;
        }

        return text[position];
    }

    private static bool NextBaseIsLetter(char[] text, int index)
    {
        var position = index + 1;
        while (position < text.Length && IsCombining(text[position]))
        {
            position++;
        }

        return position < text.Length && char.IsLetter(text[position]);
    }

    private static bool IsCombining(char character) =>
        CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static bool IsRemovedOnUpper(char mark) =>
        mark is CombiningGrave or CombiningAcute or CombiningPerispomeni or CombiningPsili or CombiningDasia;

    private static bool IsIotaOrUpsilon(char character) => character is 'ι' or 'υ' or 'Ι' or 'Υ';

    private static bool IsGreekVowel(char character) =>
        character is 'α' or 'ε' or 'η' or 'ι' or 'ο' or 'υ' or 'ω' or 'Α' or 'Ε' or 'Η' or 'Ι' or 'Ο' or 'Υ' or 'Ω';
}
