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

    private static TaxChargeLine Line(string element, decimal amount, string taxClass = "general", string chargeType = "PREM-MTPL", string transactionType = "ENDORSEMENT") => new()
    {
        Element = element,
        ChargeType = chargeType,
        ProductLine = "MOTOR",
        TaxClass = taxClass,
        PremiumAmount = new SpiMoney(amount, "EUR"),
        PeriodStart = TaxPoint,
        PeriodEnd = TaxPoint.AddDays(100),
        TransactionType = transactionType,
    };

    private static TaxCalculationRequest Request(params TaxChargeLine[] lines) => new()
    {
        LegalEntityId = Entity.Id.Value,
        RiskJurisdiction = "GR",
        TaxPointDate = TaxPoint,
        PolicyholderType = PolicyholderType.Consumer,
        BusinessBasis = "ESTABLISHMENT",
        ChargeLines = lines,
    };

    private static TaxCalculationRequest Single(decimal amount, string taxClass = "general", string chargeType = "PREM-MTPL", string transactionType = "ENDORSEMENT") =>
        Request(Line("MTPL", amount, taxClass, chargeType, transactionType));

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
        var (calculator, engine) = Build("Development");
        var line = (await calculator.CalculateAsync(Single(70.00m), TestContext.Current.CancellationToken)).Lines.Single();

        line.RuleId.ShouldBe("tax.ipt.rate.general");
        line.RuleVersion.ShouldNotBeNullOrEmpty();
        line.LegalStatus.ShouldBe(LegalStatus.Settled);
        line.Provisional.ShouldBeFalse();
        line.LegalSourceRef.ShouldNotBeNullOrEmpty();
        line.RoundingRuleId.ShouldNotBeNullOrEmpty();
        line.ConfigurationHash.ShouldBe(engine.Catalogue.Hash.Hash.ToString());
        line.ConfigurationHash!.Length.ShouldBe(64);
    }

    [Theory]
    [InlineData("levy", "AUXFUND")]
    [InlineData("levy.auxfund", "AUXFUND-LEVY")]
    [InlineData("stamp", "STAMP-DUTY")]
    [InlineData("general", "STAMP")]
    public async Task A_levy_or_stamp_has_no_rows_so_it_is_RULE_MISSING(string taxClass, string chargeType)
    {
        var ex = await Should.ThrowAsync<SpiException>(async () =>
            await Build("Development").Calculator.CalculateAsync(Single(70.00m, taxClass, chargeType), TestContext.Current.CancellationToken));

        ex.Category.ShouldBe(SpiErrorCategory.RuleMissing);
        ex.Error.Code.ShouldBe("RULE_MISSING");
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
        var pending = new RowPack(new PackConfigValue("tax.ipt.rate.general", ConfigValueType.ExactDecimal, "0.15", LegalStatus.PendingOpinion, "test pending row", true));

        var ex = await Should.ThrowAsync<DomainException>(async () =>
            await Build("Production", pending).Calculator.CalculateAsync(Single(70.00m), TestContext.Current.CancellationToken));
        ex.Error.Code.Value.ShouldBe("MKT-ERR-CFG-NOT-SETTLED");

        var line = (await Build("Development", pending).Calculator.CalculateAsync(Single(70.00m), TestContext.Current.CancellationToken)).Lines.Single();
        line.Provisional.ShouldBeTrue();
        line.LegalStatus.ShouldBe(LegalStatus.PendingOpinion);
    }

    [Fact]
    public async Task Production_serves_the_settled_gr_rate()
    {
        var line = (await Build("Production").Calculator.CalculateAsync(Single(70.00m), TestContext.Current.CancellationToken)).Lines.Single();

        line.Amount.Amount.ShouldBe(10.50m);
        line.Provisional.ShouldBeFalse();
    }

    [Fact]
    public async Task A_credit_base_is_refused_unless_the_transaction_is_reducing_and_then_the_tax_is_a_credit()
    {
        var (calculator, _) = Build("Development");

        var refused = await Should.ThrowAsync<SpiException>(async () =>
            await calculator.CalculateAsync(Single(-70.00m), TestContext.Current.CancellationToken));
        refused.Category.ShouldBe(SpiErrorCategory.Validation);

        var credit = (await calculator.CalculateAsync(Single(-70.00m, transactionType: "VOID"), TestContext.Current.CancellationToken)).Lines.Single();
        credit.Amount.Amount.ShouldBe(-10.50m);
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

    private sealed class RowPack(PackConfigValue value) : IPackConfigurationSource
    {
        public string PackId => "test";

        public string PackVersion => "0.0.1";

        public string Country => "GR";

        public IReadOnlyList<PackConfigValue> Values { get; } = [value];
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = ".";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
