using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.SharedKernel.Ai;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CoreIns.IntegrationTests.Compliance;

/// <summary>SL6-CMP-AIREG main paths through the real Host: register lifecycle, four eyes, events, and the Platform gate via the real <c>IAiRegisterLookup</c>.</summary>
public sealed class AiRegisterTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Officer = "Staff.ComplianceOfficer";
    private const string Admin = "Platform.Admin";

    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;
    private IAiGate _gate = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString, settings: new Dictionary<string, string?> { ["Platform:Ai:CacheSeconds"] = "1" });
        _client = _factory.CreateClient();
        _gate = _factory.Services.GetRequiredService<IAiGate>();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static string NewFeature() => $"AI-TST-{Random.Shared.Next(10, 99)}{Random.Shared.Next(10, 99)}";

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(string path, object body, string roles, string user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(body);
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> GetAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, Officer);
        request.Headers.Add(TestAuthHandler.UserHeader, "dpo");
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<bool> WaitForGateAsync(string feature, bool allowed)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < until)
        {
            if (_gate.Check(feature).IsAllowed == allowed)
            {
                return true;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        return false;
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> EnableAsync(string feature) =>
        PostAsync("/api/plt/v1/ai-toggle/set", new { featureId = feature, enabled = true }, Admin, "ai-admin");

    [Fact]
    public async Task Lifecycle_enforces_four_eyes_emits_events_and_gates_the_platform_toggle()
    {
        var feature = NewFeature();

        var (registered, registeredBody) = await PostAsync("/api/cmp/v1/ai-systems/register", new { featureId = feature, name = "Test feature" }, Officer, "dpo");
        registered.StatusCode.ShouldBe(HttpStatusCode.OK, registeredBody?.ToJsonString());
        registeredBody!["status"]!.GetValue<string>().ShouldBe("DRAFT");

        // DRAFT: the real lookup finds the entry, but it is not APPROVED, so PLT-AI refuses to enable it.
        var (draft, draftBody) = await EnableAsync(feature);
        draft.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, draftBody?.ToJsonString());
        draftBody!["code"]?.GetValue<string>().ShouldBe("PLT-ERR-AI-NOT-REGISTERED");

        // Approving a draft (not yet in review) is refused.
        (await PostAsync("/api/cmp/v1/ai-systems/approve", new { featureId = feature }, Officer, "dpo2")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        (await PostAsync("/api/cmp/v1/ai-systems/submit-for-review", new { featureId = feature }, Officer, "dpo")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Four eyes: the submitter cannot approve.
        var (self, selfBody) = await PostAsync("/api/cmp/v1/ai-systems/approve", new { featureId = feature }, Officer, "dpo");
        self.StatusCode.ShouldBe(HttpStatusCode.Forbidden, selfBody?.ToJsonString());
        selfBody!["code"]?.GetValue<string>().ShouldBe("CMP-ERR-APPROVAL-SAME-USER");

        var (approved, approvedBody) = await PostAsync("/api/cmp/v1/ai-systems/approve", new { featureId = feature }, Officer, "dpo2");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, approvedBody?.ToJsonString());
        approvedBody!["status"]!.GetValue<string>().ShouldBe("APPROVED");
        approvedBody["approvedBy"]!.GetValue<string>().ShouldBe("dpo2");
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'AiSystemStatusChanged' AND payload->>'featureId' = '{feature}' AND payload->>'newStatus' = 'APPROVED'")).ShouldBe(1);

        // APPROVED: PLT-AI allows the toggle and the gate opens.
        var (enabled, enabledBody) = await EnableAsync(feature);
        enabled.StatusCode.ShouldBe(HttpStatusCode.OK, enabledBody?.ToJsonString());
        (await WaitForGateAsync(feature, true)).ShouldBeTrue();

        // Suspending emits the event (the worker delivers it to PLT's handler, covered by AiControlPlaneTests) and the register stops allowing enabling.
        var (suspended, suspendedBody) = await PostAsync("/api/cmp/v1/ai-systems/suspend", new { featureId = feature, reason = "audit finding" }, Officer, "dpo2");
        suspended.StatusCode.ShouldBe(HttpStatusCode.OK, suspendedBody?.ToJsonString());
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'AiSystemStatusChanged' AND payload->>'featureId' = '{feature}' AND payload->>'newStatus' = 'SUSPENDED'")).ShouldBe(1);
        (await EnableAsync(feature)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var (got, gotBody) = await GetAsync($"/api/cmp/v1/ai-systems/{feature}");
        got.StatusCode.ShouldBe(HttpStatusCode.OK);
        gotBody!["status"]!.GetValue<string>().ShouldBe("SUSPENDED");

        var (retired, _) = await PostAsync("/api/cmp/v1/ai-systems/retire", new { featureId = feature }, Officer, "dpo2");
        retired.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Pilot_features_are_seeded_as_draft_and_listed()
    {
        var (response, body) = await GetAsync("/api/cmp/v1/ai-systems?limit=200");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var items = body!["items"]!.AsArray();
        var clm = items.Single(i => i!["featureId"]!.GetValue<string>() == "AI-CLM-01")!;
        clm["status"]!.GetValue<string>().ShouldBe("DRAFT");

        (await GetAsync("/api/cmp/v1/ai-systems/AI-XXX-99")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
