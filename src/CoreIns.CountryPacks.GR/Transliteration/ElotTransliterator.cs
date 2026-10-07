using System.Globalization;
using System.Text;
using CoreIns.CountryPacks.GR.Language;

namespace CoreIns.CountryPacks.GR.Transliteration;

/// <summary>
/// ELOT 743 Type 2 transcription of Greek to Latin (aligned with ISO 843) as stated in PRD-17 §9.4.2 and PRD-01
/// (REQ-MKT-091, REQ-MKT-177, REQ-PTY-012, REQ-PTY-062; GR-12): letter table, digraphs "ου" → ou, "αυ/ευ/ηυ"
/// contextual (v before vowels and voiced consonants, f before θ κ ξ π σ τ φ χ ψ and at word end), "μπ" → b at the
/// start or end of a word and mp inside, "γγ" → ng, "γκ" → gk, "γξ" → nx, "γχ" → nch; accents dropped; a tonos on the
/// first vowel or a dialytika on the second breaks a digraph. Upper-case source letters give upper-case output, with
/// multi-letter outputs fully upper-cased inside an all-capitals word ("ΘΕΟΔΩΡΟΣ" → "THEODOROS") and capitalised
/// otherwise ("Θεόδωρος" → "Theodoros").
/// </summary>
/// <remarks>
/// The full table of the paid standard is not reproduced in the PRDs (OI-MKT-18); the vectors in the PRDs are the
/// acceptance set (see the pack tests). Search variants add the digraph alternatives of REQ-PTY-067
/// ("ou"/"u", "mp"/"b", "nt"/"d", "gk"/"g", "ch"/"h").
/// </remarks>
public static class ElotTransliterator
{
    /// <summary>Upper bound on the number of search variants returned for one text (keeps the search query bounded).</summary>
    public const int MaxSearchVariants = 32;

    private static readonly Dictionary<char, string> Letters = new()
    {
        ['α'] = "a", ['β'] = "v", ['γ'] = "g", ['δ'] = "d", ['ε'] = "e", ['ζ'] = "z", ['η'] = "i", ['θ'] = "th",
        ['ι'] = "i", ['κ'] = "k", ['λ'] = "l", ['μ'] = "m", ['ν'] = "n", ['ξ'] = "x", ['ο'] = "o", ['π'] = "p",
        ['ρ'] = "r", ['σ'] = "s", ['τ'] = "t", ['υ'] = "y", ['φ'] = "f", ['χ'] = "ch", ['ψ'] = "ps", ['ω'] = "o",
    };

    /// <summary>Letters after which αυ/ευ/ηυ is transcribed with f (voiceless consonants, ELOT 743).</summary>
    private const string Voiceless = "θκξπστφχψ";

    /// <summary>Transliterates <paramref name="text"/>; Greek letters are transcribed, everything else is copied.</summary>
    /// <param name="text">Input text.</param>
    /// <param name="unmapped">Set when a character of the Greek block had no mapping and was copied unchanged.</param>
    public static string Transliterate(string text, out bool unmapped)
    {
        ArgumentNullException.ThrowIfNull(text);
        var units = Segment(text, out unmapped);
        var output = new StringBuilder(text.Length + 8);
        foreach (var unit in units)
        {
            output.Append(unit.Alternatives[0]);
        }

        return output.ToString();
    }

    /// <summary>
    /// Search variants of <paramref name="text"/>: the ELOT transcription first, then the digraph alternatives of
    /// REQ-PTY-067, each reduced to the search key (<see cref="GreekSearchNormalizer"/>); distinct, at most
    /// <see cref="MaxSearchVariants"/>. Text without Greek letters yields its own search key.
    /// </summary>
    public static IReadOnlyList<string> SearchVariants(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var units = Segment(text, out _);

        var partials = new List<StringBuilder> { new() };
        foreach (var unit in units)
        {
            var next = new List<StringBuilder>(partials.Count * unit.Alternatives.Length);
            foreach (var alternative in unit.Alternatives)
            {
                foreach (var partial in partials)
                {
                    if (next.Count >= MaxSearchVariants * 4)
                    {
                        break;
                    }

                    next.Add(new StringBuilder(partial.ToString()).Append(alternative));
                }
            }

            partials = next;
        }

        var keys = new List<string>();
        foreach (var partial in partials)
        {
            var key = GreekSearchNormalizer.SearchKey(partial.ToString());
            if (key.Length > 0 && !keys.Contains(key, StringComparer.Ordinal))
            {
                keys.Add(key);
                if (keys.Count == MaxSearchVariants)
                {
                    break;
                }
            }
        }

        return keys;
    }

    /// <summary>One source character as a Greek letter with its attributes, or a pass-through character.</summary>
    private readonly record struct Letter(string Original, char Base, bool IsGreek, bool IsUpper, bool Accented, bool Diaeresis);

    /// <summary>A transcribed unit: the ELOT output first, then search alternatives.</summary>
    private readonly record struct Unit(string[] Alternatives);

    private static List<Unit> Segment(string text, out bool unmapped)
    {
        unmapped = false;
        var letters = Analyse(text, ref unmapped);
        var units = new List<Unit>(letters.Count);

        var index = 0;
        while (index < letters.Count)
        {
            var current = letters[index];
            if (!current.IsGreek)
            {
                units.Add(new Unit([current.Original]));
                index++;
                continue;
            }

            var next = At(letters, index + 1);
            var afterNext = At(letters, index + 2);
            var previous = At(letters, index - 1);
            var nextIsDigraphPartner = next is { IsGreek: true, Diaeresis: false } && !current.Accented;

            string[] lowerAlternatives;
            int consumed;

            if (current.Base == 'ο' && next?.Base == 'υ' && nextIsDigraphPartner)
            {
                lowerAlternatives = ["ou", "u"];
                consumed = 2;
            }
            else if (current.Base is 'α' or 'ε' or 'η' && next?.Base == 'υ' && nextIsDigraphPartner)
            {
                var vowel = Letters[current.Base];
                var voiceless = afterNext is not { IsGreek: true } || Voiceless.Contains(afterNext.Value.Base, StringComparison.Ordinal);
                lowerAlternatives = [vowel + (voiceless ? "f" : "v")];
                consumed = 2;
            }
            else if (current.Base == 'μ' && next is { IsGreek: true, Base: 'π' })
            {
                var atWordEdge = previous is not { IsGreek: true } || afterNext is not { IsGreek: true };
                lowerAlternatives = atWordEdge ? ["b", "mp"] : ["mp", "b"];
                consumed = 2;
            }
            else if (current.Base == 'ν' && next is { IsGreek: true, Base: 'τ' })
            {
                lowerAlternatives = ["nt", "d"];
                consumed = 2;
            }
            else if (current.Base == 'γ' && next is { IsGreek: true, Base: 'γ' or 'κ' or 'ξ' or 'χ' })
            {
                lowerAlternatives = next.Value.Base switch
                {
                    'γ' => ["ng"],
                    'κ' => ["gk", "g"],
                    'ξ' => ["nx"],
                    _ => ["nch", "nh"],
                };
                consumed = 2;
            }
            else if (current.Base == 'χ')
            {
                lowerAlternatives = ["ch", "h"];
                consumed = 1;
            }
            else
            {
                lowerAlternatives = [Letters[current.Base]];
                consumed = 1;
            }

            var following = At(letters, index + consumed);
            var allCapitals = consumed == 2
                ? next!.Value.IsUpper
                : following is { IsGreek: true } ? following.Value.IsUpper : previous is { IsGreek: true, IsUpper: true };

            units.Add(new Unit(Array.ConvertAll(lowerAlternatives, alternative => ApplyCase(alternative, current.IsUpper, allCapitals))));
            index += consumed;
        }

        return units;
    }

    private static Letter? At(List<Letter> letters, int index) =>
        index >= 0 && index < letters.Count ? letters[index] : null;

    private static string ApplyCase(string lower, bool upper, bool allCapitals)
    {
        if (!upper)
        {
            return lower;
        }

        return allCapitals || lower.Length == 1
            ? lower.ToUpperInvariant()
            : char.ToUpperInvariant(lower[0]) + lower[1..];
    }

    private static List<Letter> Analyse(string text, ref bool unmapped)
    {
        var letters = new List<Letter>(text.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(text.Normalize(NormalizationForm.FormC));
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            var decomposed = element.Normalize(NormalizationForm.FormD);
            var baseCharacter = decomposed[0];
            var lower = ToGreekLowerBase(baseCharacter);

            if (lower is null)
            {
                if (baseCharacter is >= 'Ͱ' and <= 'Ͽ' or >= 'ἀ' and <= '῿')
                {
                    var punctuation = baseCharacter switch
                    {
                        ';' => "?",
                        '·' => ";",
                        _ => null,
                    };
                    unmapped |= punctuation is null;
                    letters.Add(new Letter(punctuation ?? element, baseCharacter, false, false, false, false));
                }
                else
                {
                    letters.Add(new Letter(element, baseCharacter, false, false, false, false));
                }

                continue;
            }

            var accented = false;
            var diaeresis = false;
            foreach (var mark in decomposed.AsSpan(1))
            {
                accented |= mark is '́' or '̀' or '͂';
                diaeresis |= mark == '̈';
            }

            letters.Add(new Letter(element, lower.Value, true, char.IsUpper(baseCharacter), accented, diaeresis));
        }

        return letters;
    }

    /// <summary>Lower-case base letter of a Greek character (final and lunate sigma fold to σ), or null.</summary>
    private static char? ToGreekLowerBase(char character)
    {
        var lower = character switch
        {
            'ς' or 'ϲ' or 'Ϲ' => 'σ',
            'ϐ' => 'β',
            'ϑ' => 'θ',
            'ϕ' => 'φ',
            >= 'Α' and <= 'Ω' => (char)(character + ('α' - 'Α')),
            _ => character,
        };

        return lower is >= 'α' and <= 'ω' && Letters.ContainsKey(lower) ? lower : null;
    }
}
