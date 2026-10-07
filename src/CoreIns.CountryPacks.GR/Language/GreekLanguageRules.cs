using System.Globalization;
using CoreIns.Modules.Market.Contracts.Localisation;

namespace CoreIns.CountryPacks.GR.Language;

/// <summary>
/// Greek language rules (REQ-MKT-340, REQ-MKT-178, REQ-PTY-065, NFR-PTY-013): search keys with tonos, dialytika,
/// case and final-sigma folding (<see cref="GreekSearchNormalizer"/>), CLDR el-Upper case mapping without tonos and
/// context-sensitive final sigma (<see cref="GreekCaseMapper"/>), and el-GR collation for sorting: accent- and
/// case-insensitive at the primary level, digits compared numerically ("ΑΣΦ-2" before "ΑΣΦ-10", DESIGN-B §D.3).
/// The database counterpart of the collation is <c>el_gr_ci_ai</c> in <c>infra/database/greek-search.sql</c>.
/// </summary>
public sealed class GreekLanguageRules : ILanguageRuleSet
{
    /// <summary>Sorting options: ignore case and accents (UCA primary strength) and order digit runs numerically.</summary>
    public const CompareOptions SortOptions =
        CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType
        | CompareOptions.IgnoreWidth | CompareOptions.NumericOrdering;

    public string Language => GrPack.Language;

    public string RuleVersion => GrPack.LanguageRuleVersion;

    public StringComparer SortComparer { get; } = StringComparer.Create(CultureInfo.GetCultureInfo("el-GR"), SortOptions);

    public string SearchKey(string text) => GreekSearchNormalizer.SearchKey(text);

    public string CaseMap(string text, CaseMapMode mode) => mode switch
    {
        CaseMapMode.Upper => GreekCaseMapper.ToUpper(text),
        CaseMapMode.Lower => GreekCaseMapper.ToLower(text),
        CaseMapMode.Title => GreekCaseMapper.ToTitle(text),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown case-map mode."),
    };
}
