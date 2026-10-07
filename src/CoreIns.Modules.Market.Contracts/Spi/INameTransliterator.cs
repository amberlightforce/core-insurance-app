namespace CoreIns.Modules.Market.Contracts.Spi;

/// <summary>
/// SPI 2 <c>NameTransliterator</c> (REQ-MKT-091; PRD-17 §9.4.2; spi.md §2): native-script names and address lines to
/// Latin, with the rule-set version returned; and search variants. Binding axis SCHEME (script + country). Mode S,
/// pure (REQ-MKT-117), 20 ms, fail closed. Core default: identity for Latin input, error for non-Latin input.
/// Errors: VALIDATION (unsupported script), RULE_MISSING.
/// </summary>
public interface INameTransliterator
{
    /// <summary>The rule-set id this implementation applies by default (recorded with every generated Latin form).</summary>
    string RuleSetId { get; }

    /// <summary>
    /// <c>transliterate(text, sourceScript, ruleSet?) → {latin, ruleSetId, warnings[]}</c>.
    /// </summary>
    /// <param name="text">Native-script text (NFC).</param>
    /// <param name="sourceScript">ISO 15924 script code of the text (for example <c>Latn</c>, <c>Cyrl</c>).</param>
    /// <param name="ruleSet">Optional rule-set id to apply; null = the bound default. An unknown id is RULE_MISSING.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<TransliterationResult> TransliterateAsync(
        string text, string sourceScript, string? ruleSet = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>searchVariants(text) → keys[]</c>: the only source of transliteration and Greeklish-style match variants
    /// (REQ-MKT-178, REQ-PTY-067). Keys are already search-normalised (accent-free, upper case, single spaces).
    /// Language folding itself comes from the MKT language rules (REQ-MKT-340).
    /// </summary>
    ValueTask<IReadOnlyList<string>> SearchVariantsAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>Result of <c>transliterate</c>.</summary>
/// <param name="Latin">Latin-script form.</param>
/// <param name="RuleSetId">Rule set applied (stored as the transliterator version, REQ-PTY-062).</param>
/// <param name="Warnings">Warning codes (for example characters passed through unchanged).</param>
public sealed record TransliterationResult(string Latin, string RuleSetId, IReadOnlyList<string> Warnings);

/// <summary>ISO 15924 script codes used by the SPI signatures.</summary>
public static class ScriptCodes
{
    public const string Latin = "Latn";
    public const string Greek = "Grek";
    public const string Cyrillic = "Cyrl";
}

/// <summary>Warning codes of <c>transliterate</c>.</summary>
public static class TransliterationWarnings
{
    /// <summary>A character outside the rule set was copied unchanged.</summary>
    public const string UnmappedCharacter = "UNMAPPED_CHARACTER";
}
