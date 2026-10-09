using CoreIns.CountryPacks.GR.Configuration;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Market.Domain;
using CoreIns.Modules.Market.Queries;
using CoreIns.Modules.Market.Services;
using CoreIns.Modules.Rating.Domain;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CoreIns.IntegrationTests.Market.Treatment;

/// <summary>
/// SL3-MKT-CALCULATE: <c>TaxCalculator.calculate</c> over the GR <c>tax.ipt.rate.*</c> rows (REQ-MKT-332, PRD-17 7.5, PITFALLS 36).
/// The amounts are asserted against the quote-time rule of the rating tax plan (<see cref="RatingEngine.TaxAmount"/>).
/// </summary>
public sealed class TaxCalculateTests
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

    private static TaxChargeLine Line(string element, decimal amount, string taxClass = "general", string chargeType = "PREM-MTPL", ChargeLineCategory? category = ChargeLineCategory.Premium, string currency = "EUR") => new()
    {
        Element = element,
        ChargeCategory = category,
        ChargeType = chargeType,
        ProductLine = "MOTOR",
        TaxClass = taxClass,
        PremiumAmount = new SpiMoney(amount, currency),
        PeriodStart = TaxPoint,
        PeriodEnd = TaxPoint.AddDays(100),
        TransactionType = "ENDORSEMENT",
    };

    private static TaxCalculationRequest Request(params TaxChargeLine[] lines) => RequestWith(null, lines);

    private static TaxCalculationRequest RequestWith(TreatmentAction? action, params TaxChargeLine[] lines) => new()
    {
        TreatmentAction = action,
        LegalEntityId = Entity.Id.Value,
        RiskJurisdiction = "GR",
        TaxPointDate = TaxPoint,
        PolicyholderType = PolicyholderType.Consumer,
        BusinessBasis = "ESTABLISHMENT",
        ChargeLines = lines,
    };

    private static TaxCalculationRequest Single(
        decimal amount, string taxClass = "general", string chargeType = "PREM-MTPL", TreatmentAction? action = null,
        ChargeLineCategory? category = ChargeLineCategory.Premium, string currency = "EUR") =>
        RequestWith(action, Line("MTPL", amount, taxClass, chargeType, category, currency));

    /// <summary>The GR rate (Settled) with a Settled tax-line rounding rule and the EUR currency role: the only combination Production serves.</summary>
    private static IPackConfigurationSource[] SettledPack(string rateStatus = "Settled") =>
    [
        new RowPack(
            new PackConfigValue("tax.ipt.rate.general", ConfigValueType.ExactDecimal, "0.15", Enum.Parse<LegalStatus>(rateStatus), "test rate", true),
            new PackConfigValue("cur.rounding.tax.line", ConfigValueType.Json, """{"mode":"HALF_UP","scale":null,"level":"LINE"}""", LegalStatus.Settled, "test rounding", true),
            new PackConfigValue("cur.transaction", ConfigValueType.Text, "EUR", LegalStatus.NotRegulatory, "test currency", false)),
    ];

    /// <summary>The quote-time computation: the GR-IPT tax plan of the rating artefact applied to one coverage premium.</summary>
    private static decimal QuoteTime(decimal premium, decimal rate)
    {
        var plan = BuiltInArtefacts.Definition("MOTOR-GR", "1", new Dictionary<string, string>()).TaxPlan.Single();
        return RatingEngine.TaxAmount(new Money(premium, Currency.EUR), rate, plan).Amount;
    }

    [Fact]
    public async Task REQ_MKT_332_a_debit_of_70_00_gets_IPT_10_50_equal_to_the_quote_time_computation()
    {
        var result = await Build("Development").Calculator.CalculateAsync(Single(70.00m), TestContext.Current.CancellationToken);

        var line = result.Lines.Single();
        line.Amount.Amount.ShouldBe(10.50m);
        line.Amount.Amount.ShouldBe(QuoteTime(70.00m, 0.15m));
        line.Rate.ShouldBe(0.15m);
        line.ChargeType.ShouldBe("IPT");
        line.Category.ShouldBe(TaxCategory.Tax);
        line.Element.ShouldBe("MTPL");
        result.DocumentLines.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_quote_coverage_lines_of_430_00_total_IPT_64_51_with_per_line_rounding_like_the_quote()
    {
        // RatingApiTests: MTPL 121.50 + OWN-DAMAGE 283.50 + WINDSCREEN 25.00 = 430.00, IPT 18.23 + 42.53 + 3.75 = 64.51.
        var result = await Build("Development").Calculator.CalculateAsync(
            Request(Line("MTPL", 121.50m), Line("OWN-DAMAGE", 283.50m), Line("WINDSCREEN", 25.00m)), TestContext.Current.CancellationToken);

        result.Lines.Select(l => l.Amount.Amount).ShouldBe([18.23m, 42.53m, 3.75m]);
        result.Lines.Select(l => l.Amount.Amount).ShouldBe([QuoteTime(121.50m, 0.15m), QuoteTime(283.50m, 0.15m), QuoteTime(25.00m, 0.15m)]);
        result.Lines.Sum(l => l.Amount.Amount).ShouldBe(64.51m);
    }

    [Fact]
    public async Task Rounding_is_per_line_so_one_430_00_line_is_64_50_not_the_quote_total()
    {
        var line = (await Build("Development").Calculator.CalculateAsync(Single(430.00m), TestContext.Current.CancellationToken)).Lines.Single();

        line.Amount.Amount.ShouldBe(64.50m);
        line.Amount.Amount.ShouldBe(QuoteTime(430.00m, 0.15m));
    }

    [Fact]
    public async Task The_line_carries_rule_id_version_status_provisional_source_and_the_configuration_hash()
    {
        var (calculator, engine) = Build("Production", SettledPack());
        var line = (await calculator.CalculateAsync(Single(70.00m), TestContext.Current.CancellationToken)).Lines.Single();

        line.RuleId.ShouldBe("tax.ipt.rate.general");
        line.RuleVersion.ShouldNotBeNullOrEmpty();
        line.LegalStatus.ShouldBe(LegalStatus.Settled);
        line.Provisional.ShouldBeFalse();
        line.LegalSourceRef.ShouldContain("cur.rounding.tax.line");
        line.RoundingRuleId.ShouldNotBeNullOrEmpty();
        line.ConfigurationHash.ShouldBe(engine.Catalogue.Hash.Hash.ToString());
        line.ConfigurationHash!.Length.ShouldBe(64);
    }

    [Fact]
    public async Task D2_the_gr_pack_rounding_row_is_Unverified_so_the_line_is_provisional_and_Production_refuses_it()
    {
        var line = (await Build("Development").Calculator.CalculateAsync(Single(70.00m), TestContext.Current.CancellationToken)).Lines.Single();
        line.LegalStatus.ShouldBe(LegalStatus.Unverified); // the weaker of the Settled rate and the Unverified cur.rounding.tax.line
        line.Provisional.ShouldBeTrue();
        line.LegalSourceRef.ShouldContain("cur.rounding.tax.line");

        var ex = await Should.ThrowAsync<DomainException>(async () =>
            await Build("Production").Calculator.CalculateAsync(Single(70.00m), TestContext.Current.CancellationToken));
        ex.Error.Code.Value.ShouldBe("MKT-ERR-CFG-NOT-SETTLED");
        ex.Error.Detail!.ShouldContain("cur.rounding.tax.line");
    }

    [Fact]
    public async Task D2_a_per_class_rounding_override_wins_over_the_tax_line_rule()
    {
        var rows = new RowPack(
            new PackConfigValue("tax.ipt.rate.general", ConfigValueType.ExactDecimal, "0.15", LegalStatus.Settled, "test rate", true),
            new PackConfigValue("cur.rounding.tax.general", ConfigValueType.Json, """{"mode":"DOWN","scale":null,"level":"LINE"}""", LegalStatus.Settled, "test override", true),
            new PackConfigValue("cur.transaction", ConfigValueType.Text, "EUR", LegalStatus.NotRegulatory, "test currency", false));

        var line = (await Build("Production", rows).Calculator.CalculateAsync(Single(121.50m), TestContext.Current.CancellationToken)).Lines.Single();

        line.Amount.Amount.ShouldBe(18.22m); // 18.225 rounded DOWN, not half-up 18.23
        line.LegalSourceRef.ShouldContain("cur.rounding.tax.general");
    }

    [Theory]
    [InlineData(ChargeLineCategory.Levy)]
    [InlineData(ChargeLineCategory.Stamp)]
    [InlineData(ChargeLineCategory.Fee)]
    [InlineData(ChargeLineCategory.Tax)]
    public async Task D3_only_a_premium_charge_is_taxed_every_other_category_is_RULE_MISSING(ChargeLineCategory category)
    {
        var ex = await Should.ThrowAsync<SpiException>(async () =>
            await Build("Development").Calculator.CalculateAsync(Single(70.00m, category: category), TestContext.Current.CancellationToken));

        ex.Category.ShouldBe(SpiErrorCategory.RuleMissing);
        ex.Error.Code.ShouldBe("RULE_MISSING");
    }

    [Fact]
    public async Task D3_a_missing_charge_category_is_a_validation_error_never_guessed_from_the_name()
    {
        var ex = await Should.ThrowAsync<SpiException>(async () =>
            await Build("Development").Calculator.CalculateAsync(Single(70.00m, chargeType: "AUXFUND-LEVY", category: null), TestContext.Current.CancellationToken));

        ex.Category.ShouldBe(SpiErrorCategory.Validation);
    }

    [Fact]
    public async Task A_premium_charge_whose_name_says_levy_is_still_a_premium_by_category()
    {
        var line = (await Build("Development").Calculator.CalculateAsync(Single(70.00m, chargeType: "PREM-LEVY-FREE"), TestContext.Current.CancellationToken)).Lines.Single();

        line.Amount.Amount.ShouldBe(10.50m);
    }

    [Fact]
    public async Task An_unknown_tax_class_is_RULE_MISSING()
    {
        var ex = await Should.ThrowAsync<SpiException>(async () =>
            await Build("Development").Calculator.CalculateAsync(Single(70.00m, "nuclear"), TestContext.Current.CancellationToken));

        ex.Category.ShouldBe(SpiErrorCategory.RuleMissing);
    }

    [Fact]
    public async Task Production_refuses_a_pending_rate_row_and_development_serves_it_as_provisional()
    {
        var ex = await Should.ThrowAsync<DomainException>(async () =>
            await Build("Production", SettledPack("PendingOpinion")).Calculator.CalculateAsync(Single(70.00m), TestContext.Current.CancellationToken));
        ex.Error.Code.Value.ShouldBe("MKT-ERR-CFG-NOT-SETTLED");

        var line = (await Build("Development", SettledPack("PendingOpinion")).Calculator.CalculateAsync(Single(70.00m), TestContext.Current.CancellationToken)).Lines.Single();
        line.Provisional.ShouldBeTrue();
        line.LegalStatus.ShouldBe(LegalStatus.PendingOpinion);
    }

    [Fact]
    public async Task Production_serves_a_fully_settled_rate_and_rounding()
    {
        var line = (await Build("Production", SettledPack()).Calculator.CalculateAsync(Single(70.00m), TestContext.Current.CancellationToken)).Lines.Single();

        line.Amount.Amount.ShouldBe(10.50m);
        line.Provisional.ShouldBeFalse();
    }

    [Theory]
    [InlineData(TreatmentAction.ReduceProRata)]
    [InlineData(TreatmentAction.ReverseAsVoid)]
    public async Task D1_a_credit_base_gets_a_credit_tax_only_under_a_reducing_treatment_action(TreatmentAction action)
    {
        var credit = (await Build("Development").Calculator.CalculateAsync(Single(-70.00m, action: action), TestContext.Current.CancellationToken)).Lines.Single();

        credit.Amount.Amount.ShouldBe(-10.50m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(TreatmentAction.Apply)]
    public async Task D1_a_credit_base_under_apply_or_no_action_is_refused(TreatmentAction? action)
    {
        var ex = await Should.ThrowAsync<SpiException>(async () =>
            await Build("Development").Calculator.CalculateAsync(Single(-70.00m, action: action), TestContext.Current.CancellationToken));

        ex.Category.ShouldBe(SpiErrorCategory.Validation);
        ex.Error.Code.ShouldBe("CREDIT_BASE_NOT_REDUCING");
    }

    [Theory]
    [InlineData(TreatmentAction.KeepNotReduced)]
    [InlineData(TreatmentAction.InsurerBears)]
    public async Task D1_actions_that_calculate_does_not_price_are_refused(TreatmentAction action)
    {
        var ex = await Should.ThrowAsync<SpiException>(async () =>
            await Build("Development").Calculator.CalculateAsync(Single(70.00m, action: action), TestContext.Current.CancellationToken));

        ex.Category.ShouldBe(SpiErrorCategory.Validation);
    }

    [Fact]
    public async Task D1_a_debit_base_under_a_reducing_treatment_is_refused()
    {
        var ex = await Should.ThrowAsync<SpiException>(async () =>
            await Build("Development").Calculator.CalculateAsync(Single(70.00m, action: TreatmentAction.ReduceProRata), TestContext.Current.CancellationToken));

        ex.Category.ShouldBe(SpiErrorCategory.Validation);
    }

    [Fact]
    public async Task D4_an_amount_the_exact_multiply_cannot_represent_is_refused_not_rounded()
    {
        var ex = await Should.ThrowAsync<SpiException>(async () =>
            await Build("Development").Calculator.CalculateAsync(Single(1.0000000000000000000000000001m), TestContext.Current.CancellationToken));

        ex.Category.ShouldBe(SpiErrorCategory.Validation);
        ex.Error.Code.ShouldBe("AMOUNT_NOT_REPRESENTABLE");
    }

    [Fact]
    public async Task D5_a_default_tax_point_date_is_refused()
    {
        var ex = await Should.ThrowAsync<SpiException>(async () =>
            await Build("Development").Calculator.CalculateAsync(Single(70.00m) with { TaxPointDate = default }, TestContext.Current.CancellationToken));

        ex.Error.Code.ShouldBe("TAX_POINT_DATE_REQUIRED");
    }

    [Fact]
    public async Task D5_a_currency_other_than_the_jurisdictions_is_refused()
    {
        var ex = await Should.ThrowAsync<SpiException>(async () =>
            await Build("Development").Calculator.CalculateAsync(Single(70.00m, currency: "USD"), TestContext.Current.CancellationToken));

        ex.Category.ShouldBe(SpiErrorCategory.Validation);
        ex.Error.Code.ShouldBe("CURRENCY_MISMATCH");
    }

    [Fact]
    public async Task Missing_lines_and_missing_tax_class_are_validation_errors()
    {
        var calculator = Build("Development").Calculator;
        (await Should.ThrowAsync<SpiException>(async () => await calculator.CalculateAsync(Request(), TestContext.Current.CancellationToken)))
            .Category.ShouldBe(SpiErrorCategory.Validation);
        (await Should.ThrowAsync<SpiException>(async () => await calculator.CalculateAsync(Single(70.00m, taxClass: " "), TestContext.Current.CancellationToken)))
            .Category.ShouldBe(SpiErrorCategory.Validation);
    }

    private sealed class RowPack(params PackConfigValue[] values) : IPackConfigurationSource
    {
        public string PackId => "test";

        public string PackVersion => "0.0.1";

        public string Country => "GR";

        public IReadOnlyList<PackConfigValue> Values { get; } = values;
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = ".";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
