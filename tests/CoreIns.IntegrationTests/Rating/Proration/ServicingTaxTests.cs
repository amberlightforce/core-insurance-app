using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Rating.Contracts.Servicing;
using CoreIns.Modules.Rating.Services;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CoreIns.IntegrationTests.Rating.Proration;

/// <summary>
/// SL3-RAT-PRORATE: tax lines for servicing deltas through <c>TaxCalculator.treatment</c> (D-SL3-05, REQ-RAT-009 subset, REQ-POL-124),
/// against a scripted calculator that stands in for the Greece pack until SL3-MKT-TREATMENT merges. The rule ids, rates and statuses
/// below are test fixtures, not pack data. No database.
/// </summary>
public sealed class ServicingTaxTests
{
    private static readonly BusinessDate Taxpoint = new(2026, 7, 1);
    private static readonly Currency Eur = Currency.EUR;

    [Fact]
    public async Task REQ_POL_124_a_debit_gets_IPT_at_the_rate_with_APPLY_and_the_ids_of_both_rules()
    {
        var calculator = new ScriptedCalculator();
        var result = await Service(calculator).LinesAsync(Request(Delta("d1", 70.00m, ServicingTransactionKind.EndorsementDebit)));

        var line = result.Lines.Single();
        line.TreatmentAction.ShouldBe(ServicingTreatmentAction.Apply);
        line.Amount.Amount.ShouldBe(10.50m); // 15% of 70.00
        line.Rate.ShouldBe(0.15m);
        line.Base.Amount.ShouldBe(70.00m);
        line.CalculationRuleId.ShouldBe("calc-ipt-general");
        line.CalculationRuleVersion.ShouldBe("1");
        line.TreatmentRuleId.ShouldBe("treat-apply");
        line.TreatmentRuleVersion.ShouldBe("1");
        line.LegalStatus.ShouldBe("Settled");
        line.Provisional.ShouldBeFalse();
        calculator.CalculateCalls.ShouldBe(1);
        calculator.TreatmentRequests.Single().TransactionKind.ShouldBe(TaxTransactionKind.EndorsementDebit);
    }

    [Fact]
    public async Task D_SL3_05_a_credit_of_288_63_gets_an_IPT_line_of_0_00_that_keeps_the_treatment_and_is_provisional()
    {
        var calculator = new ScriptedCalculator();
        var result = await Service(calculator).LinesAsync(Request(
            Delta("c1", -288.63m, ServicingTransactionKind.Cancellation, source: "Policyholder")));

        var line = result.Lines.Single();
        line.TreatmentAction.ShouldBe(ServicingTreatmentAction.KeepNotReduced);
        line.Amount.Amount.ShouldBe(0m);
        line.Amount.Currency.ShouldBe(Eur);
        line.Base.Amount.ShouldBe(-288.63m);
        line.Rate.ShouldBeNull();
        line.CalculationRuleId.ShouldBeNull(); // no calculation ran
        line.TreatmentRuleId.ShouldBe("treat-keep");
        line.TreatmentRuleVersion.ShouldBe("2");
        line.LegalStatus.ShouldBe("PendingOpinion");
        line.Provisional.ShouldBeTrue();
        line.LegalSourceRef.ShouldBe("POL 1028/2017 (fixture)");
        calculator.CalculateCalls.ShouldBe(0);
        calculator.TreatmentRequests.Single().CancellationSource.ShouldBe("Policyholder");
        calculator.TreatmentRequests.Single().ChargeOrigin.ShouldBe(ChargeOrigin.Pol);
    }

    [Fact]
    public async Task A_levy_with_no_rule_fails_the_whole_request_and_nothing_is_returned()
    {
        var calculator = new ScriptedCalculator();
        var request = Request(
            Delta("d1", 70.00m, ServicingTransactionKind.EndorsementDebit),
            Delta("levy", 70.00m, ServicingTransactionKind.EndorsementDebit, taxCharge: "GR-AUXF", category: ServicingTaxCategory.Levy));

        var ex = await Should.ThrowAsync<DomainException>(() => Service(calculator).LinesAsync(request));

        ex.Error.Code.Value.ShouldBe("RAT-ERR-TAX");
        ex.Error.Detail!.ShouldContain("RuleMissing");
        calculator.CalculateCalls.ShouldBe(1); // the first delta was computed, but its line is not returned
    }

    [Fact]
    public async Task The_weakest_legal_status_of_calculation_and_treatment_wins_both_ways()
    {
        var pendingTreatment = new ScriptedCalculator { ApplyTreatmentStatus = TreatmentLegalStatus.Pending };
        var line = (await Service(pendingTreatment).LinesAsync(Request(Delta("d", 10m, ServicingTransactionKind.EndorsementDebit)))).Lines.Single();
        line.LegalStatus.ShouldBe("PendingOpinion");
        line.Provisional.ShouldBeTrue();

        var weakRate = new ScriptedCalculator { RateStatus = CoreIns.Modules.Market.Contracts.Spi.LegalStatus.Unverified };
        line = (await Service(weakRate).LinesAsync(Request(Delta("d", 10m, ServicingTransactionKind.EndorsementDebit)))).Lines.Single();
        line.LegalStatus.ShouldBe("Unverified");
        line.Provisional.ShouldBeTrue();

        // NotRegulatory is not a statutory tax value: it is never reported as Settled and is provisional (refused in Production).
        var notRegulatory = new ScriptedCalculator { RateStatus = CoreIns.Modules.Market.Contracts.Spi.LegalStatus.NotRegulatory };
        line = (await Service(notRegulatory).LinesAsync(Request(Delta("d", 10m, ServicingTransactionKind.EndorsementDebit)))).Lines.Single();
        line.LegalStatus.ShouldBe("NotRegulatory");
        line.Provisional.ShouldBeTrue();
        var ex = await Should.ThrowAsync<DomainException>(() => Service(notRegulatory, "Production").LinesAsync(Request(Delta("d", 10m, ServicingTransactionKind.EndorsementDebit))));
        ex.Error.Code.Value.ShouldBe("RAT-ERR-TAX");

        var draft = new ScriptedCalculator { RateStatus = CoreIns.Modules.Market.Contracts.Spi.LegalStatus.Draft };
        line = (await Service(draft).LinesAsync(Request(Delta("d", 10m, ServicingTransactionKind.EndorsementDebit)))).Lines.Single();
        line.LegalStatus.ShouldBe("Draft");
        line.Provisional.ShouldBeTrue();
    }

    [Fact]
    public async Task The_treatment_detail_travels_on_the_line_for_the_POL_adapter()
    {
        var keep = (await Service(new ScriptedCalculator()).LinesAsync(Request(Delta("c", -50m, ServicingTransactionKind.EndorsementCredit)))).Lines.Single();
        keep.CustomerCredit.ShouldBe(ServicingCustomerCredit.None);
        keep.AuthorityLiability.ShouldBe(ServicingAuthorityLiability.NotReduce);
        keep.FiscalDocument.ShouldBe(ServicingFiscalDocument.None);

        var apply = (await Service(new ScriptedCalculator()).LinesAsync(Request(Delta("d", 50m, ServicingTransactionKind.EndorsementDebit)))).Lines.Single();
        apply.CustomerCredit.ShouldBe(ServicingCustomerCredit.ProRata);
        apply.AuthorityLiability.ShouldBe(ServicingAuthorityLiability.Reduce);
    }

    [Fact]
    public async Task A_tax_larger_than_the_delta_or_zero_under_APPLY_is_refused()
    {
        var big = new ScriptedCalculator { TaxFactor = 1.5m };
        (await Should.ThrowAsync<DomainException>(() => Service(big).LinesAsync(Request(Delta("d", 70m, ServicingTransactionKind.EndorsementDebit)))))
            .Error.Code.Value.ShouldBe("RAT-ERR-TAX");
        var zero = new ScriptedCalculator { TaxFactor = 0m };
        (await Should.ThrowAsync<DomainException>(() => Service(zero).LinesAsync(Request(Delta("d", 70m, ServicingTransactionKind.EndorsementDebit)))))
            .Error.Code.Value.ShouldBe("RAT-ERR-TAX");
    }

    [Fact]
    public async Task A_provisional_line_is_refused_in_Production_and_allowed_elsewhere()
    {
        var credit = Request(Delta("c1", -288.63m, ServicingTransactionKind.Cancellation, source: "Policyholder"));

        var ex = await Should.ThrowAsync<DomainException>(() => Service(new ScriptedCalculator(), "Production").LinesAsync(credit));
        ex.Error.Code.Value.ShouldBe("RAT-ERR-TAX");

        // A Settled debit is fine in Production.
        var debit = await Service(new ScriptedCalculator(), "Production").LinesAsync(Request(Delta("d", 70m, ServicingTransactionKind.EndorsementDebit)));
        debit.Lines.Single().Provisional.ShouldBeFalse();
    }

    [Fact]
    public async Task Fail_closed_cases_no_calculator_no_tax_class_missing_source_wrong_sign_unexpected_answer()
    {
        // no TaxCalculator bound
        var unbound = new RatingServicingTax(new ServiceCollection().BuildServiceProvider(), Context(), new Directory(), new Env("Development"));
        (await Should.ThrowAsync<DomainException>(() => unbound.LinesAsync(Request(Delta("d", 1m, ServicingTransactionKind.EndorsementDebit)))))
            .Error.Code.Value.ShouldBe("RAT-ERR-TAX");

        var service = Service(new ScriptedCalculator());
        (await Should.ThrowAsync<DomainException>(() => service.LinesAsync(Request(Delta("d", 1m, ServicingTransactionKind.EndorsementDebit, taxClass: "")))))
            .Error.Code.Value.ShouldBe("RAT-ERR-TAX");
        (await Should.ThrowAsync<DomainException>(() => service.LinesAsync(Request(Delta("d", -1m, ServicingTransactionKind.Cancellation)))))
            .Error.Code.Value.ShouldBe("RAT-ERR-INPUT");
        (await Should.ThrowAsync<DomainException>(() => service.LinesAsync(Request())))
            .Error.Code.Value.ShouldBe("RAT-ERR-INPUT");

        // a calculator that returns a tax of the wrong sign is refused
        var wrongSign = Service(new ScriptedCalculator { SignFlip = true });
        (await Should.ThrowAsync<DomainException>(() => wrongSign.LinesAsync(Request(Delta("d", 70m, ServicingTransactionKind.EndorsementDebit)))))
            .Error.Code.Value.ShouldBe("RAT-ERR-TAX");

        // a calculator that throws something unexpected fails closed too
        var broken = Service(new ScriptedCalculator { Boom = true });
        (await Should.ThrowAsync<DomainException>(() => broken.LinesAsync(Request(Delta("d", 70m, ServicingTransactionKind.EndorsementDebit)))))
            .Error.Code.Value.ShouldBe("RAT-ERR-TAX");
    }

    [Fact]
    public async Task A_reduce_pro_rata_treatment_taxes_the_credit_and_the_line_is_negative()
    {
        var calculator = new ScriptedCalculator { CreditAction = TreatmentAction.ReduceProRata, CreditStatus = TreatmentLegalStatus.Settled };
        var line = (await Service(calculator).LinesAsync(Request(Delta("c", -100.00m, ServicingTransactionKind.EndorsementCredit)))).Lines.Single();

        line.TreatmentAction.ShouldBe(ServicingTreatmentAction.ReduceProRata);
        line.Amount.Amount.ShouldBe(-15.00m);
        line.Provisional.ShouldBeFalse();
    }

    // ---- fixtures -------------------------------------------------------------------------------------------------------------

    private static ServicingTaxLinesRequest Request(params ServicingDelta[] deltas) => new("GR", null, Taxpoint, "ENDORSEMENT", "MOTOR", deltas);

    private static ServicingDelta Delta(
        string id, decimal amount, ServicingTransactionKind kind, string? source = null, string taxCharge = "GR-IPT",
        ServicingTaxCategory category = ServicingTaxCategory.Tax, string? taxClass = null) =>
        new(id, "MTPL", "PREM-MTPL", taxCharge, category, taxClass ?? "general", new Money(amount, Eur), Taxpoint, Taxpoint.AddDays(100), kind, source);

    private static RequestContext Context() => new() { LegalEntity = LegalEntityCode.Parse("GR-TEST") };

    private static RatingServicingTax Service(ScriptedCalculator calculator, string environment = "Development") =>
        new(new ServiceCollection().AddSingleton<ITaxCalculator>(calculator).BuildServiceProvider(), Context(), new Directory(), new Env(environment));

    private sealed class Directory : ILegalEntityDirectory
    {
        public LegalEntityId Resolve(LegalEntityCode code) => new(Guid.Parse("0192f0c4-0000-7000-8000-000000000001"));

        public IReadOnlyList<LegalEntityId> All => [];
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = ".";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>
    /// A scripted TaxCalculator. Treatment: debits APPLY (Settled); credits KEEP_NOT_REDUCED (Pending) unless <see cref="CreditAction"/>
    /// says otherwise; a Levy has no rule (RULE_MISSING). Calculate: 15% of the base.
    /// </summary>
    private sealed class ScriptedCalculator : ITaxCalculator
    {
        public int CalculateCalls { get; private set; }

        public List<TaxTreatmentRequest> TreatmentRequests { get; } = [];

        public TreatmentLegalStatus ApplyTreatmentStatus { get; init; } = TreatmentLegalStatus.Settled;

        public CoreIns.Modules.Market.Contracts.Spi.LegalStatus RateStatus { get; init; } = CoreIns.Modules.Market.Contracts.Spi.LegalStatus.Settled;

        public TreatmentAction CreditAction { get; init; } = TreatmentAction.KeepNotReduced;

        public TreatmentLegalStatus CreditStatus { get; init; } = TreatmentLegalStatus.Pending;

        public bool SignFlip { get; init; }

        public decimal TaxFactor { get; init; } = 0.15m;

        public bool Boom { get; init; }

        public ValueTask<TaxTreatmentResult> TreatmentAsync(TaxTreatmentRequest request, CancellationToken cancellationToken = default)
        {
            TreatmentRequests.Add(request);
            if (request.Category == TaxCategory.Levy)
            {
                throw new SpiException(new SpiError(SpiErrorCategory.RuleMissing, "NO_TREATMENT_RULE"), "no rule");
            }

            var debit = request.TransactionKind is TaxTransactionKind.EndorsementDebit or TaxTransactionKind.NewBusiness;
            var action = debit ? TreatmentAction.Apply : CreditAction;
            return ValueTask.FromResult(new TaxTreatmentResult
            {
                ChargeType = request.ChargeType,
                Action = action,
                CustomerCredit = action == TreatmentAction.KeepNotReduced ? CustomerCredit.None : CustomerCredit.ProRata,
                AuthorityLiability = action == TreatmentAction.KeepNotReduced ? AuthorityLiability.NotReduce : AuthorityLiability.Reduce,
                FiscalDocument = FiscalDocumentTreatment.None,
                RuleId = debit ? "treat-apply" : "treat-keep",
                RuleVersion = debit ? "1" : "2",
                LegalStatus = debit ? ApplyTreatmentStatus : CreditStatus,
                LegalSourceRef = debit ? "Law 5177/2025 (fixture)" : "POL 1028/2017 (fixture)",
            });
        }

        public ValueTask<TaxCalculationResult> CalculateAsync(TaxCalculationRequest request, CancellationToken cancellationToken = default)
        {
            if (Boom)
            {
                throw new InvalidOperationException("boom");
            }

            CalculateCalls++;
            var line = request.ChargeLines.Single();
            var amount = decimal.Round(line.PremiumAmount.Amount * TaxFactor, 2, MidpointRounding.AwayFromZero) * (SignFlip ? -1m : 1m);
            return ValueTask.FromResult(new TaxCalculationResult(
                [
                    new TaxLine
                    {
                        Element = line.Element,
                        ChargeType = "GR-IPT",
                        Category = TaxCategory.Tax,
                        TaxClass = line.TaxClass,
                        Base = line.PremiumAmount,
                        Rate = 0.15m,
                        Amount = new SpiMoney(amount, line.PremiumAmount.Currency),
                        RoundingRuleId = "round-2",
                        RuleId = "calc-ipt-general",
                        RuleVersion = "1",
                        LegalSourceRef = "Law 5177/2025 (fixture)",
                        LegalStatus = RateStatus,
                    },
                ],
                []));
        }
    }
}

internal static class ServicingTaxTestExtensions
{
    public static Task<ServicingTaxLinesResult> LinesAsync(this IRatingServicingTax service, ServicingTaxLinesRequest request) =>
        service.TaxLinesAsync(request, TestContext.Current.CancellationToken);
}
