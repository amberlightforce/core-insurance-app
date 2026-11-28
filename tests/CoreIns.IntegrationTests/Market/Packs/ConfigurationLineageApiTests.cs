using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Market.Packs;

/// <summary>BO-MKT-03: configuration explain, compare and export.</summary>
public sealed class ConfigurationLineageApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Base = "/api/mkt/v1/configuration";
    private const string Crs = "/api/mkt/v1/config-change-requests";
    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, JsonNode Body)> SendAsync(HttpMethod method, string path, string user, string role, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.Add(TestAuthHandler.RolesHeader, role);
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await _client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!);
    }

    private Task<(HttpStatusCode Status, JsonNode Body)> AdminGetAsync(string path) => SendAsync(HttpMethod.Get, Base + path, "admin", "Platform.Admin");

    private async Task<string> CurrentHashAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1100), Ct);
        var (_, current) = await SendAsync(HttpMethod.Get, Base + "/current-hash", "reader", "Staff.Underwriter");
        return current.Text("hash");
    }

    private async Task ApplyChangeAsync(string key, string value)
    {
        var item = new { key, value, legalStatus = "Settled", sourceRef = "Test change, BO-MKT-03", motorPath = true, validFrom = "2027-01-01", validTo = (string?)null };
        var (created, body) = await SendAsync(HttpMethod.Post, Crs, "cfg-maker", "Platform.ReleaseManager", new { reason = "Change a premium tax rate from 2027", items = new[] { item } });
        created.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        var id = body.Text("request.id");
        (await SendAsync(HttpMethod.Post, Crs + "/submit", "cfg-maker", "Platform.ReleaseManager", new { requestId = id })).Status.ShouldBe(HttpStatusCode.OK);
        var (approved, result) = await SendAsync(HttpMethod.Post, Crs + "/approve", "cfg-checker", "Platform.DesignAuthority", new { requestId = id, reason = "Independent checker confirms" });
        approved.ShouldBe(HttpStatusCode.OK, result.ToJsonString());
    }

    [Fact]
    public async Task Explain_shows_the_layer_and_pack_version_that_supplied_the_value()
    {
        var (status, body) = await AdminGetAsync("/explain?key=tax.ipt.rate.general&context=GR-TEST&validAt=2026-06-01");
        status.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        body.Text("found").ShouldBe("true");
        body.Text("layer").ShouldBe("country:GR");
        body.Text("value").ShouldBe("0.15");
        body.Text("legalStatus").ShouldNotBeNullOrEmpty();
        body.Text("packVersion").ShouldNotBeNullOrEmpty();
        var winning = body["layerContributions"]!.AsArray().Single(c => (bool)c!["winning"]!)!;
        winning.Text("node").ShouldBe("country:GR");
        winning.Text("packVersion").ShouldBe(body.Text("packVersion"));
        body["mergeSteps"]!.AsArray().Count.ShouldBeGreaterThan(1);
        body["cr"].ShouldBeNull();
        (await AdminGetAsync("/explain?key=no.such.key")).Status.ShouldNotBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, Base + "/explain?key=tax.ipt.rate.general", "nobody", "Customer")).Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Compare_of_an_unchanged_configuration_is_empty_and_export_hash_is_the_current_hash()
    {
        var current = await CurrentHashAsync();
        var (status, body) = await AdminGetAsync($"/compare?hash={current}&hashB={current}");
        status.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        body["changes"]!.AsArray().ShouldBeEmpty();
        var (dateStatus, dateBody) = await AdminGetAsync("/compare?validAt=2026-01-01&validAtB=2026-02-01");
        dateStatus.ShouldBe(HttpStatusCode.OK, dateBody.ToJsonString());
        dateBody["changes"]!.AsArray().ShouldBeEmpty();
        (await AdminGetAsync($"/compare?hash={current}&validAt=2026-01-01")).Status.ShouldNotBe(HttpStatusCode.OK);

        var (exported, export) = await SendAsync(HttpMethod.Post, Base + "/export", "admin", "Platform.Admin", new { });
        exported.ShouldBe(HttpStatusCode.OK, export.ToJsonString());
        export.Text("hash").ShouldBe(current);
        var document = JsonNode.Parse(export.Text("canonicalJson"))!;
        document.Text("hash").ShouldBe(current);
        document["values"]!.AsArray().Count.ShouldBeGreaterThan(0);
        export.Text("documentDigest").Length.ShouldBe(64);
    }

    [Fact]
    public async Task An_approved_change_is_explained_with_its_request_and_approver_and_compared_against_the_old_hash()
    {
        var before = await CurrentHashAsync();
        await ApplyChangeAsync("tax.ipt.rate.general", "0.16");
        var after = await CurrentHashAsync();
        after.ShouldNotBe(before);

        var (_, explained) = await AdminGetAsync("/explain?key=tax.ipt.rate.general&context=GR-TEST&validAt=2027-02-01");
        explained.Text("value").ShouldBe("0.16");
        explained.Text("packId").ShouldStartWith("cfg-overlay.");
        explained.Text("cr.requestedBy").ShouldContain("cfg-maker");
        explained["approvers"]!.AsArray().Select(a => (string?)a).ShouldContain(a => a != null && a.Contains("cfg-checker"));

        var (_, old) = await AdminGetAsync($"/explain?key=tax.ipt.rate.general&context=GR-TEST&validAt=2027-02-01&hash={before}");
        old.Text("value").ShouldBe("0.15");

        var (_, diff) = await AdminGetAsync($"/compare?hash={before}&hashB={after}&filter=tax.ipt.");
        var changes = diff["changes"]!.AsArray();
        changes.ShouldNotBeEmpty();
        changes.ShouldContain(c => c!.Text("key") == "tax.ipt.rate.general" && c.Text("after.value") == "0.16");

        var (_, byDate) = await AdminGetAsync("/compare?validAt=2026-12-31&validAtB=2027-02-01&filter=tax.ipt.rate.general");
        byDate["changes"]!.AsArray().Single()!.Text("change").ShouldBe("Changed");
    }
}
