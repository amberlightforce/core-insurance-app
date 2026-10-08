using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Modules.Finance.Posting;
using CoreIns.SharedKernel;

namespace CoreIns.IntegrationTests.Finance;

/// <summary>Pure FIN rules (no database): rule resolution and compile checks, entry mapping, the double-entry invariant.</summary>
public sealed class FinanceDomainTests
{
    private const string Source = "bil.BillingEntryPosted";
    private static readonly string Seed = FinanceSeed.GrTestV2;

    private static PostingRule Rule(string code, string entryType, string account, string? category = null, string? chargeType = null, string? target = "GL-1110", string? derive = null) =>
        new(code, Source, entryType, account, category, chargeType, target, derive, code, code);

    private static BookSetup SeedBook()
    {
        var accounts = FinanceSeed.ChartCodes(Seed).ToDictionary(c => c, c => new ChartAccount(c, c, c, true), StringComparer.Ordinal);
        return new BookSetup("IFRS17", Currency.EUR,
            new RuleSet(Guid.CreateVersion7(), "GR-TEST", "IFRS17", 2, new BusinessDate(2026, 1, 1), CoreIns.SharedKernel.Identifiers.Sha256Hash.Parse(FinanceSeed.RuleSetHash(Seed)), FinanceSeed.Rules(Seed)),
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
    public void REQ_FIN_052_the_applied_seeds_are_immutable_their_content_hashes_are_pinned()
    {
        // A change to the rule set is a new seed file and a new rule-set version, never an edit of an applied one (append-only rules).
        FinanceSeed.RuleSetHash(FinanceSeed.GrTestV1).ShouldBe(PinnedV1Hash);
        FinanceSeed.RuleSetHash(FinanceSeed.GrTestV2).ShouldBe(PinnedV2Hash);
    }

    // Pinned when gr-test.finance.v1 was applied by migration InitialFinance.
    private const string PinnedV1Hash = "c2aebbf2b46b1cfce32372c5b6f3a797d7267667973e47e5ff05aff69ac0c4ba";

    // Pinned when gr-test.finance.v2 was applied by migration ClaimsPostings (SL2-FIN-CLM).
    private const string PinnedV2Hash = "d5801ba738fc114622d0e99b7b193cadd657a66d9702bed06234ec2b8e279b51";

    [Fact]
    public void DSL208_rule_set_v2_keeps_every_v1_rule_and_chart_account_and_adds_the_claims_postings()
    {
        var v1 = FinanceSeed.Rules(FinanceSeed.GrTestV1);
        var v2 = FinanceSeed.Rules(FinanceSeed.GrTestV2);
        v1.Where(r => !v2.Contains(r)).ShouldBeEmpty();
        FinanceSeed.ChartCodes(FinanceSeed.GrTestV1).Where(c => !FinanceSeed.ChartCodes(FinanceSeed.GrTestV2).Contains(c)).ShouldBeEmpty();
        v2.Where(r => !v1.Contains(r)).Select(r => $"{r.SourceEvent}/{r.EntryType}/{r.SourceAccount}->{r.Account}").Order(StringComparer.Ordinal).ShouldBe(
        [
            "bil.BillingEntryPosted/DISBURSEMENT_CLEARED/LA-10->GL-1110",
            "bil.BillingEntryPosted/DISBURSEMENT_CLEARED/LA-13->GL-2530",
            "bil.BillingEntryPosted/DISBURSEMENT_RELEASED/LA-13->GL-2530",
            "bil.BillingEntryPosted/DISBURSEMENT_RELEASED/LA-17->GL-2510",
            "clm.PaymentIssued/PAYMENT/CLM-CASE-RESERVE->GL-2210",
            "clm.PaymentIssued/PAYMENT/CLM-PAYMENT-CLEARING->GL-2510",
            "clm.ReserveChanged/RESERVE/CLM-CASE-RESERVE->GL-2210",
            "clm.ReserveChanged/RESERVE/CLM-INCURRED->GL-5110",
        ]);
    }

    private static string Eur3(string amount) =>
        $$$"""{"transaction":{"amount":"{{{amount}}}","currency":"EUR"},"functional":{"amount":"{{{amount}}}","currency":"EUR"},"group":{"amount":"{{{amount}}}","currency":"EUR"}}""";

    [Fact]
    public void REQ_FIN_034_158_a_reserve_release_reverses_incurred_and_LIC_with_claim_dimensions()
    {
        var (claim, exposure, line, term) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var payload = $$"""
            {"claimId":"{{claim}}","exposureId":"{{exposure}}","reserveLineId":"{{line}}","reserveLine":{"costType":"INDEMNITY","category":"VEHICLE_REPAIR"},
             "kind":"RESERVE","delta":{{Eur3("-300.00")}},"newOpenAmount":{{Eur3("0.00")}},"setId":"{{Guid.CreateVersion7()}}","accidentDate":"2026-11-01",
             "policyTermId":"{{term}}","productCode":"MOTOR-GR","siiLob":"UNMAPPED","ifrs17GroupRef":null,"catCode":null,"handlingSegment":"STANDARD","accountingDate":null}
            """;
        var entry = ClaimFacts.Normalise(ClaimFacts.ReserveChanged, Guid.CreateVersion7(), payload, null, new BusinessDate(2026, 11, 6));
        entry.AccountingDate.ShouldBe(new BusinessDate(2026, 11, 6));
        entry.NeedsPolicyContext.ShouldBeFalse();

        var journal = EntryPosting.Map(ClaimFacts.ReserveChanged, entry, SeedBook(), _ => null, (_, _) => null).Journal.ShouldNotBeNull();
        journal.RuleCodes.ShouldBe(["CR-INCURRED", "CR-CASE-RESERVE"]);
        journal.Lines.Select(l => $"{l.Account}:{l.Side}:{l.Amount}").ShouldBe(["GL-5110:CREDIT:300.00 EUR", "GL-2210:DEBIT:300.00 EUR"]);
        journal.Lines[0].Dimensions.ShouldBe(new LineDimensions
        {
            ClaimId = claim, ExposureId = exposure, ReserveLineId = line, CostType = "INDEMNITY", CostCategory = "VEHICLE_REPAIR", PolicyTermId = term, ProductCode = "MOTOR-GR",
        });
    }

    [Fact]
    public void REQ_FIN_037_159_an_eroding_payment_moves_LIC_to_the_claim_payment_clearing_account_per_payment()
    {
        var (claim, payment, disbursement) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var payload = $$"""
            {"paymentId":"{{payment}}","transactionIds":["{{Guid.CreateVersion7()}}"],"lines":[{"lineKey":"t1","amount":{{Eur3("6200.00")}},
             "reserveLineId":"{{Guid.CreateVersion7()}}","exposureId":"{{Guid.CreateVersion7()}}","costType":"INDEMNITY","costCategory":"VEHICLE_REPAIR","eroding":true}],
             "amount":{{Eur3("6200.00")}},"payeePartyId":"{{Guid.CreateVersion7()}}","method":"SEPA_CT","disbursementId":"{{disbursement}}","exGratia":false,
             "accountingDate":"2026-11-05"}
            """;
        var entry = ClaimFacts.Normalise(ClaimFacts.PaymentIssued, Guid.CreateVersion7(), payload, claim, new BusinessDate(2026, 11, 6));
        entry.AccountingDate.ShouldBe(new BusinessDate(2026, 11, 5));
        entry.SourceRef.ShouldBe(payment.ToString());

        var journal = EntryPosting.Map(ClaimFacts.PaymentIssued, entry, SeedBook(), _ => null, (_, _) => null).Journal.ShouldNotBeNull();
        journal.RuleCodes.ShouldBe(["CP-CASE-RESERVE", "CP-CLAIM-CLEARING"]);
        journal.Lines.Select(l => $"{l.Account}:{l.Side}:{l.Amount}").ShouldBe(["GL-2210:DEBIT:6200.00 EUR", "GL-2510:CREDIT:6200.00 EUR"]);
        journal.Lines.ShouldAllBe(l => l.Dimensions.ClaimId == claim && l.Dimensions.ClaimPaymentId == payment && l.Dimensions.DisbursementId == disbursement);

        // A non-eroding line has no rule in the slice (PRD-09 names no account): an intake exception, never a default account.
        var nonEroding = ClaimFacts.Normalise(ClaimFacts.PaymentIssued, Guid.CreateVersion7(),
            payload.Replace("\"eroding\":true", "\"eroding\":false", StringComparison.Ordinal), claim, new BusinessDate(2026, 11, 6));
        EntryPosting.Map(ClaimFacts.PaymentIssued, nonEroding, SeedBook(), _ => null, (_, _) => null).Reason.ShouldBe(ExceptionReasons.NoRule);
    }

    [Fact]
    public void DSL208_BIL_disbursement_entries_post_clearing_in_transit_and_cash_with_the_claim_payment_from_their_source_id()
    {
        var (claim, payment, disbursement) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        SourceLine Leg(string account, string side) => new(account, side, Money.Of(6200m, "EUR"), new Dictionary<string, string?>
        {
            [LineDimensionKeys.DisbursementId] = disbursement.ToString(),
            [LineDimensionKeys.SourceType] = DisbursementSources.ClaimPayment,
            [LineDimensionKeys.SourceId] = payment.ToString(),
            [LineDimensionKeys.ClaimId] = claim.ToString(),
        });

        var released = Map("DISBURSEMENT_RELEASED", Leg("LA-17", Sides.Debit), Leg("LA-13", Sides.Credit)).Journal.ShouldNotBeNull();
        released.Lines.Select(l => $"{l.Account}:{l.Side}").ShouldBe(["GL-2510:DEBIT", "GL-2530:CREDIT"]);
        released.Lines.ShouldAllBe(l => l.Dimensions.ClaimPaymentId == payment && l.Dimensions.DisbursementId == disbursement && l.Dimensions.ClaimId == claim);
        Map("DISBURSEMENT_CLEARED", Leg("LA-13", Sides.Debit), Leg("LA-10", Sides.Credit)).Journal.ShouldNotBeNull()
            .Lines.Select(l => $"{l.Account}:{l.Side}").ShouldBe(["GL-2530:DEBIT", "GL-1110:CREDIT"]);
    }

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
