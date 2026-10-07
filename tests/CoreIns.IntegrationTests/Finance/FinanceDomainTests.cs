using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.SharedKernel;

namespace CoreIns.IntegrationTests.Finance;

/// <summary>Pure FIN rules (no database): rule resolution and compile checks, entry mapping, the double-entry invariant.</summary>
public sealed class FinanceDomainTests
{
    private const string Source = "bil.BillingEntryPosted";
    private static readonly string Seed = FinanceSeed.GrTestV1;

    private static PostingRule Rule(string code, string entryType, string account, string? category = null, string? chargeType = null, string? target = "GL-1110", string? derive = null) =>
        new(code, Source, entryType, account, category, chargeType, target, derive, code, code);

    private static BookSetup SeedBook()
    {
        var accounts = FinanceSeed.ChartCodes(Seed).ToDictionary(c => c, c => new ChartAccount(c, c, c, true), StringComparer.Ordinal);
        return new BookSetup("IFRS17", Currency.EUR,
            new RuleSet(Guid.CreateVersion7(), "GR-TEST", "IFRS17", 1, new BusinessDate(2026, 1, 1), CoreIns.SharedKernel.Identifiers.Sha256Hash.Parse(FinanceSeed.RuleSetHash(Seed)), FinanceSeed.Rules(Seed)),
            accounts, FinanceSeed.Derivations(Seed));
    }

    private static readonly PolicyContext Context = new(Guid.CreateVersion7(), "POL000000001", Guid.CreateVersion7(), Guid.CreateVersion7(), "MOTOR-GR", "1.0", new string('a', 64), true);

    private static SourceLine Line(string account, string side, decimal amount, string? chargeType = null, string? category = null, string currency = "EUR") =>
        new(account, side, Money.Of(amount, currency), new Dictionary<string, string?>
        {
            [LineDimensionKeys.ChargeType] = chargeType,
            [LineDimensionKeys.ChargeCategory] = category,
            [LineDimensionKeys.PolicyTermId] = Context.TermId.ToString(),
            [LineDimensionKeys.CoverageCode] = chargeType == "PREM-OD" ? "OWN-DAMAGE" : "MTPL",
        });

    private static PostingOutcome Map(string entryType, params SourceLine[] lines) =>
        EntryPosting.Map(Source, new SourceEntry(Guid.CreateVersion7(), entryType, new BusinessDate(2026, 11, 2), new BusinessDate(2026, 11, 2), lines), SeedBook(),
            _ => Context, (_, chargeType) => chargeType switch { "PREM-MTPL" => "PREM-MOTOR-MTPL", "PREM-OD" => "PREM-MOTOR-OD", "GR-IPT" => "TAX-IPT", _ => null });

    [Fact]
    public void REQ_FIN_053_D_SLC_10b_the_shipped_rule_set_compiles_and_every_PFC_GL_key_of_the_motor_product_derives_an_account()
    {
        PostingRules.Compile(FinanceSeed.Rules(Seed), FinanceSeed.ChartCodes(Seed), FinanceSeed.Derivations(Seed)).ShouldBeEmpty();

        var product = System.Text.Json.Nodes.JsonNode.Parse(CoreIns.Modules.Product.ProductSeeds.MotorPrivateCarJson())!;
        var glKeys = product["chargeTypes"]!.AsArray().Select(c => c!["glKey"]!.GetValue<string>()).ToList();
        glKeys.ShouldNotBeEmpty();
        glKeys.Where(k => !FinanceSeed.Derivations(Seed).ContainsKey(k)).ShouldBeEmpty();
    }

    [Fact]
    public void REQ_FIN_052_the_v1_seed_is_immutable_its_content_hash_is_pinned()
    {
        // A change to the rule set is a new seed file and a new rule-set version, never an edit of v1 (append-only rules).
        FinanceSeed.RuleSetHash(Seed).ShouldBe(PinnedV1Hash);
    }

    // Pinned when gr-test.finance.v1 was applied by migration InitialFinance.
    private const string PinnedV1Hash = "c2aebbf2b46b1cfce32372c5b6f3a797d7267667973e47e5ff05aff69ac0c4ba";

    [Fact]
    public void REQ_FIN_049_the_most_specific_rule_wins_and_ties_or_gaps_are_reported()
    {
        var general = Rule("GEN", "WRITTEN", "LA-04", "PREMIUM");
        var specific = Rule("SPEC", "WRITTEN", "LA-04", "PREMIUM", "PREM-OD", "GL-2110");
        PostingRules.Resolve([general, specific], Source, "WRITTEN", "LA-04", "PREMIUM", "PREM-OD").Rule.ShouldBe(specific);
        PostingRules.Resolve([general, specific], Source, "WRITTEN", "LA-04", "PREMIUM", "PREM-MTPL").Rule.ShouldBe(general);
        PostingRules.Resolve([general], Source, "WRITTEN", "LA-04", "TAX", null).Reason.ShouldBe(ExceptionReasons.NoRule);

        var byType = Rule("TYPE", "WRITTEN", "LA-04", chargeType: "PREM-OD");
        PostingRules.Resolve([general, byType], Source, "WRITTEN", "LA-04", "PREMIUM", "PREM-OD").Reason.ShouldBe(ExceptionReasons.AmbiguousRule);
        PostingRules.Compile([general, byType], new HashSet<string> { "GL-1110" }, new Dictionary<string, string>()).ShouldContain(e => e.Contains("GEN and TYPE"));
        PostingRules.Compile([Rule("X", "WRITTEN", "LA-01", target: "GL-0000")], new HashSet<string> { "GL-1110" }, new Dictionary<string, string>())
            .ShouldContain(e => e.Contains("not in the chart"));
    }

    [Fact]
    public void REQ_FIN_036_050_075_written_premium_maps_per_coverage_to_the_derived_LRC_account_with_dimensions()
    {
        var outcome = Map("WRITTEN", Line("LA-01", Sides.Debit, 160m, "PREM-OD", "PREMIUM"), Line("LA-04", Sides.Credit, 160m, "PREM-OD", "PREMIUM"));

        var journal = outcome.Journal.ShouldNotBeNull();
        journal.RuleCodes.ShouldBe(["WR-WRITTEN-UNBILLED", "WR-PREMIUM"]);
        journal.Lines.Select(l => $"{l.Account}:{l.Side}:{l.Amount}").ShouldBe(["GL-1215:DEBIT:160 EUR", "GL-2110:CREDIT:160 EUR"]);
        var credit = journal.Lines[1].Dimensions;
        credit.GlKey.ShouldBe("PREM-MOTOR-OD");
        credit.CoverageCode.ShouldBe("OWN-DAMAGE");
        credit.PolicyNumber.ShouldBe(Context.PolicyNumber);
        credit.ProductVersion.ShouldBe("1.0");
        journal.Totals.ShouldBe([Money.Of(160m, "EUR")]);
    }

    [Fact]
    public void REQ_FIN_068_a_negative_amount_posts_on_the_opposite_side_and_zero_lines_are_skipped()
    {
        var journal = Map("WRITTEN",
            Line("LA-01", Sides.Debit, -50m, "PREM-MTPL", "PREMIUM"), Line("LA-04", Sides.Credit, -50m, "PREM-MTPL", "PREMIUM"),
            Line("LA-01", Sides.Debit, 0m, "GR-IPT", "TAX"), Line("LA-27", Sides.Credit, 0m, "GR-IPT", "TAX")).Journal.ShouldNotBeNull();
        journal.Lines.Select(l => $"{l.Account}:{l.Side}:{l.Amount}").ShouldBe(["GL-1215:CREDIT:50 EUR", "GL-2110:DEBIT:50 EUR"]);

        Map("WRITTEN", Line("LA-01", Sides.Debit, 0m, "GR-IPT", "TAX"), Line("LA-27", Sides.Credit, 0m, "GR-IPT", "TAX")).Reason.ShouldBe(ExceptionReasons.Empty);
    }

    [Fact]
    public void REQ_FIN_080_unpostable_entries_become_intake_exceptions_with_a_reason()
    {
        Map("WRITTEN", Line("LA-01", Sides.Debit, 10m, "PREM-XX", "PREMIUM"), Line("LA-04", Sides.Credit, 10m, "PREM-XX", "PREMIUM")).Reason.ShouldBe(ExceptionReasons.NoChargeType);
        Map("RECEIVED", Line("LA-10", Sides.Debit, 10m, currency: "USD"), Line("LA-11", Sides.Credit, 10m, currency: "USD")).Reason.ShouldBe(ExceptionReasons.RateMissing);
        Map("RECEIVED", Line("LA-10", Sides.Debit, 10.001m), Line("LA-11", Sides.Credit, 10.001m)).Reason.ShouldBe(ExceptionReasons.Precision);
        Map("RECEIVED", Line("LA-10", Sides.Debit, 10m), Line("LA-11", Sides.Credit, 9m)).Reason.ShouldBe(ExceptionReasons.Unbalanced);
        Map("REFUNDED", Line("LA-12", Sides.Debit, 10m), Line("LA-13", Sides.Credit, 10m)).Reason.ShouldBe(ExceptionReasons.NoRule);
    }

    [Fact]
    public void REQ_FIN_068_072_journals_balance_per_currency_and_a_reversal_mirrors_every_line()
    {
        var lines = new List<JournalLineDraft>
        {
            new(1, "GL-1110", Sides.Debit, Money.Of(10m, "EUR"), Money.Of(10m, "EUR"), "R", LineDimensions.None),
            new(2, "GL-2540", Sides.Credit, Money.Of(10m, "EUR"), Money.Of(10m, "EUR"), "R", LineDimensions.None),
        };
        Journals.Check(lines).ShouldBeEmpty();
        Journals.Check([lines[0]]).ShouldNotBeEmpty();
        Journals.Check([lines[0], lines[1] with { Amount = Money.Of(9.99m, "EUR") }]).ShouldNotBeEmpty();
        Journals.Check([lines[0], lines[1] with { FunctionalAmount = Money.Of(10m, "USD") }]).ShouldNotBeEmpty();
        Journals.Reverse(lines).Select(l => l.Side).ShouldBe([Sides.Credit, Sides.Debit]);
        Journals.Check(Journals.Reverse(lines)).ShouldBeEmpty();
    }
}
