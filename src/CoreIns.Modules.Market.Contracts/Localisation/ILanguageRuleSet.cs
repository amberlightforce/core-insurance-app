namespace CoreIns.Modules.Market.Contracts.Localisation;

/// <summary>
/// Language rules of one language, held as MKT language data and supplied by a pack (REQ-MKT-340, REQ-MKT-178).
/// <c>mkt.L10n.searchKeys(text, language)</c> and <c>mkt.L10n.caseMap(text, language, mode)</c> delegate to the rule set
/// bound for the language tag, so PTY, WRK and DOC never state language rules in core code. This is language data,
/// not one of the 40 SPIs of PRD-17 §9.4 (transliteration variants stay with <c>NameTransliterator.searchVariants</c>).
/// </summary>
public interface ILanguageRuleSet
{
    /// <summary>BCP 47 language tag (for example <c>el</c>).</summary>
    string Language { get; }

    /// <summary>Version of the rule data (recorded with keys; versioned with the pinned CLDR version, REQ-MKT-340).</summary>
    string RuleVersion { get; }

    /// <summary>
    /// Accent-insensitive, case-insensitive search key of <paramref name="text"/> in this language (REQ-MKT-178):
    /// stable, idempotent and equal for inputs that differ only in case, accents or punctuation.
    /// </summary>
    string SearchKey(string text);

    /// <summary>Language-correct case mapping (REQ-MKT-340).</summary>
    string CaseMap(string text, CaseMapMode mode);

    /// <summary>Culture-correct comparer for sorting in this language (NFR-PTY-013).</summary>
    StringComparer SortComparer { get; }
}

/// <summary>Case-mapping mode of <c>mkt.L10n.caseMap</c> (PRD-17 §9 operation list).</summary>
public enum CaseMapMode
{
    Upper,
    Lower,
    Title,
}
