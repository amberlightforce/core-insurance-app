using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Rating.Contracts;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Modules.Underwriting.Contracts;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Platform.Contracts.Common;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.Testing.Contracts.Fakes.Market;
using CoreIns.Testing.Contracts.Fakes.Product;
using CoreIns.Testing.Contracts.Fakes.Rating;
using CoreIns.Testing.Contracts.Fakes.Underwriting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy;

/// <summary>
/// The POL slice host: the real Host and database, with PFC, RAT, UW and MKT rounding replaced by their generated
/// sandbox doubles (those modules are built in parallel, D-SLC-01). Every rate below is ILLUSTRATIVE TEST DATA
/// (D-SLC-04): it is not a tariff, a tax rate or any regulatory value.
/// </summary>
internal sealed class PolicySlice : IAsyncDisposable
{
    public const string Product = "MOTOR_PRIVATE_CAR";
    public const string ArtefactHash = "1111111111111111111111111111111111111111111111111111111111111111";
    public const string RatingArtefactHash = "2222222222222222222222222222222222222222222222222222222222222222";
    public const string ResolutionHashValue = "3333333333333333333333333333333333333333333333333333333333333333";
    public const string WorksheetId = "4444444444444444444444444444444444444444444444444444444444444444";

    private readonly ApiHostFactory _root;

    public PolicySlice(string connectionString)
    {
        _root = new ApiHostFactory(connectionString);
        Factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IProductProductVersionService>(Versions);
            services.AddSingleton<IProductQuestionSetService>(QuestionSets);
            services.AddSingleton<IProductPolicyDraftService>(Drafts);
            services.AddSingleton<IRatingRateService>(Rating);
            services.AddSingleton<IMarketRoundingService>(Rounding);
            services.AddSingleton<IUnderwritingRulesService>(Underwriting);
        }));
        Client = Factory.CreateClient();

        Versions.Setup("pfc.ProductVersion.resolve", new ProductVersionResolveResponse
        {
            Version = new ProductVersionNumber(1, 0),
            ArtefactHash = Sha256Hash.Parse(ArtefactHash),
            ResolutionManifest = new ProductVersionResolveResponse.ResolutionManifestDetail
            {
                ArtefactHash = Sha256Hash.Parse(ArtefactHash), RatingArtefactHash = Sha256Hash.Parse(RatingArtefactHash),
            },
            ResolutionHash = ResolutionHash.Parse(ResolutionHashValue),
        });
        QuestionSets.Setup("pfc.QuestionSet.evaluate", new QuestionSetEvaluateResponse { KnockOutFlags = false, ReferralFlags = false });
        Drafts.Setup("pfc.PolicyDraft.validate", new PolicyDraftValidateResponse { Errors = [] });
        Rating.Setup("rat.Rate.rate", call => Rate((RateRateRequest)call.Arguments[0]!));

        // Test double of mkt.Rounding.apply: two decimals, half away from zero (the real rule is MKT configuration).
        Rounding.Setup("mkt.Rounding.apply", call =>
        {
            var request = (RoundingApplyRequest)call.Arguments[0]!;
            return new RoundingApplyResponse { AmountAfterRounding = request.Amount!.Value.Round(2, MidpointRounding.AwayFromZero) };
        });
        AcceptAll();
    }

    public WebApplicationFactory<Program> Factory { get; }

    public HttpClient Client { get; }

    public FakeProductProductVersionService Versions { get; } = new();

    public FakeProductQuestionSetService QuestionSets { get; } = new();

    public FakeProductPolicyDraftService Drafts { get; } = new();

    public FakeRatingRateService Rating { get; } = new();

    public FakeMarketRoundingService Rounding { get; } = new();

    public FakeUnderwritingRulesService Underwriting { get; } = new();

    /// <summary>UW raises nothing: accept.</summary>
    public void AcceptAll() => Underwriting.Setup("uw.Rules.evaluate", new RulesEvaluateResponse { EvaluationId = Guid.CreateVersion7(), Issues = [] });

    /// <summary>UW raises one open issue blocking <paramref name="point"/> at every checkpoint.</summary>
    public void Raise(BlockingPoint point) => Underwriting.Setup("uw.Rules.evaluate", new RulesEvaluateResponse
    {
        EvaluationId = Guid.CreateVersion7(),
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
    private static RateRateResponse Rate(RateRateRequest request)
    {
        var tree = request.Segments[0].RiskTree;
        var vehicle = tree.GetProperty("vehicles")[0].GetProperty("locator").GetString()!;
        var segment = request.Segments[0].SegmentId;
        var eur = Currency.FromCode("EUR");
        RateRateResponse.RateItem Item(string chargeType, string category, string coverage, decimal rate) => new()
        {
            SegmentId = segment, ElementId = vehicle, ChargeType = chargeType, ChargeCategory = category, CoverageCode = coverage,
            AnnualRate = rate, Currency = eur, Handling = RateRateResponse.RateItem.HandlingValue.Proratable,
        };

        return new RateRateResponse
        {
            Rates = [Item("MTPL_PREMIUM", "PREMIUM", "MTPL", 312.3456m), Item("OD_PREMIUM", "PREMIUM", "OWN_DAMAGE", 120.005m)],
            Taxes = [JsonSerializer.SerializeToElement(Item("TEST_TAX", "TAX", "MTPL", 46.8518m), SharedKernelJson.Options)],
            Bindable = request.Envelope.Mode == RateRateRequest.EnvelopeDetail.ModeValue.Full,
            WorksheetId = Sha256Hash.Parse(WorksheetId),
            WorksheetHash = Sha256Hash.Parse(WorksheetId),
        };
    }

    public async Task<string> CreatePartyAsync()
    {
        var (response, body) = await SendAsync(Client, HttpMethod.Post, "/api/pty/v1/parties", Person("Μαρία", "Παπαδοπούλου", null));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return body.Text("party.partyId");
    }

    public static object Submission(string partyId, DateTimeOffset effectiveAt, string quoteType = "FULL") => new
    {
        policyholderPartyId = partyId,
        product = Product,
        channel = "STAFF",
        effectiveAt = effectiveAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
        quoteType,
    };

    /// <summary>A Greek private car with a look-alike plate, a main driver and MTPL + own damage (synthetic data).</summary>
    public static object[] MotorRisk() =>
    [
        new { op = "SET_VEHICLE", vehicle = new { plate = "ikx-1234", make = "Toyota", model = "Yaris", firstRegistrationYear = 2021, use = "PRIVATE" } },
        new { op = "SET_ANSWERS", questionSet = new { questionSetCode = "GR_MOTOR_PREQUAL", questionSetVersion = "1", answers = new { garagedOvernight = true } } },
    ];

    public static object[] DriverAndCovers(string vehicleLocator, string driverPartyId) =>
    [
        new { op = "SET_DRIVER", driver = new { partyId = driverPartyId, driverType = "MAIN", yearFirstLicensed = 2010, vehicleLocator, usagePercent = 100 } },
        new
        {
            op = "SET_COVERAGES",
            coverages = new object[]
            {
                new { coverageCode = "MTPL", elementLocator = vehicleLocator, selected = true },
                new { coverageCode = "OWN_DAMAGE", elementLocator = vehicleLocator, selected = true },
            },
        },
    ];

    /// <summary>Creates a submission and fills the motor risk; returns (jobId, draftVersion).</summary>
    public async Task<(string JobId, int DraftVersion, JsonNode Draft)> DraftAsync(string partyId, DateTimeOffset effectiveAt, string quoteType = "FULL")
    {
        var (created, submission) = await SendAsync(Client, HttpMethod.Post, "/api/pol/v1/submissions", Submission(partyId, effectiveAt, quoteType));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, submission?.ToJsonString());
        var jobId = submission.Text("jobId");
        var (first, draft) = await SendAsync(Client, HttpMethod.Post, "/api/pol/v1/jobs/update-draft",
            new { jobId, versionNo = 1, expectedDraftVersion = 0, instructions = MotorRisk() });
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
            new { jobId, versionNo, paymentPlanOption = "TEST_PLAN_ANNUAL", confirmation }, roles: roles, key: key);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await _root.DisposeAsync();
    }
}
