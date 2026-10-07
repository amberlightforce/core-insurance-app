using System.Globalization;
using System.Text;
using CoreIns.Modules.Market.Contracts.Localisation;
using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.Modules.Party.Domain;

/// <summary>
/// Name forms and search keys (REQ-PTY-061..067). PTY core holds no language rule and no transliteration table
/// (XMR-D-205): script detection is Unicode-generic (the script property of the letters); Latin forms come from the
/// bound <see cref="INameTransliterator"/>; search keys from the bound MKT language rules (<see cref="ILanguageRuleSet"/>)
/// and the transliterator's <c>searchVariants</c>.
/// </summary>
internal sealed class NameForms(INameTransliterator transliterator, ILanguageRuleSet languageRules)
{
    /// <summary>Version recorded with each stored key (language rules + transliteration rule set).</summary>
    public string KeyRuleVersion => $"{languageRules.RuleVersion}+{transliterator.RuleSetId}";

    /// <summary>The ISO 15924 script of a text: the script of its letters (Latn when none or mixed with Latin only).</summary>
    public static string ScriptOf(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return ScriptCodes.Latin;
        }

        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune))
            {
                continue;
            }

            var value = rune.Value;
            if (value is (>= 0x0370 and <= 0x03FF) or (>= 0x1F00 and <= 0x1FFF))
            {
                return ScriptCodes.Greek;
            }

            if (value is >= 0x0400 and <= 0x052F)
            {
                return ScriptCodes.Cyrillic;
            }
        }

        return ScriptCodes.Latin;
    }

    /// <summary>Latin form of a native-script text, or null for Latin text. Fails closed (SpiException) for an unsupported script.</summary>
    public async ValueTask<string?> LatinAsync(string? text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var script = ScriptOf(text);
        if (script == ScriptCodes.Latin)
        {
            return text;
        }

        var result = await transliterator.TransliterateAsync(text, script, null, cancellationToken).ConfigureAwait(false);
        return result.Latin;
    }

    /// <summary>The transliteration rule set recorded with generated Latin forms (REQ-PTY-062).</summary>
    public string TransliteratorVersion => transliterator.RuleSetId;

    /// <summary>The language's search key of a text (REQ-PTY-065).</summary>
    public string Key(string text) => languageRules.SearchKey(text);

    /// <summary>
    /// Keys stored for one name form: the key of the whole name (FULL) and, per name part, its key and every
    /// transliteration variant (PART), so a query part in either script matches the part in any position (REQ-PTY-067).
    /// </summary>
    public async ValueTask<IReadOnlyList<(string Kind, string Key)>> StoredKeysAsync(string fullName, CancellationToken cancellationToken)
    {
        var keys = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var full = Key(fullName);
        if (full.Length > 0)
        {
            keys.Add(("FULL", full));
        }

        foreach (var part in full.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seen.Add(part))
            {
                keys.Add(("PART", part));
            }
        }

        foreach (var variant in await transliterator.SearchVariantsAsync(fullName, cancellationToken).ConfigureAwait(false))
        {
            // The first variant is the whole name; parts are single tokens. Store tokens only once.
            foreach (var part in variant.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (seen.Add(part))
                {
                    keys.Add(("PART", part));
                }
            }
        }

        return keys;
    }

    /// <summary>The keys of one query part: its own key plus its transliteration variants (REQ-PTY-067).</summary>
    public async ValueTask<IReadOnlyList<string>> QueryPartKeysAsync(string part, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        var own = Key(part);
        if (own.Length > 0)
        {
            keys.Add(own);
        }

        foreach (var variant in await transliterator.SearchVariantsAsync(part, cancellationToken).ConfigureAwait(false))
        {
            if (!keys.Contains(variant, StringComparer.Ordinal) && !variant.Contains(' ', StringComparison.Ordinal))
            {
                keys.Add(variant);
            }
        }

        return keys;
    }

    /// <summary>Splits a query into parts by the language rules (accents and punctuation removed).</summary>
    public IReadOnlyList<string> QueryParts(string query) =>
        Key(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Take(6).ToList();

    /// <summary>A person's display name "given family".</summary>
    public static string PersonDisplay(string? given, string? family) =>
        string.Join(' ', new[] { given, family }.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));

    /// <summary>Last characters of a value for masked display (REQ-PTY-044).</summary>
    public static string Mask(string suffix) => string.Create(CultureInfo.InvariantCulture, $"******{suffix}");
}
