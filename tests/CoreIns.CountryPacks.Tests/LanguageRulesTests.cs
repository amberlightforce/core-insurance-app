using CoreIns.CountryPacks.GR.Language;
using CoreIns.Modules.Market.Contracts.Localisation;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CoreIns.CountryPacks.Tests;

/// <summary>Greek language rules (REQ-MKT-340, REQ-MKT-178, REQ-PTY-065, NFR-PTY-013; DESIGN-B §D.3).</summary>
public sealed class LanguageRulesTests
{
    private readonly GreekLanguageRules _rules = new();

    /// <summary>DESIGN-B §D.3 mandatory cases (positive and negative) and REQ-MKT-340 "οδός" → "ΟΔΟΣ".</summary>
    [Theory]
    [InlineData("οδός", "ΟΔΟΣ")]
    [InlineData("Μάιος", "ΜΑΪΟΣ")]
    [InlineData("ρολόι", "ΡΟΛΟΪ")]
    [InlineData("πρωτεΐνη", "ΠΡΩΤΕΪΝΗ")]
    [InlineData("αϋπνία", "ΑΫΠΝΙΑ")]
    [InlineData("ευρώ", "ΕΥΡΩ")]
    [InlineData("Ασφαλιστήριο", "ΑΣΦΑΛΙΣΤΗΡΙΟ")]
    [InlineData("ᾠδή", "ΩΙΔΗ")]
    [InlineData("άυλος", "ΑΫΛΟΣ")]
    [InlineData("παίζω", "ΠΑΙΖΩ")]
    [InlineData("αύριο", "ΑΥΡΙΟ")]
    [InlineData("Παπαδόπουλος", "ΠΑΠΑΔΟΠΟΥΛΟΣ")]
    public void Upper_case_has_no_tonos(string text, string upper) => _rules.CaseMap(text, CaseMapMode.Upper).ShouldBe(upper);

    [Theory]
    [InlineData("ΟΔΟΣ", "οδος")]
    [InlineData("ΠΑΠΑΔΟΠΟΥΛΟΣ ΓΕΩΡΓΙΟΣ", "παπαδοπουλος γεωργιος")]
    [InlineData("ΣΟΦΙΑ", "σοφια")] // σ at the start of a word stays σ
    [InlineData("ΟΔΟΣ.", "οδος.")]
    [InlineData("Σ", "σ")] // a lone sigma is not a word ending
    public void Lower_case_restores_the_final_sigma(string text, string lower) => _rules.CaseMap(text, CaseMapMode.Lower).ShouldBe(lower);

    [Fact]
    public void Title_case_capitalises_each_word_and_keeps_tonos() =>
        _rules.CaseMap("ΠΑΠΑΔΟΠΟΥΛΟΣ γεώργιος", CaseMapMode.Title).ShouldBe("Παπαδοπουλος Γεώργιος");

    /// <summary>REQ-MKT-178 / REQ-MKT-340 acceptance and REQ-PTY-065 example.</summary>
    [Theory]
    [InlineData("Σωτηρόπουλος", "ΣΩΤΗΡΟΠΟΥΛΟΣ")]
    [InlineData("Παπαδόπουλος", "ΠΑΠΑΔΟΠΟΥΛΟΣ")]
    [InlineData("ΠΑΠΑΔΟΠΟΥΛΟΣ", "ΠΑΠΑΔΟΠΟΥΛΟΣ")]
    [InlineData("παπαδοπουλοσ", "ΠΑΠΑΔΟΠΟΥΛΟΣ")]
    [InlineData("  Παπα-Γεωργίου,  Ελένη ", "ΠΑΠΑ ΓΕΩΡΓΙΟΥ ΕΛΕΝΗ")]
    [InlineData("Ραΐσα", "ΡΑΙΣΑ")]
    [InlineData("Müller", "MULLER")]
    [InlineData("İsmail Kılıç", "ISMAIL KILIC")] // dotless i: Unicode simple upper case, as in PostgreSQL
    [InlineData("", "")]
    public void Search_key_folds_case_accents_dialytika_final_sigma_and_punctuation(string text, string key) =>
        _rules.SearchKey(text).ShouldBe(key);

    [Fact]
    public void Sort_comparer_sorts_accented_letters_with_their_base_and_digits_numerically()
    {
        string[] input = ["ΑΣΦ-10", "Ωμέγα", "άλφα", "ΑΣΦ-2", "Βήτα", "αλφάβητο"];
        input.Order(_rules.SortComparer).ShouldBe(["άλφα", "αλφάβητο", "ΑΣΦ-2", "ΑΣΦ-10", "Βήτα", "Ωμέγα"]);
        _rules.SortComparer.Compare("Παπαδόπουλος", "ΠΑΠΑΔΟΠΟΥΛΟΣ").ShouldBe(0);
    }

    /// <summary>Property: the search key is idempotent.</summary>
    [Property(MaxTest = 500)]
    public Property Search_key_is_idempotent() =>
        Prop.ForAll(MixedText(), text =>
        {
            var key = GreekSearchNormalizer.SearchKey(text);
            return (GreekSearchNormalizer.SearchKey(key) == key).Label(text);
        });

    /// <summary>Property: case and accents never change the search key of Greek text.</summary>
    [Property(MaxTest = 500)]
    public Property Search_key_is_case_and_accent_insensitive() =>
        Prop.ForAll(TransliterationTests.GreekWords(), text =>
        {
            var key = GreekSearchNormalizer.SearchKey(text);
            return (GreekSearchNormalizer.SearchKey(GreekCaseMapper.ToUpper(text)) == key
                    && GreekSearchNormalizer.SearchKey(GreekCaseMapper.ToLower(text)) == key).Label(text);
        });

    /// <summary>Property: upper-casing is idempotent and never leaves an acute accent.</summary>
    [Property(MaxTest = 500)]
    public Property Upper_case_is_idempotent_and_tonos_free() =>
        Prop.ForAll(TransliterationTests.GreekWords(), text =>
        {
            var upper = GreekCaseMapper.ToUpper(text);
            return (GreekCaseMapper.ToUpper(upper) == upper
                    && !upper.Normalize(System.Text.NormalizationForm.FormD).Contains('́', StringComparison.Ordinal)).Label(text);
        });

    private static Arbitrary<string> MixedText()
    {
        const string characters = "αβγάέήίόύώϊϋΐΰςσΣΑΩabcABCéèüñçßøÅ0129 -.,'’\tИванЁё";
        return Gen.Choose(0, 30).SelectMany(length => Gen.ArrayOf(Gen.Elements(characters.ToCharArray()), length)).Select(chars => new string(chars)).ToArbitrary();
    }
}
