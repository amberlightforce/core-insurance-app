using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.CountryPacks.GR.Transliteration;

/// <summary>
/// <c>NameTransliterator</c> SPI over ELOT 743 Type 2 (REQ-MKT-091, REQ-MKT-260, PRD-17 §9.4.2). Pure (REQ-MKT-117).
/// Greek (<c>Grek</c>) input is transcribed; Latin (<c>Latn</c>) input is returned unchanged; any other script is
/// VALIDATION <c>UNSUPPORTED_SCRIPT</c>; an unknown rule set is RULE_MISSING. The rule-set id is a constructor argument
/// because the Cyprus stub binds the same algorithm under its own id (PRD-17 §9.4.2, research F14).
/// </summary>
public sealed class GreekNameTransliterator : INameTransliterator
{
    /// <summary>Creates the Greece pack transliterator (<see cref="GrPack.ElotRuleSetId"/>).</summary>
    public GreekNameTransliterator()
        : this(GrPack.ElotRuleSetId)
    {
    }

    /// <summary>Creates a transliterator recording <paramref name="ruleSetId"/> (used by the Cyprus stub).</summary>
    public GreekNameTransliterator(string ruleSetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleSetId);
        RuleSetId = ruleSetId;
    }

    public string RuleSetId { get; }

    public ValueTask<TransliterationResult> TransliterateAsync(
        string text, string sourceScript, string? ruleSet = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sourceScript);

        if (ruleSet is not null && !string.Equals(ruleSet, RuleSetId, StringComparison.Ordinal))
        {
            throw new SpiException(
                new SpiError(SpiErrorCategory.RuleMissing, "RULE_SET_UNKNOWN"), $"Rule set '{ruleSet}' is not bound.");
        }

        switch (sourceScript)
        {
            case ScriptCodes.Latin:
                return ValueTask.FromResult(new TransliterationResult(text, RuleSetId, []));
            case ScriptCodes.Greek:
                var latin = ElotTransliterator.Transliterate(text, out var unmapped);
                IReadOnlyList<string> warnings = unmapped ? [TransliterationWarnings.UnmappedCharacter] : [];
                return ValueTask.FromResult(new TransliterationResult(latin, RuleSetId, warnings));
            default:
                throw new SpiException(
                    new SpiError(SpiErrorCategory.Validation, "UNSUPPORTED_SCRIPT", nameof(sourceScript)),
                    $"Script '{sourceScript}' is not supported by rule set {RuleSetId}.");
        }
    }

    public ValueTask<IReadOnlyList<string>> SearchVariantsAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ValueTask.FromResult(ElotTransliterator.SearchVariants(text));
    }
}
