using CoreIns.CountryPacks.GR.Configuration;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Market.Domain;
using CoreIns.Modules.Market.Queries;
using CoreIns.Modules.Market.Services;
using CoreIns.Modules.Rating.Contracts.Servicing;
using CoreIns.Modules.Rating.Services;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CoreIns.IntegrationTests.Rating.Proration;

/// <summary>
/// SL3-RAT-PRORATE: <c>IRatingServicingTax</c> against the real MKT <c>TaxCalculator.treatment</c> over the Greece pack rows
/// (SL3-MKT-TREATMENT). MKT does not bind <c>calculate</c> yet, so the calculator under test is the real treatment plus a test-only
/// <c>calculate</c> that applies the IPT rate the real MKT configuration resolves (<c>tax.ipt.rate.general</c>). No database.
/// </summary>
public sealed class ServicingTaxRealMarketTests
{
    private static readonly BusinessDate TaxPoint = new(2027, 1, 15);
    private static readonly Guid EntityId = Guid.Parse("0192f0c4-0000-7000-8000-000000000001");

    [Fact]
    public async Task D_SL3_05_a_debit_of_70_00_gets_IPT_10_50_with_APPLY_and_the_real_rule_ids()
    {
        var line = (await Service().LinesAsync(Request(Delta("d", 70.00m, ServicingTransactionKind.EndorsementDebit)))).Lines.Single();

        line.TreatmentAction.ShouldBe(ServicingTreatmentAction.Apply);
        line.Amount.Amount.ShouldBe(10.50m);
        line.Rate.ShouldBe(0.15m);
        line.TreatmentRuleId.ShouldBe("GR-TRT-IPT-ENDORSEMENT-DEBIT");
        line.TreatmentRuleVersion.ShouldNotBeNullOrEmpty();
        line.CalculationRuleId.ShouldNotBeNullOrEmpty();
        line.LegalSourceRef.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task D_SL3_05_a_policyholder_credit_of_288_63_keeps_the_IPT_with_a_0_00_line_that_is_pending_opinion_and_provisional()
    {
        var line = (await Service().LinesAsync(Request(Delta("c", -288.63m, ServicingTransactionKind.Cancellation, "Policyholder")))).Lines.Single();

        line.TreatmentAction.ShouldBe(ServicingTreatmentAction.KeepNotReduced);
        line.Amount.Amount.ShouldBe(0m);
        line.Base.Amount.ShouldBe(-288.63m);
        line.CustomerCredit.ShouldBe(ServicingCustomerCredit.None);
        line.AuthorityLiability.ShouldBe(ServicingAuthorityLiability.NotReduce);
        line.CalculationRuleId.ShouldBeNull();
        line.TreatmentRuleId.ShouldStartWith("GR-TRT-IPT-");
        line.LegalStatus.ShouldBe("PendingOpinion");
        line.Provisional.ShouldBeTrue();
    }

    [Fact]
    public async Task A_levy_has_no_treatment_rule_so_the_whole_request_fails()
    {
        var request = Request(
            Delta("d", 70.00m, ServicingTransactionKind.EndorsementDebit),
            Delta("levy", 70.00m, ServicingTransactionKind.EndorsementDebit, category: ServicingTaxCategory.Levy));

        var ex = await Should.ThrowAsync<DomainException>(() => Service().LinesAsync(request));

        ex.Error.Code.Value.ShouldBe("RAT-ERR-TAX");
        ex.Error.Detail!.ShouldContain("RuleMissing");
    }

    [Fact]
    public async Task Production_refuses_the_pending_credit_treatment()
    {
        var ex = await Should.ThrowAsync<DomainException>(() => Service("Production").LinesAsync(
            Request(Delta("c", -288.63m, ServicingTransactionKind.Cancellation, "Policyholder"))));

        ex.Error.Code.Value.ShouldBe("MKT-ERR-CFG-NOT-SETTLED"); // MKT's own Production refusal passes through unchanged
    }

    // ---- fixtures -------------------------------------------------------------------------------------------------------------

    private static ServicingTaxLinesRequest Request(params ServicingDelta[] deltas) => new("GR", null, TaxPoint, "ENDORSEMENT", "MOTOR", deltas);

    private static ServicingDelta Delta(
        string id, decimal amount, ServicingTransactionKind kind, string? source = null, ServicingTaxCategory category = ServicingTaxCategory.Tax) =>
        new(id, "MTPL", "PREM-MTPL", "IPT", category, "general", new Money(amount, Currency.EUR), TaxPoint, TaxPoint.AddDays(100), kind, source);

    private static RatingServicingTax Service(string environment = "Development")
    {
        var entity = new LegalEntityInfo(new LegalEntityId(EntityId), LegalEntityCode.Parse("GR-TEST"), "GR", "gr", "EUR", "Europe/Athens", "ACTIVE", true);
        var clock = new ManualClock(Instant.FromUtc(2026, 10, 8));
        var catalogue = new ConfigurationCatalogue([new GrPackConfiguration()], clock.Now);
        var engine = new ConfigurationEngine(catalogue, new LegalEntityRegistry([entity]), new Env(environment), clock);
        return new RatingServicingTax(
            new RealTreatmentWithConfiguredRate(new MarketTaxCalculator(engine), engine),
            new RequestContext { LegalEntity = LegalEntityCode.Parse("GR-TEST") },
            new Directory(),
            new Env(environment));
    }

    /// <summary>The real MKT treatment, and a test-only calculate at the IPT rate the real configuration resolves.</summary>
    private sealed class RealTreatmentWithConfiguredRate(MarketTaxCalculator real, ConfigurationEngine engine) : ITaxCalculator
    {
        public ValueTask<TaxTreatmentResult> TreatmentAsync(TaxTreatmentRequest request, CancellationToken cancellationToken = default) =>
            real.TreatmentAsync(request, cancellationToken);

        public ValueTask<TaxCalculationResult> CalculateAsync(TaxCalculationRequest request, CancellationToken cancellationToken = default)
        {
            var resolved = engine.Resolve(
                new ConfigurationResolveRequest
                {
                    LegalEntity = "GR-TEST",
                    Jurisdiction = "GR",
                    Keys = ["tax.ipt.rate.general"],
                    TimeBasisDates = new Dictionary<string, BusinessDate> { [TimeBases.TaxPointDate] = new BusinessDate(request.TaxPointDate) },
                },
                ValidAt.From(new BusinessDate(request.TaxPointDate)),
                null).Values.Single();
            var rate = decimal.Parse(resolved.Value.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            var line = request.ChargeLines.Single();
            return ValueTask.FromResult(new TaxCalculationResult(
                [
                    new TaxLine
                    {
                        Element = line.Element,
                        ChargeType = "IPT",
                        Category = TaxCategory.Tax,
                        TaxClass = line.TaxClass,
                        Base = line.PremiumAmount,
                        Rate = rate,
                        Amount = new SpiMoney(decimal.Round(line.PremiumAmount.Amount * rate, 2, MidpointRounding.AwayFromZero), line.PremiumAmount.Currency),
                        RoundingRuleId = "cur.rounding.tax.line",
                        RuleId = "tax.ipt.rate.general",
                        RuleVersion = resolved.ValueVersionId.ToString(),
                        LegalSourceRef = resolved.LegalSourceRef ?? string.Empty,
                        LegalStatus = Enum.Parse<CoreIns.Modules.Market.Contracts.Spi.LegalStatus>(resolved.LegalStatus.ToString()),
                    },
                ],
                []));
        }
    }

    private sealed class Directory : ILegalEntityDirectory
    {
        public LegalEntityId Resolve(LegalEntityCode code) => new(EntityId);

        public IReadOnlyList<LegalEntityId> All => [];
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = ".";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
