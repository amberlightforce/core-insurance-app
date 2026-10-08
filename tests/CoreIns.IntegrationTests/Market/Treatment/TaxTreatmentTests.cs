using CoreIns.CountryPacks.CY;
using CoreIns.CountryPacks.GR.Configuration;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Market.Domain;
using CoreIns.Modules.Market.Queries;
using CoreIns.Modules.Market.Services;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CoreIns.IntegrationTests.Market.Treatment;

/// <summary>
/// SL3-MKT-TREATMENT: <c>TaxCalculator.treatment</c> over the GR rows (REQ-MKT-330/331/332, D-SL3-05/06, D-SLC-09) and the
/// CY synthetic stub, the pack-load guard TCK-TAX-NET-REFUND and the cancellation-source code list (REQ-POL-205).
/// </summary>
public sealed class TaxTreatmentTests
{
    private static readonly DateOnly TaxPoint = new(2027, 1, 15);

    private static readonly LegalEntityInfo Entity = new(
        new LegalEntityId(Guid.Parse("0192f0c4-0000-7000-8000-000000000001")), LegalEntityCode.Parse("GR-TEST"), "GR", "gr", "EUR", "Europe/Athens", "ACTIVE", true);

    private static (MarketTaxCalculator Calculator, ConfigurationEngine Engine) Build(string environment, params IPackConfigurationSource[] sources)
    {
        var clock = new ManualClock(Instant.FromUtc(2026, 10, 8));
        var catalogue = new ConfigurationCatalogue(sources.Length == 0 ? [new GrPackConfiguration()] : sources, clock.Now);
        var engine = new ConfigurationEngine(catalogue, new LegalEntityRegistry([Entity]), new FakeEnvironment(environment), clock);
        return (new MarketTaxCalculator(engine), engine);
    }

    private static string Json(string action) => $$"""{"action":"{{action}}","ruleId":"TEST","ruleVersion":"1"}""";

    private static TaxTreatmentRequest Request(
        TaxTransactionKind kind, TaxCategory category = TaxCategory.Tax, string? source = null, string jurisdiction = "GR") => new()
    {
        LegalEntityId = Entity.Id.Value,
        RiskJurisdiction = jurisdiction,
        TaxPointDate = TaxPoint,
        ChargeType = "IPT",
        Category = category,
        ChargeOrigin = ChargeOrigin.Pol,
        TransactionKind = kind,
        CancellationSource = source,
        PolicyholderType = PolicyholderType.Consumer,
        BusinessBasis = "ESTABLISHMENT",
    };

    [Theory]
    [InlineData(TaxTransactionKind.EndorsementCredit)]
    [InlineData(TaxTransactionKind.ReturnPremium)]
    [InlineData(TaxTransactionKind.Refund)]
    public async Task REQ_MKT_331_ipt_on_credits_is_kept_not_reduced_provisional_with_rule_id_and_version(TaxTransactionKind kind)
    {
        var result = await Build("Development").Calculator.TreatmentAsync(Request(kind), TestContext.Current.CancellationToken);

        result.Action.ShouldBe(TreatmentAction.KeepNotReduced);
        result.CustomerCredit.ShouldBe(CustomerCredit.None);
        result.AuthorityLiability.ShouldBe(AuthorityLiability.NotReduce);
        result.FiscalDocument.ShouldBe(FiscalDocumentTreatment.None);
        result.LegalStatus.ShouldBe(TreatmentLegalStatus.Pending);
        result.Provisional.ShouldBeTrue();
        result.RuleId.ShouldStartWith("GR-TRT-IPT-");
        result.RuleVersion.ShouldNotBeNullOrEmpty();
        result.LegalSourceRef.ShouldContain("REQ-MKT-331");
        result.LegalSourceRef.ShouldContain("ΠΟΛ 1028/2017");
        result.ChargeType.ShouldBe("IPT");
    }

    [Fact]
    public async Task REQ_MKT_331_cancellation_by_the_policyholder_keeps_the_ipt()
    {
        var result = await Build("Development").Calculator.TreatmentAsync(Request(TaxTransactionKind.Cancellation, source: "Policyholder"), TestContext.Current.CancellationToken);

        result.Action.ShouldBe(TreatmentAction.KeepNotReduced);
        result.RuleId.ShouldBe("GR-TRT-IPT-CANCEL-POLICYHOLDER");
        result.Provisional.ShouldBeTrue();
    }

    [Theory]
    [InlineData(TaxTransactionKind.NewBusiness)]
    [InlineData(TaxTransactionKind.EndorsementDebit)]
    [InlineData(TaxTransactionKind.Fee)]
    public async Task REQ_MKT_331_ipt_on_debits_and_fees_applies(TaxTransactionKind kind)
    {
        var result = await Build("Development").Calculator.TreatmentAsync(Request(kind), TestContext.Current.CancellationToken);

        result.Action.ShouldBe(TreatmentAction.Apply);
        result.Provisional.ShouldBeTrue();
    }

    [Fact]
    public async Task REQ_MKT_331_distance_withdrawal_void_reverses_as_void_with_full_credit_and_a_credit_note()
    {
        var result = await Build("Development").Calculator.TreatmentAsync(Request(TaxTransactionKind.DistanceWithdrawalVoid), TestContext.Current.CancellationToken);

        result.Action.ShouldBe(TreatmentAction.ReverseAsVoid);
        result.CustomerCredit.ShouldBe(CustomerCredit.Full);
        result.AuthorityLiability.ShouldBe(AuthorityLiability.Reduce);
        result.FiscalDocument.ShouldBe(FiscalDocumentTreatment.CreditNote);
        result.LegalSourceRef.ShouldContain("Law 5317/2026 Art. 72");
    }

    [Fact]
    public void REQ_MKT_331_the_withdrawal_void_routing_key_is_registered_and_unused()
    {
        ConfigKeys.Find(TaxTreatmentRules.WithdrawalVoidRouting).ShouldNotBeNull();
        Build("Development").Engine.Catalogue.Entries.ShouldNotContain(e => e.Key == TaxTreatmentRules.WithdrawalVoidRouting);
    }

    [Theory]
    [InlineData(TaxCategory.Levy)]
    [InlineData(TaxCategory.Stamp)]
    public async Task D_SL3_06_levy_and_stamp_lines_have_no_rule_and_fail_closed(TaxCategory category)
    {
        var calculator = Build("Development").Calculator;

        foreach (var kind in new[] { TaxTransactionKind.EndorsementCredit, TaxTransactionKind.NewBusiness, TaxTransactionKind.ReturnPremium })
        {
            var error = await Should.ThrowAsync<SpiException>(async () => await calculator.TreatmentAsync(Request(kind, category), TestContext.Current.CancellationToken));
            error.Category.ShouldBe(SpiErrorCategory.RuleMissing);
            error.Error.Code.ShouldBe("RULE_MISSING");
        }

        var cancel = await Should.ThrowAsync<SpiException>(async () =>
            await calculator.TreatmentAsync(Request(TaxTransactionKind.Cancellation, category, "Policyholder"), TestContext.Current.CancellationToken));
        cancel.Category.ShouldBe(SpiErrorCategory.RuleMissing);
    }

    [Theory]
    [InlineData("Insurer")]
    [InlineData("NonPayment")]
    [InlineData("DistanceWithdrawal")]
    [InlineData("LongTermWithdrawal")]
    [InlineData("Objection")]
    [InlineData("Statutory")]
    public async Task REQ_MKT_331_cancellation_sources_other_than_policyholder_have_no_rule(string source)
    {
        var error = await Should.ThrowAsync<SpiException>(async () =>
            await Build("Development").Calculator.TreatmentAsync(Request(TaxTransactionKind.Cancellation, source: source), TestContext.Current.CancellationToken));

        error.Category.ShouldBe(SpiErrorCategory.RuleMissing);
    }

    [Theory]
    [InlineData(TaxTransactionKind.Reinstatement)]
    [InlineData(TaxTransactionKind.Void)]
    public async Task Kinds_without_a_row_fail_closed(TaxTransactionKind kind)
    {
        var error = await Should.ThrowAsync<SpiException>(async () =>
            await Build("Development").Calculator.TreatmentAsync(Request(kind, source: "Policyholder"), TestContext.Current.CancellationToken));

        error.Category.ShouldBe(SpiErrorCategory.RuleMissing);
    }

    [Fact]
    public async Task No_rule_for_a_jurisdiction_without_a_pack_row_and_no_core_default()
    {
        var error = await Should.ThrowAsync<SpiException>(async () =>
            await Build("Development").Calculator.TreatmentAsync(Request(TaxTransactionKind.Refund, jurisdiction: "DE"), TestContext.Current.CancellationToken));

        error.Category.ShouldBe(SpiErrorCategory.RuleMissing);
    }

    [Theory]
    [InlineData(TaxTransactionKind.Cancellation)]
    [InlineData(TaxTransactionKind.Void)]
    public async Task REQ_MKT_332_a_missing_cancellation_source_on_cancellation_or_void_is_a_validation_error(TaxTransactionKind kind)
    {
        var error = await Should.ThrowAsync<SpiException>(async () =>
            await Build("Development").Calculator.TreatmentAsync(Request(kind), TestContext.Current.CancellationToken));

        error.Category.ShouldBe(SpiErrorCategory.Validation);
        error.Error.Code.ShouldBe("CANCELLATION_SOURCE_REQUIRED");
    }

    [Fact]
    public async Task An_unknown_source_or_transaction_kind_is_a_validation_error()
    {
        var calculator = Build("Development").Calculator;

        var source = await Should.ThrowAsync<SpiException>(async () =>
            await calculator.TreatmentAsync(Request(TaxTransactionKind.Cancellation, source: "Bogus"), TestContext.Current.CancellationToken));
        var kind = await Should.ThrowAsync<SpiException>(async () =>
            await calculator.TreatmentAsync(Request((TaxTransactionKind)99), TestContext.Current.CancellationToken));

        source.Category.ShouldBe(SpiErrorCategory.Validation);
        kind.Category.ShouldBe(SpiErrorCategory.Validation);
    }

    [Fact]
    public async Task D_SLC_09_production_refuses_every_pending_opinion_row_and_staging_serves_it_provisional()
    {
        var production = Build("Production").Calculator;
        var staging = Build("Staging").Calculator;

        foreach (var request in new[]
                 {
                     Request(TaxTransactionKind.Cancellation, source: "Policyholder"),
                     Request(TaxTransactionKind.NewBusiness),
                     Request(TaxTransactionKind.DistanceWithdrawalVoid),
                 })
        {
            var refused = await Should.ThrowAsync<DomainException>(async () => await production.TreatmentAsync(request, TestContext.Current.CancellationToken));
            refused.Error.Code.ToString().ShouldBe("MKT-ERR-CFG-NOT-SETTLED");
            (await staging.TreatmentAsync(request, TestContext.Current.CancellationToken)).Provisional.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_settled_row_is_served_in_production_and_is_not_provisional()
    {
        var calculator = Build("Production", new SettledRow()).Calculator;

        var result = await calculator.TreatmentAsync(Request(TaxTransactionKind.Refund), TestContext.Current.CancellationToken);

        result.LegalStatus.ShouldBe(TreatmentLegalStatus.Settled);
        result.Provisional.ShouldBeFalse();
    }

    [Fact]
    public async Task CY_stub_reduces_every_credit_pro_rata_in_every_category_and_is_synthetic()
    {
        var (calculator, engine) = Build("Development", new CyTreatmentRules());

        foreach (var category in Enum.GetValues<TaxCategory>())
        {
            foreach (var kind in new[] { TaxTransactionKind.EndorsementCredit, TaxTransactionKind.ReturnPremium, TaxTransactionKind.Refund, TaxTransactionKind.DistanceWithdrawalVoid })
            {
                var result = await calculator.TreatmentAsync(Request(kind, category, jurisdiction: "CY"), TestContext.Current.CancellationToken);
                result.Action.ShouldBe(TreatmentAction.ReduceProRata);
                result.CustomerCredit.ShouldBe(CustomerCredit.ProRata);
                result.Provisional.ShouldBeTrue();
                result.LegalSourceRef.ShouldContain("SYNTHETIC");
            }

            foreach (var source in TaxTreatmentRules.CancellationSources)
            {
                (await calculator.TreatmentAsync(Request(TaxTransactionKind.Cancellation, category, source, "CY"), TestContext.Current.CancellationToken))
                    .Action.ShouldBe(TreatmentAction.ReduceProRata);
            }
        }

        engine.Catalogue.Entries.Where(e => e.Key.StartsWith(TaxTreatmentRules.KeyPrefix, StringComparison.Ordinal)).ShouldAllBe(e => e.SourceRef.Contains("SYNTHETIC"));
        (await Should.ThrowAsync<SpiException>(async () =>
            await calculator.TreatmentAsync(Request(TaxTransactionKind.Refund, jurisdiction: "GR"), TestContext.Current.CancellationToken))).Category.ShouldBe(SpiErrorCategory.RuleMissing);
    }

    [Fact]
    public async Task CY_stub_is_refused_in_production()
    {
        var calculator = Build("Production", new CyTreatmentRules()).Calculator;

        await Should.ThrowAsync<DomainException>(async () =>
            await calculator.TreatmentAsync(Request(TaxTransactionKind.Refund, jurisdiction: "CY"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("tax.treatment.rule.TAX.DISTANCE_WITHDRAWAL_VOID.ANY", "KEEP_NOT_REDUCED")]
    [InlineData("tax.treatment.rule.TAX.CANCELLATION.DistanceWithdrawal", "KEEP_NOT_REDUCED")]
    [InlineData("tax.treatment.rule.LEVY.VOID.DistanceWithdrawal", "KEEP_NOT_REDUCED")]
    public void TCK_TAX_NET_REFUND_a_pack_that_gives_no_customer_credit_on_a_distance_withdrawal_is_rejected_at_load(string key, string action)
    {
        var pack = new RowPack(new PackConfigValue(key, ConfigValueType.Json, Json(action), LegalStatus.PendingOpinion, "test", false));

        var error = Should.Throw<InvalidOperationException>(() => new ConfigurationCatalogue([pack], Instant.FromUtc(2026, 10, 8)));

        error.Message.ShouldContain("TCK-TAX-NET-REFUND");
    }

    [Fact]
    public void TCK_TAX_NET_REFUND_a_withdrawal_row_with_credit_loads()
    {
        foreach (var action in new[] { "REVERSE_AS_VOID", "INSURER_BEARS", "REDUCE_PRO_RATA" })
        {
            var pack = new RowPack(new PackConfigValue("tax.treatment.rule.TAX.DISTANCE_WITHDRAWAL_VOID.ANY", ConfigValueType.Json, Json(action), LegalStatus.PendingOpinion, "test", false));
            Should.NotThrow(() => new ConfigurationCatalogue([pack], Instant.FromUtc(2026, 10, 8)));
        }
    }

    [Theory]
    [InlineData("tax.treatment.rule.TAX.BOGUS.ANY")]
    [InlineData("tax.treatment.rule.TAX.CANCELLATION.Nobody")]
    [InlineData("tax.treatment.rule.TAX.CANCELLATION")]
    [InlineData("tax.treatment.rule.DUTY.CANCELLATION.ANY")]
    public void Malformed_treatment_keys_are_rejected_at_load(string key)
    {
        var pack = new RowPack(new PackConfigValue(key, ConfigValueType.Json, Json("APPLY"), LegalStatus.PendingOpinion, "test", false));

        Should.Throw<InvalidOperationException>(() => new ConfigurationCatalogue([pack], Instant.FromUtc(2026, 10, 8)));
    }

    [Fact]
    public void REQ_POL_205_the_cancellation_source_code_list_resolves_through_the_configuration_engine()
    {
        var response = Build("Production").Engine.Resolve(
            new ConfigurationResolveRequest { LegalEntity = "GR-TEST", Jurisdiction = "GR", Keys = [TaxTreatmentRules.CancellationSourceCodeListKey] },
            ValidAt.From(new BusinessDate(2027, 1, 15)), null);

        var item = response.Values.Single();
        item.Value.EnumerateArray().Select(e => e.GetString()).ShouldBe(
            ["Policyholder", "Insurer", "NonPayment", "DistanceWithdrawal", "LongTermWithdrawal", "Objection", "Statutory"]);
        item.Provisional.ShouldBeFalse();
    }

    [Fact]
    public void GR_rows_exist_for_the_ipt_category_only_and_every_row_is_pending_opinion_with_a_source()
    {
        var rows = Build("Development").Engine.Catalogue.Entries.Where(e => e.Key.StartsWith(TaxTreatmentRules.KeyPrefix, StringComparison.Ordinal)).ToList();

        rows.Count.ShouldBe(8);
        rows.ShouldAllBe(e => e.LegalStatus == LegalStatus.PendingOpinion && e.Key.StartsWith("tax.treatment.rule.TAX.", StringComparison.Ordinal) && e.SourceRef.Contains("REQ-MKT-331"));
    }

    private sealed class RowPack(PackConfigValue value) : IPackConfigurationSource
    {
        public string PackId => "test";

        public string PackVersion => "0.0.1";

        public string Country => "GR";

        public IReadOnlyList<PackConfigValue> Values { get; } = [value];
    }

    private sealed class SettledRow : IPackConfigurationSource
    {
        public string PackId => "test";

        public string PackVersion => "0.0.1";

        public string Country => "GR";

        public IReadOnlyList<PackConfigValue> Values { get; } =
        [
            new("tax.treatment.rule.TAX.REFUND.ANY", ConfigValueType.Json, Json("KEEP_NOT_REDUCED"), LegalStatus.Settled, "test settled row", false),
        ];
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = ".";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
