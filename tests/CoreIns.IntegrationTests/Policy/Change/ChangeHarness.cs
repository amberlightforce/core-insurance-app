using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Commands.Change;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Change;

/// <summary>
/// The tax and levy port of the change tests (stand-in for RAT servicing tax lines + MKT <c>TaxCalculator.treatment</c>, D-SL3-05):
/// a premium debit applies a 15 % tax (<c>APPLY</c>), a credit keeps it not reduced (<c>KEEP_NOT_REDUCED</c>, 0.00); both
/// carry a rule id, version, legal status and the provisional flag. ILLUSTRATIVE TEST DATA.
/// </summary>
internal sealed class TaxBehaviour
{
    /// <summary>"ok", "missing" (no tax lines) or "credit-tax" (a credit that reduces the tax).</summary>
    public string Mode { get; set; } = "ok";
}

internal sealed class ScriptedServicingTax(TaxBehaviour behaviour) : IServicingTax
{
    public Task<Result<IReadOnlyList<PricedTaxLine>>> LinesAsync(ServicingTaxRequest request, CancellationToken cancellationToken)
    {
        if (behaviour.Mode == "missing")
        {
            return Task.FromResult<Result<IReadOnlyList<PricedTaxLine>>>(new List<PricedTaxLine>());
        }

        var lines = request.PremiumDeltas.Select(delta =>
        {
            var debit = delta.Amount > 0m;
            return new PricedTaxLine(
                delta.Key, delta.Key.CoverageCode, "GR-IPT", ChargeCategories.Tax, 0.15m,
                debit ? decimal.Round(delta.Amount * 0.15m, 2, MidpointRounding.AwayFromZero) : behaviour.Mode == "credit-tax" ? decimal.Round(delta.Amount * 0.15m, 2, MidpointRounding.AwayFromZero) : 0m,
                debit ? TreatmentActionCode.Apply : TreatmentActionCode.KeepNotReduced, debit ? "TEST-IPT-ENDORSE-DEBIT" : "TEST-IPT-ENDORSE-CREDIT", "1",
                debit ? "Unverified" : "PendingOpinion", true);
        }).ToList();
        return Task.FromResult<Result<IReadOnlyList<PricedTaxLine>>>(lines);
    }
}

/// <summary>A policy bound by the harness.</summary>
internal sealed record Issued(string PartyId, string PolicyId, string TermId, string Locator, string IssuanceTransactionId, DateTime Start, DateTime End, string Number);

/// <summary>
/// The POL change slice: the POL slice host (real PTY, PFC, MKT rounding; RAT and UW scripted) with a manual clock and the
/// scripted tax port. Rates are a function of the vehicle's engine capacity and value, so an edit moves the price:
/// MTPL = capacity × 0.2231, own damage = value × 0.008 (4 decimals). Every rate is ILLUSTRATIVE TEST DATA.
/// </summary>
internal sealed class ChangeHarness : IAsyncDisposable
{
    public const string Csr = "Staff.Csr";

    /// <summary>Holds the job permissions but not <c>pol.change</c>.</summary>
    public const string NoChange = "Staff.NoChange";

    private static readonly TimeZoneInfo Athens = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");

    private readonly PostgresFixture _database;
    private WebApplicationFactory<Program>? _factory;

    public ChangeHarness(PostgresFixture database)
    {
        _database = database;
        // A CSR holds the change permissions too (the role does not exist in the dev users; it only shows the date limits).
        var settings = new Dictionary<string, string?>();
        foreach (var permission in new[] { "pol.PolicyChange.create", "pol.change", "pol.Job.updateDraft", "pol.Job.quote", "pol.Job.bind", "pol.Job.get", "pol.Job.withdraw" })
        {
            settings[$"Platform:Permissions:Grants:{permission}:0"] = "Staff.Underwriter";
            settings[$"Platform:Permissions:Grants:{permission}:1"] = Csr;
            if (permission.StartsWith("pol.Job.", StringComparison.Ordinal))
            {
                settings[$"Platform:Permissions:Grants:{permission}:2"] = NoChange;
            }
        }

        Slice = new PolicySlice(database.AppConnectionString, settings: settings);
    }

    public PolicySlice Slice { get; }

    public TaxBehaviour Tax { get; } = new();

    public ManualClock Clock { get; } = new(Instant.FromDateTimeOffset(DateTimeOffset.UtcNow));

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Slice.SeedAsync();
        Sha256Hash configuration;
        await using (var scope = Slice.Factory.Services.CreateAsyncScope())
        {
            configuration = (await scope.ServiceProvider.GetRequiredService<IMarketConfigurationService>().CurrentHashAsync()).Hash!.Value;
        }

        Slice.Rating.Setup("rat.Rate.rate", call => Rate((RateRateRequest)call.Arguments[0]!, configuration));
        _factory = Slice.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
            services.RemoveAll<IServicingTax>();
            services.AddSingleton(Tax);
            services.AddScoped<IServicingTax, ScriptedServicingTax>();
            // IProration is the production binding: RAT's proration behind POL's port (RatingProrationAdapter), bound per term.
        }));
        Client = _factory.CreateClient();
    }

    public IServiceProvider Services => _factory!.Services;

    public static decimal MtplRate(int capacity) => decimal.Round(capacity * 0.2231m, 4);

    public static decimal OwnDamageRate(decimal value) => decimal.Round(value * 0.008m, 4);

    private static RateRateResponse Rate(RateRateRequest request, Sha256Hash configuration)
    {
        var vehicle = request.Segments[0].RiskTree.GetProperty("vehicle");
        var locator = vehicle.GetProperty("elementId").GetString()!;
        var capacity = vehicle.GetProperty("engineCapacityCc").GetInt32();
        var value = vehicle.TryGetProperty("vehicleValue", out var v) ? decimal.Parse(v.GetString()!, CultureInfo.InvariantCulture) : 0m;
        var segment = request.Segments[0].SegmentId;
        var eur = Currency.FromCode("EUR");
        RateRateResponse.RateItem Item(string chargeType, string coverage, decimal rate) => new()
        {
            SegmentId = segment, ElementId = locator, ChargeType = chargeType, ChargeCategory = "PREMIUM", CoverageCode = coverage,
            AnnualRate = rate, Currency = eur, Handling = RateRateResponse.RateItem.HandlingValue.Proratable,
        };

        var mtpl = MtplRate(capacity);
        return new RateRateResponse
        {
            Rates = [Item("PREM-MTPL", "MTPL", mtpl), Item("PREM-OD", "OWN-DAMAGE", OwnDamageRate(value))],
            Taxes =
            [
                new RateRateResponse.TaxeItem
                {
                    SegmentId = segment, CoverageCode = "MTPL", ChargeType = "GR-IPT", Category = RateRateResponse.TaxeItem.CategoryValue.Tax, ChargeCategory = "TAX",
                    LegalStatus = "Unverified", Provisional = true, Base = new Money(decimal.Round(mtpl, 2, MidpointRounding.AwayFromZero), eur), Rate = 0.15m,
                    Amount = new Money(decimal.Round(decimal.Round(mtpl, 2, MidpointRounding.AwayFromZero) * 0.15m, 2, MidpointRounding.AwayFromZero), eur),
                    ConfigurationKey = "test.tax.rate",
                },
            ],
            RatingArtefactHash = Sha256Hash.Parse(PolicySlice.RatingArtefactHash),
            ConfigurationHash = new ConfigurationHash(configuration),
            Bindable = request.Envelope.Mode == RateRateRequest.EnvelopeDetail.ModeValue.Full,
            WorksheetId = Sha256Hash.Parse(PolicySlice.WorksheetId),
            WorksheetHash = Sha256Hash.Parse(PolicySlice.WorksheetId),
        };
    }

    // ---- HTTP -----------------------------------------------------------------------------------------------------

    public Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body = null, string roles = Underwriter, Guid? key = null) =>
        CoreIns.IntegrationTests.Party.PartyApi.SendAsync(Client, method, path, body, roles, key);

    /// <summary>Issues a policy (one vehicle, MTPL + own damage) starting in an hour and returns it; the clock stays at "now".</summary>
    public async Task<Issued> IssueAsync()
    {
        var party = await Slice.CreatePartyAsync();
        var (jobId, _, draft) = await Slice.DraftAsync(party, DateTimeOffset.UtcNow.AddHours(1));
        (await Slice.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (response, bind) = await Slice.BindAsync(jobId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        var termId = bind.Text("termId");
        var start = await ScalarAsync<DateTime>($"SELECT valid_from FROM pol.policy_term WHERE term_id = '{termId}' AND recorded_to IS NULL");
        var end = await ScalarAsync<DateTime>($"SELECT valid_to FROM pol.policy_term WHERE term_id = '{termId}' AND recorded_to IS NULL");
        return new Issued(party, bind.Text("policyId"), termId, draft.Text("riskTree.vehicles.0.locator"), bind.Text("transactionId"), start, end, bind.Text("policyNumber"));
    }

    /// <summary>Moves the clock to <paramref name="days"/> days after the term's start.</summary>
    public Instant SetDay(Issued policy, int days)
    {
        var instant = Instant.FromUtcDateTime(DateTime.SpecifyKind(policy.Start.AddDays(days), DateTimeKind.Utc));
        Clock.Set(instant);
        // The record-time watermark may not run more than a day (plus the dev clock offset) ahead of the database clock (POL-TEMPORAL):
        // tell the database how far this test's clock runs ahead, as the dev clock endpoint would.
        var ahead = (long)(instant.ToUtcDateTime() - DateTime.UtcNow).TotalMicroseconds;
        if (ahead > 0)
        {
            ExecuteAsync($"UPDATE plt.dev_clock SET offset_micros = {ahead}, version = version + 1, updated_at = now() WHERE offset_micros < {ahead}").GetAwaiter().GetResult();
        }

        return instant;
    }

    public async Task<(HttpResponseMessage Response, JsonNode? Body)> StartChangeAsync(Issued policy, string? effectiveAt = null, string roles = Underwriter, bool dryRun = false) =>
        await SendAsync(HttpMethod.Post, "/api/pol/v1/policy-changes" + (dryRun ? "?dryRun=true" : string.Empty), new { policyId = policy.PolicyId, effectiveAt = effectiveAt ?? Iso(Clock.Now.ToUtcDateTime()) }, roles);

    public async Task<string> NewChangeAsync(Issued policy, string? effectiveAt = null)
    {
        var (response, body) = await StartChangeAsync(policy, effectiveAt);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return body.Text("jobId");
    }

    /// <summary>The vehicle of the policy with the given rating fields (everything else as issued).</summary>
    public static object Vehicle(Issued policy, int capacity = 1400, string value = "15000.00", string use = "PRIVATE", string plate = "ikx-1234", int year = 2021) => new
    {
        locator = policy.Locator, plate, make = "Toyota", model = "Yaris", firstRegistrationYear = year, engineCapacityCc = capacity, use,
        value = new { amount = value, currency = "EUR" },
    };

    public async Task<(HttpResponseMessage Response, JsonNode? Body)> EditAsync(string jobId, object[] instructions, int draftVersion = 0, int versionNo = 1) =>
        await SendAsync(HttpMethod.Post, "/api/pol/v1/jobs/update-draft", new { jobId, versionNo, expectedDraftVersion = draftVersion, instructions });

    public async Task<(HttpResponseMessage Response, JsonNode? Body)> EditVehicleAsync(string jobId, Issued policy, int capacity = 1400, string value = "15000.00", string use = "PRIVATE", string plate = "ikx-1234")
    {
        var result = await EditAsync(jobId, [new { op = "SET_VEHICLE", vehicle = Vehicle(policy, capacity, value, use, plate) }]);
        result.Response.StatusCode.ShouldBe(HttpStatusCode.OK, result.Body?.ToJsonString());
        return result;
    }

    public async Task<(HttpResponseMessage Response, JsonNode? Body)> QuoteAsync(string jobId, int versionNo = 1, bool dryRun = false, string roles = Underwriter) =>
        await SendAsync(HttpMethod.Post, "/api/pol/v1/jobs/quote" + (dryRun ? "?dryRun=true" : string.Empty), new { jobId, versionNo }, roles);

    public async Task<(HttpResponseMessage Response, JsonNode? Body)> BindAsync(string jobId, int versionNo = 1, bool dryRun = false, bool confirmation = true, string roles = Underwriter) =>
        await SendAsync(HttpMethod.Post, "/api/pol/v1/jobs/bind" + (dryRun ? "?dryRun=true" : string.Empty), new { jobId, versionNo, paymentPlanOption = "ANNUAL", confirmation }, roles);

    public async Task<(HttpResponseMessage Response, JsonNode? Body)> WithdrawAsync(string jobId, string roles = Underwriter) =>
        await SendAsync(HttpMethod.Post, "/api/pol/v1/jobs/withdraw", new { jobId, reasonCode = "CUSTOMER_DECLINED" }, roles);

    public async Task<(HttpResponseMessage Response, JsonNode? Body)> PreviewAsync(string jobId, string roles = Underwriter) =>
        await SendAsync(HttpMethod.Get, $"/api/pol/v1/policy-changes/{jobId}/preview", roles: roles);

    // ---- database -------------------------------------------------------------------------------------------------

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(_database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull ? default! : (T)value;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(_database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Writes a new current version of the term the way a command would (policy lock watermark moved forward, the old version
    /// closed at it, the new one stamped with it): the cancellation or foreign transaction another builder's command would write.
    /// </summary>
    public async Task NewTermVersionAsync(Issued policy, string state = "CANCELLED", string? cancelledAtSql = null, string? headSql = null)
    {
        await using var dataSource = NpgsqlDataSource.Create(_database.SuperuserConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var sql = $"""
            UPDATE pol.policy SET last_recorded_at = last_recorded_at + interval '1 second', record_version = record_version + 1 WHERE policy_id = '{policy.PolicyId}';
            CREATE TEMP TABLE old_term ON COMMIT DROP AS SELECT * FROM pol.policy_term WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL;
            UPDATE pol.policy_term SET recorded_to = (SELECT last_recorded_at FROM pol.policy WHERE policy_id = '{policy.PolicyId}')
                WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL;
            INSERT INTO pol.policy_term (term_version_id, term_id, policy_id, legal_entity_id, term_number, valid_from, valid_to, recorded_from, recorded_to, state,
                product_version, artefact_hash, rating_artefact_hash, resolution_hash, configuration_hash, currency, producer_code, payment_plan_ref, written_date,
                head_transaction_id, created_by, predecessor_term_id, cancelled_at)
            SELECT gen_random_uuid(), term_id, policy_id, legal_entity_id, term_number, valid_from, valid_to,
                (SELECT last_recorded_at FROM pol.policy WHERE policy_id = '{policy.PolicyId}'), NULL, '{state}',
                product_version, artefact_hash, rating_artefact_hash, resolution_hash, configuration_hash, currency, producer_code, payment_plan_ref, written_date,
                {headSql ?? "head_transaction_id"}, created_by, predecessor_term_id, {cancelledAtSql ?? "cancelled_at"}
            FROM old_term;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
    }

    // ---- arithmetic -----------------------------------------------------------------------------------------------

    /// <summary>Whole Europe/Athens dates from <paramref name="from"/> to <paramref name="to"/> (D-SL3-04).</summary>
    public static int Days(DateTime from, DateTime to) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(to, DateTimeKind.Utc), Athens)).DayNumber
        - DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(from, DateTimeKind.Utc), Athens)).DayNumber;

    /// <summary>The prorated change of an annual rate difference over <paramref name="days"/> of a 365-day term, rounded half up to cents.</summary>
    public static decimal Prorated(decimal rateDifference, int days) => decimal.Round(rateDifference * days / 365m, 2, MidpointRounding.AwayFromZero);

    public static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await Slice.DisposeAsync();
    }
}
