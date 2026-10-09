using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Reinsurance.Registry;

/// <summary>A clock a test moves (the "IClock test double" of the brief). Time still passes nowhere on its own.</summary>
internal sealed class MutableClock(Instant now) : IClock
{
    public Instant Now { get; set; } = now;
}

/// <summary>
/// The RI registry host: the real Host and database with the real PTY (reinsurers are real organisation parties) and a
/// <see cref="MutableClock"/>. Every treaty, party and amount here is synthetic and ILLUSTRATIVE (D-SL4-04).
/// </summary>
internal sealed class RegistrySlice : IAsyncDisposable
{
    public const string Accountant = "Staff.ReinsuranceAccountant";
    public const string Manager = "Staff.ReinsuranceManager";
    public const string ClaimsHandler = "Staff.ClaimsHandler";

    /// <summary>Athens is UTC+2 in winter: 2026-02-10 11:00 local.</summary>
    public static readonly Instant Start = Instant.FromUtc(2026, 2, 10, 9, 0, 0);

    private readonly string _superuser;
    private readonly ApiHostFactory _root;

    public RegistrySlice(PostgresFixture database, Instant? now = null)
    {
        _superuser = database.SuperuserConnectionString;
        Clock = new MutableClock(now ?? Start);
        _root = new ApiHostFactory(database.AppConnectionString);
        Factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IClock>(Clock)));
        Client = Factory.CreateClient();
    }

    public MutableClock Clock { get; }

    public WebApplicationFactory<Program> Factory { get; }

    public HttpClient Client { get; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A product code unique per call (scope codes are shared by every test of a class).</summary>
    public static string NewProduct() => "P_" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    public async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(
        HttpMethod method, string path, object? body = null, string roles = Accountant, string user = "riacct", Guid? key = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.AcceptLanguage.ParseAdd("en");
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        }

        if (body is not null)
        {
            request.Content = body is JsonNode node ? new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json") : JsonContent.Create(body);
        }

        var response = await Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    /// <summary>A real organisation party in PTY (synthetic legal name); returns its id.</summary>
    public async Task<string> OrganisationAsync(string legalName)
    {
        var (response, body) = await SendAsync(
            HttpMethod.Post, "/api/pty/v1/parties",
            new { partyType = "ORGANISATION", organisation = new { legalName, tradeName = legalName } },
            roles: Underwriter, user: "pty");
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created, body?.ToJsonString());
        return body.Text("party.partyId");
    }

    /// <summary>A real person party in PTY; returns its id (a reinsurer must be an organisation).</summary>
    public async Task<string> PersonAsync()
    {
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/pty/v1/parties", Person("Νίκος", "Παπαδόπουλος", afm: null), roles: Underwriter, user: "pty");
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created, body?.ToJsonString());
        return body.Text("party.partyId");
    }

    /// <summary>
    /// A create body: one XoL layer 500,000 xs 500,000 (AAD 0, AAL 1,500,000), a two-reinsurer panel (60% lead + 40%) placed
    /// at 100%, scope = one product and coverage, period [<paramref name="from"/>, <paramref name="to"/>). <paramref name="change"/> edits the JSON.
    /// </summary>
    public static JsonObject Body(
        string product, string coverage, string leadReinsurer, string followReinsurer, string from = "2026-01-01", string to = "2027-01-01",
        Action<JsonObject>? change = null)
    {
        var body = new JsonObject
        {
            ["legalEntity"] = "GR-TEST",
            ["contractType"] = "XOL_PER_RISK",
            ["contractYear"] = int.Parse(from[..4], CultureInfo.InvariantCulture),
            ["currency"] = "EUR",
            ["period"] = new JsonObject { ["from"] = from, ["to"] = to },
            ["scope"] = new JsonObject { ["productCodes"] = new JsonArray(product), ["coverageCodes"] = new JsonArray(coverage) },
            ["clause"] = new JsonObject { ["alaeIncluded"] = true, ["statutoryInterestIncluded"] = false, ["recoveriesInure"] = "REALISED_ONLY" },
            ["layers"] = new JsonArray(new JsonObject
            {
                ["layerNo"] = 1,
                ["attachment"] = Money("500000.00"),
                ["limit"] = Money("500000.00"),
                ["aad"] = Money("0.00"),
                ["aal"] = Money("1500000.00"),
            }),
            ["participations"] = new JsonArray(
                new JsonObject { ["reinsurerPartyId"] = leadReinsurer, ["signedLinePct"] = "60", ["lead"] = true },
                new JsonObject { ["reinsurerPartyId"] = followReinsurer, ["signedLinePct"] = "40", ["lead"] = false }),
            ["placedPct"] = "100",
        };
        change?.Invoke(body);
        return body;
    }

    public static JsonObject Money(string amount, string currency = "EUR") => new() { ["amount"] = amount, ["currency"] = currency };

    /// <summary>Creates a Draft treaty as the accountant and returns (id, recordVersion, body).</summary>
    public async Task<(string Id, int Version, JsonNode Body)> CreateAsync(JsonObject body, string user = "riacct")
    {
        var (response, created) = await SendAsync(HttpMethod.Post, "/api/ri/v1/contracts", body, Accountant, user);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created, created?.ToJsonString());
        return (created.Text("contract.contractId"), int.Parse(created.Text("contract.recordVersion"), CultureInfo.InvariantCulture), created!);
    }

    /// <summary>Submits as <paramref name="user"/>; returns the new record version.</summary>
    public async Task<int> SubmitAsync(string id, int version, string user = "riacct")
    {
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/ri/v1/contracts/submit", new { contractId = id, expectedRecordVersion = version }, Accountant, user);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, body?.ToJsonString());
        return int.Parse(body.Text("contract.recordVersion"), CultureInfo.InvariantCulture);
    }

    public Task<(HttpResponseMessage Response, JsonNode? Body)> ApproveAsync(
        string id, int version, string user = "rimgr", string decision = "APPROVE", string? reason = null, Guid? key = null) =>
        SendAsync(HttpMethod.Post, "/api/ri/v1/contracts/approve", new { contractId = id, expectedRecordVersion = version, decision, reason }, Manager, user, key);

    /// <summary>create → submit → approve by another person; returns the id and the approve response.</summary>
    public async Task<(string Id, JsonNode? Approved)> ApprovedAsync(JsonObject body)
    {
        var (id, version, _) = await CreateAsync(body);
        var submitted = await SubmitAsync(id, version);
        var (response, approved) = await ApproveAsync(id, submitted);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, approved?.ToJsonString());
        return (id, approved);
    }

    public async Task<JsonNode> GetAsync(string id, string roles = Accountant)
    {
        var (response, body) = await SendAsync(HttpMethod.Get, $"/api/ri/v1/contracts/{id}", roles: roles);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, body?.ToJsonString());
        return body!;
    }

    /// <summary>Runs one pass of the lifecycle scanner (what the Hangfire job does) in the host.</summary>
    public async Task<int> ScanAsync()
    {
        var scope = Factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await scope.ServiceProvider.GetRequiredService<LifecycleScanner>().RunAsync(Ct);
        }
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(_superuser);
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(_superuser);
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>The Athens-local midnight that starts <paramref name="date"/>, as an instant.</summary>
    public static Instant AthensMidnight(int year, int month, int day) =>
        Instant.FromUtcDateTime(TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified), TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens")));

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await _root.DisposeAsync();
    }

    /// <summary>Property names of a JSON object, for "only these keys" assertions.</summary>
    public static IReadOnlyList<string> Keys(JsonElement element) => [.. element.EnumerateObject().Select(p => p.Name)];
}
