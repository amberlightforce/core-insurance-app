using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Modules.Finance.Posting;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.IntegrationTests.Finance.Servicing;

/// <summary>
/// SL3-FIN-RULES without a database: rule set v3 (credits, refunds, refund disbursements) and the REQ-FIN-182/-183
/// tax-treatment check against <c>TaxCalculator.treatment</c> (D-SL3-05, D-SL3-06).
/// </summary>
public sealed class FinanceServicingRulesTests
{
    private const string Source = "bil.BillingEntryPosted";
    private static readonly string V2 = FinanceSeed.GrTestV2;
    private static readonly string V3 = FinanceSeed.GrTestV3;

    // Pinned when gr-test.finance.v3 was applied by migration ServicingPostings (SL3-FIN-RULES).
    private const string PinnedV3Hash = "8e69245fd1a0a683fe6efe18ee4455ec927b20a12cbb97bec60b5be9fca6010e";

    private static readonly Guid Entity = Guid.Parse("0192f0c4-0000-7000-8000-000000000001");
    private static readonly Guid Term = Guid.CreateVersion7();

    [Fact]
    public void REQ_FIN_052_the_applied_v3_seed_is_immutable_its_content_hash_is_pinned()
    {
        FinanceSeed.RuleSetHash(V3).ShouldBe(PinnedV3Hash);
    }

    [Fact]
    public void REQ_FIN_053_rule_set_v3_compiles_keeps_every_v2_rule_and_chart_account_and_adds_only_the_servicing_postings()
    {
        var v2 = FinanceSeed.Rules(V2);
        var v3 = FinanceSeed.Rules(V3);
        PostingRules.Compile(v3, FinanceSeed.ChartCodes(V3), FinanceSeed.Derivations(V3)).ShouldBeEmpty();

        // v2 is kept byte for byte (slice-2 postings unchanged); nothing is removed.
        v2.Where(r => !v3.Contains(r)).ShouldBeEmpty();
        FinanceSeed.ChartCodes(V2).Where(c => !FinanceSeed.ChartCodes(V3).Contains(c)).ShouldBeEmpty();
        FinanceSeed.Derivations(V2).Where(d => !FinanceSeed.Derivations(V3).Contains(d)).ShouldBeEmpty();

        v3.Where(r => !v2.Contains(r)).Select(r => $"{r.EntryType}/{r.SourceAccount}/{r.ChargeCategory ?? "-"}->{r.Account ?? r.DeriveFrom}").Order(StringComparer.Ordinal).ShouldBe(
        [
            "CREDIT_BILLED/LA-01/-->GL-1215",
            "CREDIT_BILLED/LA-02/-->GL-1210",
            "CREDIT_WRITTEN/LA-01/-->GL-1215",
            "CREDIT_WRITTEN/LA-04/PREMIUM->GL_KEY",
            "CREDIT_WRITTEN/LA-04/SURCHARGE->GL_KEY",
            "CREDIT_WRITTEN/LA-05/FEE->GL_KEY",
            "DISBURSEMENT_RELEASED/LA-12/-->GL-2535",
            "REFUND_APPROVED/LA-02/-->GL-1210",
            "REFUND_APPROVED/LA-12/-->GL-2535",
        ]);

        // The refund payable is the one new account, a technical placeholder (PRD-09 section 4 has no refund payable).
        FinanceSeed.ChartCodes(V3).Except(FinanceSeed.ChartCodes(V2)).ShouldBe(["GL-2535"]);
        FinanceSeed.Json(V3).ShouldContain("TECHNICAL_PLACEHOLDER");
    }

    [Fact]
    public void REQ_FIN_182_no_v3_rule_maps_a_tax_or_levy_line_of_a_credit()
    {
        // Greece keeps IPT payable on a cancellation: a tax or levy line of a credit has no rule (D-SL3-05, D-SL3-06).
        FinanceSeed.Rules(V3).Where(r => r.EntryType is "CREDIT_WRITTEN" or "CREDIT_BILLED" or "REFUND_APPROVED")
            .Where(r => r.SourceAccount is "LA-06" or "LA-07" or "LA-08" or "LA-26" or "LA-27")
            .ShouldBeEmpty();
    }

    // -------- the tax-treatment check --------------------------------------------------------------------------------

    private static (TaxTreatmentCheck Check, FakeTaxCalculator Calculator) Checker(bool bound = true)
    {
        var calculator = new FakeTaxCalculator();
        var services = new ServiceCollection();
        if (bound)
        {
            services.AddSingleton<ITaxCalculator>(calculator);
        }

        return (new TaxTreatmentCheck(services.BuildServiceProvider()), calculator);
    }

    private static SourceLine Line(string account, string side, decimal amount, string? kind = "CANCELLATION", string? source = "Policyholder",
        string? chargeType = "GR-IPT", string? category = "TAX", string? rule = "GR-TRT-IPT-CANCEL-POLICYHOLDER") =>
        new(account, side, Money.Of(amount, "EUR"), new Dictionary<string, string?>
        {
            [LineDimensionKeys.TransactionKind] = kind,
            [LineDimensionKeys.CancellationSource] = source,
            [LineDimensionKeys.ChargeType] = chargeType,
            [LineDimensionKeys.ChargeCategory] = category,
            [LineDimensionKeys.TreatmentRuleId] = rule,
            [LineDimensionKeys.PolicyTermId] = Term.ToString(),
        });

    private static SourceLine Premium(string account, string side, decimal amount, string? kind = "CANCELLATION") =>
        Line(account, side, amount, kind, kind == "CANCELLATION" ? "Policyholder" : null, "PREM-MTPL", "PREMIUM", null);

    private static SourceEntry Entry(string type, params SourceLine[] lines) =>
        new(Guid.CreateVersion7(), type, new BusinessDate(2026, 11, 2), new BusinessDate(2026, 11, 2), lines);

    private static Task<TaxCheckResult> CheckAsync((TaxTreatmentCheck Check, FakeTaxCalculator Calculator) c, SourceEntry entry) =>
        c.Check.CheckSourceAsync(entry.EntryType, entry, Entity, "GR", TestContext.Current.CancellationToken);

    [Fact]
    public async Task REQ_FIN_182_a_cancellation_credit_with_a_zero_IPT_line_passes_and_the_zero_line_is_not_even_looked_up()
    {
        var c = Checker();
        var result = await CheckAsync(c, Entry("CREDIT_WRITTEN",
            Premium("LA-04", "DEBIT", 288.63m), Premium("LA-01", "CREDIT", 288.63m),
            Line("LA-06", "DEBIT", 0m), Line("LA-01", "CREDIT", 0m, chargeType: null, category: null)));
        result.Passed.ShouldBeTrue(result.Detail);
        c.Calculator.Requests.ShouldBeEmpty("a 0.00 tax line moves nothing: no treatment is needed");
    }

    [Fact]
    public async Task REQ_FIN_182_a_debit_on_the_IPT_payable_against_KEEP_NOT_REDUCED_is_refused()
    {
        var c = Checker();
        var result = await CheckAsync(c, Entry("CREDIT_WRITTEN", Line("LA-06", "DEBIT", 43.29m), Line("LA-01", "CREDIT", 43.29m, chargeType: null, category: null)));
        result.Passed.ShouldBeFalse();
        result.Detail!.ShouldContain("GR-TRT-IPT-CANCEL-POLICYHOLDER");
        result.Detail!.ShouldContain("REQ-FIN-182");

        // The request carries the line's transaction kind, source and charge type, and the IPT category (REQ-MKT-330).
        var request = c.Calculator.Requests[0];
        (request.TransactionKind, request.CancellationSource, request.ChargeType, request.Category, request.ChargeOrigin, request.RiskJurisdiction)
            .ShouldBe((TaxTransactionKind.Cancellation, "Policyholder", "GR-IPT", TaxCategory.Tax, ChargeOrigin.Bil, "GR"));
    }

    [Fact]
    public async Task REQ_FIN_182_a_negative_credit_amount_on_the_IPT_payable_is_a_reduction_too()
    {
        // A negative amount posts on the opposite side (EntryPosting), so CREDIT -43.29 on LA-06 debits GL-2410.
        var result = await CheckAsync(Checker(), Entry("CREDIT_WRITTEN", Line("LA-06", "CREDIT", -43.29m), Line("LA-01", "DEBIT", -43.29m, chargeType: null, category: null)));
        result.Passed.ShouldBeFalse(result.Detail);
    }

    [Fact]
    public async Task REQ_FIN_183_an_endorsement_credit_is_treated_like_a_cancellation_credit_premium_only_passes_an_IPT_debit_is_refused()
    {
        var premiumOnly = Checker();
        (await CheckAsync(premiumOnly, Entry("CREDIT_WRITTEN",
            Premium("LA-04", "DEBIT", 12.00m, "ENDORSEMENT_CREDIT"), Premium("LA-01", "CREDIT", 12.00m, "ENDORSEMENT_CREDIT")))).Passed.ShouldBeTrue();

        var forged = await CheckAsync(Checker(), Entry("CREDIT_WRITTEN",
            Line("LA-06", "DEBIT", 1.80m, "ENDORSEMENT_CREDIT", null, rule: "GR-TRT-IPT-ENDORSEMENT-CREDIT"),
            Line("LA-01", "CREDIT", 1.80m, "ENDORSEMENT_CREDIT", null, null, null)));
        forged.Passed.ShouldBeFalse();
        forged.Detail!.ShouldContain("GR-TRT-IPT-ENDORSEMENT-CREDIT");
    }

    [Fact]
    public async Task REQ_FIN_183_an_endorsement_debit_written_as_new_business_passes_its_IPT_increase()
    {
        var c = Checker();
        var result = await CheckAsync(c, Entry("WRITTEN",
            Line("LA-01", "DEBIT", 6.00m, "ENDORSEMENT_DEBIT", null, null, null), Line("LA-27", "CREDIT", 6.00m, "ENDORSEMENT_DEBIT", null, rule: "GR-TRT-IPT-ENDORSEMENT-DEBIT")));
        result.Passed.ShouldBeTrue(result.Detail);
        c.Calculator.Requests.ShouldAllBe(r => r.TransactionKind == TaxTransactionKind.EndorsementDebit);
        c.Calculator.Requests.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task REQ_FIN_182_a_credit_kept_on_the_payable_where_the_treatment_reduces_it_is_refused()
    {
        // DISTANCE_WITHDRAWAL_VOID is REVERSE_AS_VOID (customerCredit FULL, the authority liability is reduced).
        var kept = await CheckAsync(Checker(), Entry("CREDIT_WRITTEN",
            Line("LA-06", "CREDIT", 43.29m, "DISTANCE_WITHDRAWAL_VOID", "DistanceWithdrawal", rule: "GR-TRT-IPT-WITHDRAWAL-VOID"),
            Line("LA-01", "DEBIT", 43.29m, "DISTANCE_WITHDRAWAL_VOID", null, null, null)));
        kept.Passed.ShouldBeFalse();
        kept.Detail!.ShouldContain("reduces the authority liability");

        var reduced = await CheckAsync(Checker(), Entry("CREDIT_WRITTEN",
            Line("LA-06", "DEBIT", 43.29m, "DISTANCE_WITHDRAWAL_VOID", "DistanceWithdrawal", rule: "GR-TRT-IPT-WITHDRAWAL-VOID"),
            Line("LA-01", "CREDIT", 43.29m, "DISTANCE_WITHDRAWAL_VOID", null, null, null)));
        reduced.Passed.ShouldBeTrue(reduced.Detail);
    }

    [Fact]
    public async Task REQ_FIN_182_RULE_MISSING_fails_closed_for_a_levy_line_and_for_a_source_the_pack_has_no_row_for()
    {
        var levy = await CheckAsync(Checker(), Entry("CREDIT_WRITTEN",
            Line("LA-07", "DEBIT", 3.00m, chargeType: "GR-AUXF", category: "LEVY", rule: null), Line("LA-01", "CREDIT", 3.00m, chargeType: null, category: null)));
        levy.Passed.ShouldBeFalse();
        levy.Detail!.ShouldContain("RULE_MISSING");

        var nonPayment = await CheckAsync(Checker(), Entry("CREDIT_WRITTEN",
            Line("LA-06", "DEBIT", 43.29m, source: "NonPayment"), Line("LA-01", "CREDIT", 43.29m, source: "NonPayment", chargeType: null, category: null)));
        nonPayment.Passed.ShouldBeFalse();
        nonPayment.Detail!.ShouldContain("RULE_MISSING");
    }

    [Theory]
    [InlineData("CREDIT_WRITTEN")]
    [InlineData("CREDIT_BILLED")]
    [InlineData("REFUND_APPROVED")]
    public async Task PITFALLS_10_a_servicing_entry_without_transactionKind_is_refused_even_with_no_tax_line(string entryType)
    {
        var result = await CheckAsync(Checker(), Entry(entryType, Premium("LA-04", "DEBIT", 10m, kind: null), Premium("LA-01", "CREDIT", 10m, kind: null)));
        result.Passed.ShouldBeFalse();
        result.Detail!.ShouldContain("transactionKind");

        var half = await CheckAsync(Checker(), Entry(entryType, Premium("LA-04", "DEBIT", 10m), Premium("LA-01", "CREDIT", 10m, kind: null)));
        half.Passed.ShouldBeFalse("every line of a servicing entry carries the kind");
    }

    [Fact]
    public async Task PITFALLS_10_a_premium_entry_that_reduces_a_tax_payable_must_declare_its_transaction_kind()
    {
        // A reversal of IPT dressed as a plain WRITTEN or BILLED entry cannot dodge the check by omitting the kind.
        foreach (var type in new[] { "WRITTEN", "BILLED" })
        {
            var forged = await CheckAsync(Checker(), Entry(type, Line("LA-06", "DEBIT", 43.29m, kind: null), Line("LA-01", "CREDIT", 43.29m, kind: null, chargeType: null, category: null)));
            forged.Passed.ShouldBeFalse(type);
        }

        // Slice 2 is untouched: an ordinary WRITTEN with an IPT credit and an IPT_DUE move need no treatment.
        (await CheckAsync(Checker(), Entry("WRITTEN", Line("LA-01", "DEBIT", 60m, kind: null, chargeType: null, category: null), Line("LA-27", "CREDIT", 60m, kind: null)))).Passed.ShouldBeTrue();
        (await CheckAsync(Checker(), Entry("IPT_DUE", Line("LA-27", "DEBIT", 60m, kind: null), Line("LA-06", "CREDIT", 60m, kind: null)))).Passed.ShouldBeTrue();
    }

    [Fact]
    public async Task PITFALLS_10_an_unknown_kind_a_missing_charge_type_or_category_and_a_failing_or_missing_calculator_all_fail_closed()
    {
        (await CheckAsync(Checker(), Entry("CREDIT_WRITTEN", Line("LA-06", "CREDIT", 1m, kind: "ENDORSEMENT"), Line("LA-01", "DEBIT", 1m, kind: "ENDORSEMENT", chargeType: null, category: null))))
            .Passed.ShouldBeFalse("ENDORSEMENT is not a transaction kind of the contract");
        (await CheckAsync(Checker(), Entry("CREDIT_WRITTEN", Line("LA-06", "DEBIT", 1m, chargeType: null), Line("LA-01", "CREDIT", 1m, chargeType: null, category: null))))
            .Passed.ShouldBeFalse("no charge type");

        var failing = Checker();
        failing.Calculator.Failure = new TimeoutException("treatment timed out");
        (await CheckAsync(failing, Entry("CREDIT_WRITTEN", Line("LA-06", "DEBIT", 1m), Line("LA-01", "CREDIT", 1m, chargeType: null, category: null))))
            .Passed.ShouldBeFalse("a calculator that fails is not a reason to post");

        var unbound = Checker(bound: false);
        var tax = await CheckAsync(unbound, Entry("CREDIT_WRITTEN", Line("LA-06", "DEBIT", 1m), Line("LA-01", "CREDIT", 1m, chargeType: null, category: null)));
        tax.Passed.ShouldBeFalse();
        tax.Detail!.ShouldContain("not bound");

        // Premium-only credits never need the calculator.
        (await CheckAsync(unbound, Entry("CREDIT_BILLED", Premium("LA-01", "DEBIT", 10m), Premium("LA-02", "CREDIT", 10m)))).Passed.ShouldBeTrue();
    }

    [Fact]
    public async Task D1_P1_IPT_DUE_may_never_debit_the_IPT_payable_with_or_without_a_transaction_kind()
    {
        (await CheckAsync(Checker(), Entry("IPT_DUE", Line("LA-06", "DEBIT", 60m, kind: null), Line("LA-27", "CREDIT", 60m, kind: null)))).Passed.ShouldBeFalse();
        (await CheckAsync(Checker(), Entry("IPT_DUE", Line("LA-06", "DEBIT", 60m, kind: null), Line("LA-10", "CREDIT", 60m, kind: null, chargeType: null, category: null)))).Passed.ShouldBeFalse();
        (await CheckAsync(Checker(), Entry("IPT_DUE", Line("LA-06", "DEBIT", 60m), Line("LA-27", "CREDIT", 60m)))).Passed.ShouldBeFalse("a kind does not make it legitimate");

        // Any other entry type that reduces a payable without a kind is refused too (no entry type is exempt).
        foreach (var type in new[] { "RECEIVED", "ALLOCATED", "COMMISSION", "TAX_REMITTANCE" })
        {
            (await CheckAsync(Checker(), Entry(type, Line("LA-06", "DEBIT", 10m, kind: null), Line("LA-10", "CREDIT", 10m, kind: null, chargeType: null, category: null))))
                .Passed.ShouldBeFalse(type);
        }
    }

    [Fact]
    public async Task D3_P2_the_normal_Greek_DUE_path_of_an_endorsement_debit_Dr_LA_27_Cr_LA_06_is_a_transfer_not_a_reduction()
    {
        // BIL posts IPT_DUE of an endorsement debit with the inherited kind ENDORSEMENT_DEBIT (TermBilling ChargeDimensions).
        var c = Checker();
        var due = await CheckAsync(c, Entry("IPT_DUE",
            Line("LA-27", "DEBIT", 3.00m, "ENDORSEMENT_DEBIT", null, rule: "GR-TRT-IPT-ENDORSEMENT-DEBIT"),
            Line("LA-06", "CREDIT", 3.00m, "ENDORSEMENT_DEBIT", null, rule: "GR-TRT-IPT-ENDORSEMENT-DEBIT")));
        due.Passed.ShouldBeTrue(due.Detail);
        c.Calculator.Requests.ShouldBeEmpty("a transfer inside the payable moves no authority liability");

        // The same shape without a kind (new business, slice 2) and the reverse direction are judged on the net too.
        (await CheckAsync(Checker(), Entry("IPT_DUE", Line("LA-27", "DEBIT", 60m, kind: null), Line("LA-06", "CREDIT", 60m, kind: null)))).Passed.ShouldBeTrue();
    }

    [Fact]
    public async Task D2_P3_the_check_runs_on_the_resolved_account_a_premium_line_with_the_IPT_charge_type_that_derives_GL_2410_is_a_reduction()
    {
        JournalDraft Draft(params JournalLineDraft[] lines) =>
            new("IFRS17", new BusinessDate(2026, 11, 2), new BusinessDate(2026, 11, 2), SourceTypes.Event, Guid.CreateVersion7(), 3, ["R"], lines);
        JournalLineDraft Of(int no, string account, string side, decimal amount, string? chargeType = null) =>
            new(no, account, side, Money.Of(amount, "EUR"), Money.Of(amount, "EUR"), "R",
                new LineDimensions { ChargeType = chargeType, TransactionKind = "CANCELLATION", CancellationSource = "Policyholder" });

        var c = Checker();
        var forged = await c.Check.CheckJournalAsync("CREDIT_WRITTEN", Draft(Of(1, "GL-2410", "DEBIT", 43.29m, "GR-IPT"), Of(2, "GL-1215", "CREDIT", 43.29m)), Entity, "GR", TestContext.Current.CancellationToken);
        forged.Passed.ShouldBeFalse();
        forged.Detail!.ShouldContain("GL-2410");

        var premium = await Checker().Check.CheckJournalAsync("CREDIT_WRITTEN", Draft(Of(1, "GL-2110", "DEBIT", 288.63m, "PREM-MTPL"), Of(2, "GL-1215", "CREDIT", 288.63m)), Entity, "GR", TestContext.Current.CancellationToken);
        premium.Passed.ShouldBeTrue();

        (await Checker().Check.CheckJournalAsync("IPT_DUE", Draft(Of(1, "GL-2410", "DEBIT", 60m, "GR-IPT"), Of(2, "GL-2411", "CREDIT", 60m, "GR-IPT")), Entity, "GR", TestContext.Current.CancellationToken))
            .Passed.ShouldBeFalse("IPT_DUE never debits GL-2410");
        (await Checker().Check.CheckJournalAsync("IPT_DUE", Draft(Of(1, "GL-2411", "DEBIT", 60m, "GR-IPT"), Of(2, "GL-2410", "CREDIT", 60m, "GR-IPT")), Entity, "GR", TestContext.Current.CancellationToken))
            .Passed.ShouldBeTrue("the release of not-yet-due IPT into the payable is a transfer");
    }

    [Fact]
    public async Task D8_a_treatment_that_depends_on_policyholder_type_or_business_basis_fails_closed()
    {
        var c = Checker();
        c.Calculator.DependsOnPolicyholderType = true;
        var result = await CheckAsync(c, Entry("CREDIT_WRITTEN", Line("LA-06", "CREDIT", 5m, "ENDORSEMENT_DEBIT", null), Line("LA-01", "DEBIT", 5m, "ENDORSEMENT_DEBIT", null, null, null)));
        result.Passed.ShouldBeFalse();
        result.Detail!.ShouldContain("policyholder type");

        // The Greece rows ignore both fields, so the defaults are safe there.
        var ok = Checker();
        (await CheckAsync(ok, Entry("CREDIT_WRITTEN", Line("LA-06", "CREDIT", 5m, "ENDORSEMENT_DEBIT", null), Line("LA-01", "DEBIT", 5m, "ENDORSEMENT_DEBIT", null, null, null)))).Passed.ShouldBeTrue();
    }

    // -------- mapping --------------------------------------------------------------------------------------------------

    private static PostingOutcome Map(string entryType, params SourceLine[] lines)
    {
        var accounts = FinanceSeed.ChartCodes(V3).ToDictionary(c => c, c => new ChartAccount(c, c, c, true), StringComparer.Ordinal);
        var book = new BookSetup("IFRS17", Currency.EUR,
            new RuleSet(Guid.CreateVersion7(), "GR-TEST", "IFRS17", 3, new BusinessDate(2026, 10, 1), CoreIns.SharedKernel.Identifiers.Sha256Hash.Parse(FinanceSeed.RuleSetHash(V3)), FinanceSeed.Rules(V3)),
            accounts, FinanceSeed.Derivations(V3));
        var context = new PolicyContext(Guid.CreateVersion7(), "POL000000001", Term, Guid.CreateVersion7(), "MOTOR-GR", "1.1", new string('a', 64), true);
        return EntryPosting.Map(Source, new SourceEntry(Guid.CreateVersion7(), entryType, new BusinessDate(2026, 11, 2), new BusinessDate(2026, 11, 2), lines), book,
            _ => context, (_, chargeType) => chargeType == "PREM-MTPL" ? "PREM-MOTOR-MTPL" : null);
    }

    [Fact]
    public void REQ_FIN_036_GF04_a_cancellation_credit_with_a_zero_IPT_line_maps_to_Dr_GL_2110_Cr_GL_1215_and_leaves_GL_2410_alone()
    {
        var outcome = Map("CREDIT_WRITTEN",
            Premium("LA-04", "DEBIT", 288.63m), Premium("LA-01", "CREDIT", 288.63m),
            Line("LA-06", "DEBIT", 0m), Line("LA-01", "CREDIT", 0m, chargeType: null, category: null));
        outcome.Journal.ShouldNotBeNull(outcome.Detail);
        outcome.Journal!.Lines.Select(l => $"{l.Account} {l.Side} {l.Amount.Amount}").ShouldBe(["GL-2110 DEBIT 288.63", "GL-1215 CREDIT 288.63"]);
        outcome.Journal.Lines.ShouldNotContain(l => l.Account == "GL-2410");

        var billed = Map("CREDIT_BILLED", Premium("LA-01", "DEBIT", 288.63m), Premium("LA-02", "CREDIT", 288.63m));
        billed.Journal!.Lines.Select(l => $"{l.Account} {l.Side}").ShouldBe(["GL-1215 DEBIT", "GL-1210 CREDIT"]);
    }

    [Fact]
    public void REQ_FIN_001_an_unknown_servicing_entry_type_or_a_tax_line_on_a_credit_is_NO_RULE_never_a_default_account()
    {
        Map("CREDIT_SETTLED", Premium("LA-04", "DEBIT", 1m), Premium("LA-01", "CREDIT", 1m)).Reason.ShouldBe(ExceptionReasons.NoRule);

        // Consistent with a REDUCE treatment the check passes, but v3 has no tax rule for a credit: fail closed.
        Map("CREDIT_WRITTEN", Line("LA-06", "DEBIT", 43.29m), Line("LA-01", "CREDIT", 43.29m, chargeType: null, category: null)).Reason.ShouldBe(ExceptionReasons.NoRule);

        // D4: BIL posts a fee credit as Dr LA-05 / Cr LA-01 (BLR-CREDIT-WRITTEN-FEE): CW-FEE mirrors WR-FEE, so the rule matches and,
        // as for a written fee, no fee GL key is derived yet: an intake exception NO_CHARGE_TYPE, not NO_RULE.
        Map("CREDIT_WRITTEN", Line("LA-05", "DEBIT", 5m, chargeType: "FEE-X", category: "FEE"), Line("LA-01", "CREDIT", 5m, chargeType: null, category: null)).Reason.ShouldBe(ExceptionReasons.NoChargeType);
    }

    [Fact]
    public void REQ_FIN_297_refund_approval_and_release_map_the_credit_balance_through_refunds_payable_to_disbursements_in_transit()
    {
        Map("REFUND_APPROVED", Line("LA-02", "DEBIT", 288.63m, "REFUND", null, null, null), Line("LA-12", "CREDIT", 288.63m, "REFUND", null, null, null))
            .Journal!.Lines.Select(l => $"{l.Account} {l.Side}").ShouldBe(["GL-1210 DEBIT", "GL-2535 CREDIT"]);
        Map("DISBURSEMENT_RELEASED", Line("LA-12", "DEBIT", 288.63m, null, null, null, null), Line("LA-13", "CREDIT", 288.63m, null, null, null, null))
            .Journal!.Lines.Select(l => $"{l.Account} {l.Side}").ShouldBe(["GL-2535 DEBIT", "GL-2530 CREDIT"]);
        Map("DISBURSEMENT_CLEARED", Line("LA-13", "DEBIT", 288.63m, null, null, null, null), Line("LA-10", "CREDIT", 288.63m, null, null, null, null))
            .Journal!.Lines.Select(l => $"{l.Account} {l.Side}").ShouldBe(["GL-2530 DEBIT", "GL-1110 CREDIT"]);
    }
}
