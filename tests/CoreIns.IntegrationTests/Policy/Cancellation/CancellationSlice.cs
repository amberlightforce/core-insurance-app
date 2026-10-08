using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Product;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Rating.Contracts;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Modules.Underwriting.Contracts;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using CoreIns.Testing.Contracts.Fakes.Rating;
using CoreIns.Testing.Contracts.Fakes.Underwriting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Cancellation;

/// <summary>
/// The POL host for the cancellation tests: the real Host and database, the real PTY, PFC (seeded Motor Private Car) and MKT
/// rounding; RAT and UW are scripted doubles (one MTPL premium and one IPT line, ILLUSTRATIVE TEST DATA), the clock is a
/// <see cref="ManualClock"/> so a test can bind at day 0 and cancel at day 120, and <c>TaxCalculator.treatment</c> is
/// <see cref="FakeTaxCalculator"/> until SL3-MKT-TREATMENT merges (it answers like the GR rows: Policyholder cancellation of
/// IPT is KEEP_NOT_REDUCED, legal status PendingOpinion).
/// </summary>
internal sealed class CancellationSlice : IAsyncDisposable
{
    public const string RatingArtefactHash = "2222222222222222222222222222222222222222222222222222222222222222";
    public const string WorksheetId = "4444444444444444444444444444444444444444444444444444444444444444";

    /// <summary>Day 0: 12:00 Athens (09:00 UTC) on 2026-10-08, so that +N×24 h stays on the same local hour across the October DST change.</summary>
    public static readonly Instant Day0 = Instant.FromUtc(2026, 10, 8, 9, 0, 0);

    private readonly ApiHostFactory _root;
    private readonly decimal _premium;
    private readonly decimal _ipt;
    private Sha256Hash? _configurationHash;

    public CancellationSlice(string connectionString, decimal premium = 430.00m, decimal ipt = 64.50m, IReadOnlyDictionary<string, string?>? settings = null)
    {
        _premium = premium;
        _ipt = ipt;
        var all = new Dictionary<string, string?>(settings ?? new Dictionary<string, string?>()) { ["Policy:AllowMissingDraftValidation"] = "true" };
        _root = new ApiHostFactory(connectionString, settings: all);
        Product = "MOTOR-GR-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        Factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IRatingRateService>(Rating);
            services.AddSingleton<IUnderwritingRulesService>(Underwriting);
            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<ITaxCalculator>(Tax);
            services.AddSingleton<CoreIns.Modules.Policy.Domain.Servicing.IProration, CoreIns.Modules.Policy.Domain.Servicing.ReferenceProration>();
        }));
        Client = Factory.CreateClient();
        Rating.Setup("rat.Rate.rate", call => Rate((RateRateRequest)call.Arguments[0]!));
        Underwriting.Setup("uw.Rules.evaluate", new RulesEvaluateResponse { EvaluationId = Guid.CreateVersion7(), Issues = [], Outcome = RulesEvaluateResponse.OutcomeValue.Accept });
    }

    public WebApplicationFactory<Program> Factory { get; }

    public HttpClient Client { get; }

    public string Product { get; }

    public ManualClock Clock { get; } = new(Day0);

    public FakeTaxCalculator Tax { get; } = new();

    public FakeRatingRateService Rating { get; } = new();

    public FakeUnderwritingRulesService Underwriting { get; } = new();

    public async Task SeedAsync()
    {
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            _configurationHash = (await scope.ServiceProvider.GetRequiredService<IMarketConfigurationService>().CurrentHashAsync()).Hash;
        }

        var (response, body) = await ProductApi.ImportAsync(Client, ProductApi.Seed(Product));
        response.IsSuccessStatusCode.ShouldBeTrue(body?.ToJsonString());
    }

    private RateRateResponse Rate(RateRateRequest request)
    {
        var vehicle = request.Segments[0].RiskTree.GetProperty("vehicle").GetProperty("elementId").GetString()!;
        var segment = request.Segments[0].SegmentId;
        var eur = Currency.FromCode("EUR");
        return new RateRateResponse
        {
            Rates =
            [
                new RateRateResponse.RateItem
                {
                    SegmentId = segment, ElementId = vehicle, ChargeType = "PREM-MTPL", ChargeCategory = "PREMIUM", CoverageCode = "MTPL",
                    AnnualRate = _premium, Currency = eur, Handling = RateRateResponse.RateItem.HandlingValue.Proratable,
                },
            ],
            Taxes =
            [
                new RateRateResponse.TaxeItem
                {
                    SegmentId = segment, CoverageCode = "MTPL", ChargeType = "GR-IPT", Category = RateRateResponse.TaxeItem.CategoryValue.Tax, ChargeCategory = "TAX",
                    LegalStatus = "PendingOpinion", Provisional = true, Base = new Money(_premium, eur), Rate = 0.15m, Amount = new Money(_ipt, eur),
                    ConfigurationKey = "test.tax.rate",
                },
            ],
            RatingArtefactHash = Sha256Hash.Parse(RatingArtefactHash),
            ConfigurationHash = _configurationHash is { } hash ? new ConfigurationHash(hash) : null,
            Bindable = request.Envelope.Mode == RateRateRequest.EnvelopeDetail.ModeValue.Full,
            WorksheetId = Sha256Hash.Parse(WorksheetId),
            WorksheetHash = Sha256Hash.Parse(WorksheetId),
        };
    }

    /// <summary>Submission → draft → quote → bind at the clock's current time (or later); returns the ids.</summary>
    public async Task<BoundPolicy> BindAsync(TimeSpan? effectiveIn = null)
    {
        var (created, party) = await SendAsync(Client, HttpMethod.Post, "/api/pty/v1/parties", Person("Μαρία", "Παπαδοπούλου", null));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, party?.ToJsonString());
        var partyId = party.Text("party.partyId");
        var effective = Clock.Now.Plus(effectiveIn ?? TimeSpan.Zero);
        var (submitted, submission) = await SendAsync(Client, HttpMethod.Post, "/api/pol/v1/submissions", new
        {
            policyholderPartyId = partyId, product = Product, channel = "STAFF", effectiveAt = effective.ToDateTimeOffset().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            quoteType = "FULL",
        });
        submitted.StatusCode.ShouldBe(HttpStatusCode.Created, submission?.ToJsonString());
        var jobId = submission.Text("jobId");
        var policyId = submission.Text("policyId");
        var (first, draft) = await SendAsync(Client, HttpMethod.Post, "/api/pol/v1/jobs/update-draft",
            new { jobId, versionNo = 1, expectedDraftVersion = 0, instructions = PolicySlice.MotorRisk() });
        first.StatusCode.ShouldBe(HttpStatusCode.OK, draft?.ToJsonString());
        var vehicle = draft.Text("riskTree.vehicles.0.locator");
        var (second, filled) = await SendAsync(Client, HttpMethod.Post, "/api/pol/v1/jobs/update-draft",
            new { jobId, versionNo = 1, expectedDraftVersion = 1, instructions = PolicySlice.DriverAndCovers(vehicle, partyId) });
        second.StatusCode.ShouldBe(HttpStatusCode.OK, filled?.ToJsonString());
        var (quoted, quote) = await SendAsync(Client, HttpMethod.Post, "/api/pol/v1/jobs/quote", new { jobId, versionNo = 1 });
        quoted.StatusCode.ShouldBe(HttpStatusCode.OK, quote?.ToJsonString());
        var (bound, bind) = await SendAsync(Client, HttpMethod.Post, "/api/pol/v1/jobs/bind", new { jobId, versionNo = 1, paymentPlanOption = "ANNUAL", confirmation = true });
        bound.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        return new BoundPolicy(policyId, bind.Text("termId"), bind.Text("transactionId"), bind.Text("termState"));
    }

    /// <summary>POSTs pol.Cancellation.create; <paramref name="effectiveAt"/> defaults to the clock's now, <paramref name="kind"/> to STANDARD.</summary>
    public Task<(HttpResponseMessage Response, JsonNode? Body)> CancelAsync(
        string policyId, string source = "Policyholder", string? kind = null, string? effectiveAt = null, bool dryRun = false, Guid? key = null, string roles = Underwriter) =>
        SendAsync(
            Client, HttpMethod.Post, "/api/pol/v1/cancellations" + (dryRun ? "?dryRun=true" : string.Empty),
            new { policyId, source, reasonCode = "CUSTOMER_REQUEST", effectiveAt = effectiveAt ?? Clock.Now.ToString(), kind = (kind ?? "STANDARD").ToUpperInvariant() },
            roles: roles, key: key);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await _root.DisposeAsync();
    }
}

/// <summary>A bound policy of the cancellation tests.</summary>
internal sealed record BoundPolicy(string PolicyId, string TermId, string TransactionId, string TermState);

/// <summary>
/// <c>TaxCalculator.treatment</c> as SL3-MKT-TREATMENT will bind it, for the GR rows of REQ-MKT-331: a Policyholder cancellation
/// of an IPT line is KEEP_NOT_REDUCED, PendingOpinion (provisional). A test switches it to a missing rule, a refusal like
/// Production's "not Settled" gate, or another action.
/// </summary>
internal sealed class FakeTaxCalculator : ITaxCalculator
{
    public enum Mode
    {
        Gr,
        RuleMissing,
        NotSettledInProduction,
        ApplyAction,
    }

    public Mode Behaviour { get; set; } = Mode.Gr;

    public List<TaxTreatmentRequest> Requests { get; } = [];

    public ValueTask<TaxCalculationResult> CalculateAsync(TaxCalculationRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<TaxTreatmentResult> TreatmentAsync(TaxTreatmentRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        switch (Behaviour)
        {
            case Mode.RuleMissing:
                throw new SpiException(new SpiError(SpiErrorCategory.RuleMissing, "RULE_MISSING"), "No treatment rule (fail closed).");
            case Mode.NotSettledInProduction:
                throw new DomainException(DomainError.Of(ModuleCode.MKT, "CFG-NOT-SETTLED", "Production refuses values that are not Settled."));
        }

        var apply = Behaviour == Mode.ApplyAction;
        return ValueTask.FromResult(new TaxTreatmentResult
        {
            ChargeType = request.ChargeType,
            Action = apply ? TreatmentAction.Apply : TreatmentAction.KeepNotReduced,
            CustomerCredit = apply ? CustomerCredit.ProRata : CustomerCredit.None,
            AuthorityLiability = apply ? AuthorityLiability.Reduce : AuthorityLiability.NotReduce,
            FiscalDocument = FiscalDocumentTreatment.CreditNote,
            RuleId = "GR-TRT-IPT-CANCEL-POLICYHOLDER",
            RuleVersion = "0.1.0",
            LegalStatus = TreatmentLegalStatus.Pending,
            LegalSourceRef = "PRD-17 REQ-MKT-331 (test double)",
        });
    }
}
