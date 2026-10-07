using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Product;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Rating.Contracts;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Modules.Underwriting.Contracts;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Platform.Contracts.Common;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.Testing.Contracts.Fakes.Rating;
using CoreIns.Testing.Contracts.Fakes.Underwriting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy;

/// <summary>
/// The POL slice host: the real Host and database with the real PTY, PFC (the seeded Motor Private Car product) and MKT
/// rounding. With <c>realRatingAndUnderwriting</c> RAT and UW are the real modules too (product MOTOR-GR, their built-in
/// illustrative tariff and rule sets); otherwise they are the generated sandbox doubles, so a test can script exact rates
/// and UW issues (product code unique per host). Every rate here is ILLUSTRATIVE TEST DATA (D-SLC-04).
/// </summary>
internal sealed class PolicySlice : IAsyncDisposable
{
    public const string RatingArtefactHash = "2222222222222222222222222222222222222222222222222222222222222222";
    public const string WorksheetId = "4444444444444444444444444444444444444444444444444444444444444444";

    private readonly ApiHostFactory _root;
    private Sha256Hash? _configurationHash;

    public PolicySlice(string connectionString, bool realRatingAndUnderwriting = false, bool allowMissingDraftValidation = true)
    {
        _root = new ApiHostFactory(
            connectionString,
            settings: new Dictionary<string, string?> { ["Policy:AllowMissingDraftValidation"] = allowMissingDraftValidation ? "true" : "false" });
        Real = realRatingAndUnderwriting;
        Product = Real ? "MOTOR-GR" : "MOTOR-GR-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        Factory = Real
            ? _root
            : _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IRatingRateService>(Rating);
                services.AddSingleton<IUnderwritingRulesService>(Underwriting);
            }));
        Client = Factory.CreateClient();
        Rating.Setup("rat.Rate.rate", call => Rate((RateRateRequest)call.Arguments[0]!));
        AcceptAll();
    }

    public bool Real { get; }

    public WebApplicationFactory<Program> Factory { get; }

    public HttpClient Client { get; }

    /// <summary>The product: MOTOR-GR with the real RAT/UW (their artefact and rule sets name it), else a code unique to this host.</summary>
    public string Product { get; }

    public FakeRatingRateService Rating { get; } = new();

    public FakeUnderwritingRulesService Underwriting { get; } = new();

    /// <summary>Imports and locks the PFC seed (MOTOR-GR 1.0) under this host's product code (re-importing the same definition is idempotent).</summary>
    public async Task SeedAsync()
    {
        // The scripted RAT prices under MKT's current configuration, as the real one does (the bind compares hashes).
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            _configurationHash = (await scope.ServiceProvider.GetRequiredService<IMarketConfigurationService>().CurrentHashAsync()).Hash;
        }

        var (response, body) = await ProductApi.ImportAsync(Client, ProductApi.Seed(Product));
        response.IsSuccessStatusCode.ShouldBeTrue(body?.ToJsonString());
    }

    /// <summary>UW raises nothing: accept.</summary>
    public void AcceptAll() => Underwriting.Setup("uw.Rules.evaluate", new RulesEvaluateResponse { EvaluationId = Guid.CreateVersion7(), Issues = [], Outcome = RulesEvaluateResponse.OutcomeValue.Accept });

    /// <summary>UW raises one open issue blocking <paramref name="point"/> at every checkpoint.</summary>
    public void Raise(BlockingPoint point) => Underwriting.Setup("uw.Rules.evaluate", new RulesEvaluateResponse
    {
        EvaluationId = Guid.CreateVersion7(),
        Outcome = RulesEvaluateResponse.OutcomeValue.Refer,
        Lane = "ASSISTED",
        Issues =
        [
            new RulesEvaluateResponse.IssueItem
            {
                IssueId = new UwIssueId(Guid.CreateVersion7()), IssueType = "YOUNG_DRIVER", BlockingPoint = point, IssueKey = "driver:1",
                ApprovalStatus = "OPEN", Severity = "REFER",
            },
        ],
    });

    /// <summary>
    /// Illustrative rating (TEST DATA): per vehicle an MTPL premium and an own-damage premium, plus one tax line on the
    /// MTPL premium in the rate-item shape. Amounts carry four decimals so that rounding is visible.
    /// </summary>
    private RateRateResponse Rate(RateRateRequest request)
    {
        var tree = request.Segments[0].RiskTree;
        var vehicle = tree.GetProperty("vehicle").GetProperty("elementId").GetString()!;
        var segment = request.Segments[0].SegmentId;
        var eur = Currency.FromCode("EUR");
        RateRateResponse.RateItem Item(string chargeType, string coverage, decimal rate) => new()
        {
            SegmentId = segment, ElementId = vehicle, ChargeType = chargeType, ChargeCategory = "PREMIUM", CoverageCode = coverage,
            AnnualRate = rate, Currency = eur, Handling = RateRateResponse.RateItem.HandlingValue.Proratable,
        };

        return new RateRateResponse
        {
            Rates = [Item("PREM-MTPL", "MTPL", 312.3456m), Item("PREM-OD", "OWN-DAMAGE", 120.005m)],
            Taxes =
            [
                new RateRateResponse.TaxeItem
                {
                    SegmentId = segment, CoverageCode = "MTPL", ChargeType = "GR-IPT", Category = RateRateResponse.TaxeItem.CategoryValue.Tax, ChargeCategory = "TAX", LegalStatus = "Unverified", Provisional = true,
                    Base = new Money(312.35m, eur), Rate = 0.15m, Amount = new Money(46.85m, eur), ConfigurationKey = "test.tax.rate",
                },
            ],
            RatingArtefactHash = Sha256Hash.Parse(RatingArtefactHash),
            ConfigurationHash = _configurationHash is { } hash ? new ConfigurationHash(hash) : null,
            Bindable = request.Envelope.Mode == RateRateRequest.EnvelopeDetail.ModeValue.Full,
            WorksheetId = Sha256Hash.Parse(WorksheetId),
            WorksheetHash = Sha256Hash.Parse(WorksheetId),
        };
    }

    /// <summary>A policyholder (and main driver) with a date of birth in PTY (synthetic data).</summary>
    public async Task<string> CreatePartyAsync(string birthDate = "1980-05-17")
    {
        var (response, body) = await SendAsync(Client, HttpMethod.Post, "/api/pty/v1/parties", Person("Μαρία", "Παπαδοπούλου", null, birthDate));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return body.Text("party.partyId");
    }

    public object Submission(string partyId, DateTimeOffset effectiveAt, string quoteType = "FULL") => new
    {
        policyholderPartyId = partyId,
        product = Product,
        channel = "STAFF",
        effectiveAt = effectiveAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
        quoteType,
    };

    /// <summary>A Greek private car with a look-alike plate, a main driver and MTPL + own damage (synthetic data).</summary>
    public static object[] MotorRisk(string usage = "PRIVATE")
    {
        var answers = new Dictionary<string, string> { ["Q-USAGE"] = usage, ["Q-HIRE-REWARD"] = "NO" };
        if (usage == "BUSINESS")
        {
            answers["Q-BUSINESS-USE"] = "Deliveries";
        }

        return
        [
            new
            {
                op = "SET_VEHICLE",
                vehicle = new
                {
                    plate = "ikx-1234", make = "Toyota", model = "Yaris", firstRegistrationYear = 2021, engineCapacityCc = 1400, use = usage,
                    value = new { amount = "15000.00", currency = "EUR" },
                },
            },
            new { op = "SET_ANSWERS", questionSet = new { questionSetCode = "MOTOR-RISK", questionSetVersion = "1", answers } },
        ];
    }

    public static object[] DriverAndCovers(string vehicleLocator, string driverPartyId) =>
    [
        new { op = "SET_DRIVER", driver = new { partyId = driverPartyId, driverType = "MAIN", yearFirstLicensed = 2010, vehicleLocator, usagePercent = 100, claimsLast5Years = 0 } },
        new
        {
            op = "SET_COVERAGES",
            coverages = new object[]
            {
                new { coverageCode = "MTPL", elementLocator = vehicleLocator, selected = true },
                new { coverageCode = "OWN-DAMAGE", elementLocator = vehicleLocator, selected = true },
            },
        },
    ];

    /// <summary>Creates a submission and fills the motor risk; returns (jobId, draftVersion).</summary>
    public async Task<(string JobId, int DraftVersion, JsonNode Draft)> DraftAsync(string partyId, DateTimeOffset effectiveAt, string quoteType = "FULL", string usage = "PRIVATE")
    {
        var (created, submission) = await SendAsync(Client, HttpMethod.Post, "/api/pol/v1/submissions", Submission(partyId, effectiveAt, quoteType));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, submission?.ToJsonString());
        var jobId = submission.Text("jobId");
        var (first, draft) = await SendAsync(Client, HttpMethod.Post, "/api/pol/v1/jobs/update-draft",
            new { jobId, versionNo = 1, expectedDraftVersion = 0, instructions = MotorRisk(usage) });
        first.StatusCode.ShouldBe(HttpStatusCode.OK, draft?.ToJsonString());
        var vehicle = draft.Text("riskTree.vehicles.0.locator");
        var (second, filled) = await SendAsync(Client, HttpMethod.Post, "/api/pol/v1/jobs/update-draft",
            new { jobId, versionNo = 1, expectedDraftVersion = 1, instructions = DriverAndCovers(vehicle, partyId) });
        second.StatusCode.ShouldBe(HttpStatusCode.OK, filled?.ToJsonString());
        return (jobId, 2, filled!);
    }

    public async Task<(HttpResponseMessage Response, JsonNode? Body)> QuoteAsync(string jobId, int versionNo = 1) =>
        await SendAsync(Client, HttpMethod.Post, "/api/pol/v1/jobs/quote", new { jobId, versionNo });

    public async Task<(HttpResponseMessage Response, JsonNode? Body)> BindAsync(string jobId, int versionNo = 1, Guid? key = null, bool confirmation = true, string roles = Underwriter, bool dryRun = false) =>
        await SendAsync(Client, HttpMethod.Post, "/api/pol/v1/jobs/bind" + (dryRun ? "?dryRun=true" : string.Empty),
            new { jobId, versionNo, paymentPlanOption = "ANNUAL", confirmation }, roles: roles, key: key);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await _root.DisposeAsync();
    }
}
