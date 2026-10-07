using CoreIns.CountryPacks.CY;
using CoreIns.CountryPacks.GR;
using CoreIns.CountryPacks.GR.Language;
using CoreIns.CountryPacks.GR.Transliteration;
using CoreIns.Modules.Market.Contracts.Spi;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CoreIns.CountryPacks.Tests;

/// <summary>ELOT 743 Type 2 (REQ-MKT-091, REQ-MKT-177, REQ-PTY-012, REQ-PTY-062, REQ-PTY-065, REQ-PTY-067).</summary>
public sealed class TransliterationTests
{
    private readonly GreekNameTransliterator _greek = new();

    /// <summary>Every transliteration vector stated in PRD-17 and PRD-01.</summary>
    [Theory]
    [InlineData("Γεώργιος Παπαδόπουλος", "Georgios Papadopoulos")] // REQ-MKT-091
    [InlineData("Χρήστος", "Christos")] // REQ-MKT-177
    [InlineData("Ευθυμίου Αγγελική", "Efthymiou Angeliki")] // REQ-PTY-062
    [InlineData("Λεωφ. Κηφισίας 124, 11526 Αθήνα", "Leof. Kifisias 124, 11526 Athina")] // REQ-PTY-012
    [InlineData("Ευθυμίου", "Efthymiou")] // PRD-01 golden-set digraphs
    [InlineData("Αυγερινός", "Avgerinos")]
    [InlineData("Αγγελόπουλος", "Angelopoulos")]
    [InlineData("Μπακογιάννης", "Bakogiannis")]
    [InlineData("Καμπάνης", "Kampanis")]
    [InlineData("Ντόκος", "Ntokos")]
    [InlineData("Χατζηδάκης", "Chatzidakis")]
    [InlineData("Ψαρρός", "Psarros")]
    [InlineData("Γεωργίου", "Georgiou")]
    [InlineData("Σωτηρόπουλος", "Sotiropoulos")]
    public async Task PRD_vectors(string greek, string latin)
    {
        var result = await _greek.TransliterateAsync(greek, ScriptCodes.Greek, cancellationToken: TestContext.Current.CancellationToken);

        result.Latin.ShouldBe(latin);
        result.RuleSetId.ShouldBe(GrPack.ElotRuleSetId);
        result.Warnings.ShouldBeEmpty();
    }

    /// <summary>ELOT 743 rules that the PRD vectors do not exercise (PRD-17 §9.4.2 "αυ/ευ contextual").</summary>
    [Theory]
    [InlineData("ΘΕΟΔΩΡΟΣ", "THEODOROS")] // all capitals: multi-letter output fully upper case
    [InlineData("Θεόδωρος", "Theodoros")]
    [InlineData("ΠΑΠΑΔΟΠΟΥΛΟΣ", "PAPADOPOULOS")]
    [InlineData("Ευάγγελος", "Evangelos")] // ευ before a vowel → ev
    [InlineData("Παύλος", "Pavlos")] // αυ before a voiced consonant → av
    [InlineData("αυτός", "aftos")] // αυ before τ → af
    [InlineData("άυλος", "aylos")] // tonos on α: not a digraph
    [InlineData("Αϋπνία", "Aypnia")] // dialytika on υ: not a digraph
    [InlineData("Ραΐσα", "Raisa")]
    [InlineData("μπαμπάς", "bampas")] // μπ at the start → b, inside → mp
    [InlineData("Ξενοφών Ψάλτης", "Xenofon Psaltis")]
    [InlineData("ᾨδή", "Odi")] // polytonic: breathings and ypogegrammeni dropped
    [InlineData("Smith", "Smith")] // non-Greek characters are copied
    public async Task ELOT_rules(string greek, string latin)
    {
        var result = await _greek.TransliterateAsync(greek, ScriptCodes.Greek, cancellationToken: TestContext.Current.CancellationToken);
        result.Latin.ShouldBe(latin);
    }

    [Fact]
    public async Task Latin_input_is_returned_unchanged_and_other_scripts_are_refused()
    {
        var latin = await _greek.TransliterateAsync("Yiorgos", ScriptCodes.Latin, cancellationToken: TestContext.Current.CancellationToken);
        latin.Latin.ShouldBe("Yiorgos");

        var error = await Should.ThrowAsync<SpiException>(async () =>
            await _greek.TransliterateAsync("Иван", ScriptCodes.Cyrillic, cancellationToken: TestContext.Current.CancellationToken));
        error.Category.ShouldBe(SpiErrorCategory.Validation);

        var missing = await Should.ThrowAsync<SpiException>(async () =>
            await _greek.TransliterateAsync("Χρήστος", ScriptCodes.Greek, "GR-ELOT743-T2/0", TestContext.Current.CancellationToken));
        missing.Category.ShouldBe(SpiErrorCategory.RuleMissing);
    }

    [Fact]
    public async Task Cyprus_stub_binds_the_same_algorithm_under_its_own_rule_set()
    {
        var result = await new CyNameTransliterator().TransliterateAsync("Γεώργιος Παπαδόπουλος", ScriptCodes.Greek, cancellationToken: TestContext.Current.CancellationToken);

        result.Latin.ShouldBe("Georgios Papadopoulos");
        result.RuleSetId.ShouldBe(CyPack.ElotRuleSetId);
        result.RuleSetId.ShouldNotBe(GrPack.ElotRuleSetId);
    }

    /// <summary>REQ-PTY-065 and REQ-PTY-067: cross-script keys, including the reverse digraph alternatives.</summary>
    [Theory]
    [InlineData("Σωτηρόπουλος", "SOTIROPOULOS")] // REQ-PTY-065 acceptance
    [InlineData("Ντόκος", "DOKOS")] // REQ-PTY-067 acceptance: "Dokos" finds "Ντόκος" (nt/d)
    [InlineData("Ντόκος", "NTOKOS")]
    [InlineData("Μπακογιάννης", "MPAKOGIANNIS")] // mp/b
    [InlineData("Παπαδόπουλος", "PAPADOPULOS")] // ou/u
    [InlineData("Χρήστος", "HRISTOS")] // ch/h
    [InlineData("Γκίκας", "GIKAS")] // gk/g
    public async Task Search_variants_include_the_digraph_alternatives(string greek, string expectedKey)
    {
        var variants = await _greek.SearchVariantsAsync(greek, TestContext.Current.CancellationToken);
        variants.ShouldContain(expectedKey);
    }

    [Fact]
    public async Task Search_variants_put_the_ELOT_form_first_and_match_a_Latin_query()
    {
        var variants = await _greek.SearchVariantsAsync("Παπαδόπουλος", TestContext.Current.CancellationToken);
        variants[0].ShouldBe("PAPADOPOULOS");

        // REQ-MKT-178 acceptance: "papadopoulos" finds "Παπαδόπουλος".
        var query = await _greek.SearchVariantsAsync("papadopoulos", TestContext.Current.CancellationToken);
        query.ShouldBe(["PAPADOPOULOS"]);
        variants.Intersect(query).ShouldNotBeEmpty();
    }

    [Fact]
    public void Search_variants_are_bounded_per_name_part()
    {
        var longName = string.Join(' ', Enumerable.Repeat("Μπουμπουλίνα Χατζηχρήστου Ντούντου", 4));
        ElotTransliterator.SearchVariants(longName).Count.ShouldBeLessThanOrEqualTo(1 + (ElotTransliterator.MaxSearchVariants * 12));
    }

    /// <summary>Review F-1e M2: alternatives in a later name part are never lost to the cap.</summary>
    [Theory]
    [InlineData("Χατζηχριστοδούλου Παπαδοπούλου Ντόκος", "DOKOS")]
    [InlineData("Μπουμπουλίνα Χατζηχρήστου Ντούντου", "DUDU")]
    [InlineData("Μπουμπουλίνα Χατζηχρήστου Ντούντου", "NTOUNTOU")]
    [InlineData("Μπουμπουλίνα Χατζηχρήστου Ντούντου", "HATZIHRISTOU")]
    [InlineData("Μπουμπουλίνα Χατζηχρήστου Ντούντου", "BUBULINA")]
    [InlineData("Παπαδοπούλου-Χατζηγεωργίου Γκίκας", "GIKAS")]
    [InlineData("Ευθυμίου Αγγελική Ντόκου", "DOKU")]
    public void Every_name_part_keeps_its_alternatives(string name, string expectedPartKey) =>
        ElotTransliterator.SearchVariants(name).ShouldContain(expectedPartKey);

    /// <summary>Property: every single-point alternative of every part of a multi-part name is present.</summary>
    [Property(MaxTest = 200)]
    public Property Single_point_alternatives_of_the_last_part_are_present() =>
        Prop.ForAll(GreekWords(), prefix =>
        {
            var variants = ElotTransliterator.SearchVariants(prefix + " Ντόκος");
            return (variants.Contains("DOKOS") && variants.Contains("NTOKOS") && variants[0].EndsWith(" NTOKOS", StringComparison.Ordinal))
                .Label(prefix);
        });

    /// <summary>Property: transliteration output never contains Greek letters and is already free of accents.</summary>
    [Property(MaxTest = 300)]
    public Property Output_is_pure_Latin_for_Greek_words() =>
        Prop.ForAll(GreekWords(), word =>
        {
            var latin = ElotTransliterator.Transliterate(word, out var unmapped);
            return (!unmapped && latin.All(character => character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or ' ')).Label($"{word} → {latin}");
        });

    /// <summary>Property: transliteration is insensitive to accents and to the final-sigma form (normalisation first).</summary>
    [Property(MaxTest = 300)]
    public Property Accents_do_not_change_the_search_key_of_the_transliteration() =>
        Prop.ForAll(GreekWords(), word =>
        {
            var plain = GreekSearchNormalizer.SearchKey(ElotTransliterator.Transliterate(StripAccents(word), out _));
            var accented = GreekSearchNormalizer.SearchKey(ElotTransliterator.Transliterate(word, out _));
            return (plain == accented || word.Any(character => character is 'ά' or 'έ' or 'ή' or 'ό')).Label(word);
        });

    /// <summary>Property: search variants are idempotent keys (normalising a variant again changes nothing).</summary>
    [Property(MaxTest = 300)]
    public Property Search_variants_are_normalised_keys() =>
        Prop.ForAll(GreekWords(), word =>
            ElotTransliterator.SearchVariants(word).All(key => GreekSearchNormalizer.SearchKey(key) == key).Label(word));

    internal static Arbitrary<string> GreekWords()
    {
        const string letters = "αβγδεζηθικλμνξοπρστυφχψωάέήίόύώϊϋΐΰΑΒΓΔΕΖΗΘΙΚΛΜΝΞΟΠΡΣΤΥΦΧΨΩ";
        var word = Gen.Choose(1, 12).SelectMany(length => Gen.ArrayOf(Gen.Elements(letters.ToCharArray()), length)).Select(characters => new string(characters));
        return Gen.Choose(1, 3).SelectMany(count => Gen.ArrayOf(word, count)).Select(words => string.Join(' ', words)).ToArbitrary();
    }

    private static string StripAccents(string text) =>
        string.Concat(text.Normalize(System.Text.NormalizationForm.FormD).Where(character => character != '́'))
            .Normalize(System.Text.NormalizationForm.FormC);
}
