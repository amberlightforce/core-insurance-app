using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Platform.Ai;
using CoreIns.Platform.Context;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Ai;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CoreIns.IntegrationTests.Platform;

/// <summary>SL6-PLT-AI main paths on PostgreSQL through the real Host: registry-gated toggles, kill switch, event consumers, in-flight disable.</summary>
public sealed class AiControlPlaneTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Admin = "Platform.Admin";
    private static readonly ConcurrentDictionary<string, string> RegisterStatuses = new(StringComparer.Ordinal);

    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;
    private IAiGate _gate = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString, settings: new Dictionary<string, string?> { ["Platform:Ai:CacheSeconds"] = "1" });
        var configured = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddScoped<IAiRegisterLookup, TestRegister>();
            services.AddScoped<IAiProvider, KillingProvider>();
        }));
        _client = configured.CreateClient();
        _gate = configured.Services.GetRequiredService<IAiGate>();
        _scopes = configured;
        return ValueTask.CompletedTask;
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _scopes = null!;

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _scopes.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private static string NewFeature() => $"AI-TST-{Random.Shared.Next(10, 99)}{Random.Shared.Next(10, 99)}";

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(string path, object body, string roles = Admin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, "ai-admin");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(body);
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task ExecAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task EnableAsync(string feature)
    {
        RegisterStatuses[feature] = "APPROVED";
        var (response, body) = await PostAsync("/api/plt/v1/ai-toggle/set", new { featureId = feature, enabled = true });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
    }

    private async Task<bool> WaitForGateAsync(string feature, bool allowed, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
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

    [Fact]
    public async Task Enabling_an_unapproved_feature_is_refused_and_an_approved_one_opens_the_gate()
    {
        var feature = NewFeature();
        _gate.Check(feature).IsAllowed.ShouldBeFalse();

        var (refused, refusedBody) = await PostAsync("/api/plt/v1/ai-toggle/set", new { featureId = feature, enabled = true });
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, refusedBody?.ToJsonString());
        refusedBody!["code"]?.GetValue<string>().ShouldBe("PLT-ERR-AI-NOT-REGISTERED");
        _gate.Check(feature).IsAllowed.ShouldBeFalse();

        RegisterStatuses[feature] = "DRAFT";
        (await PostAsync("/api/plt/v1/ai-toggle/set", new { featureId = feature, enabled = true })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        await EnableAsync(feature);
        (await WaitForGateAsync(feature, true, TimeSpan.FromSeconds(8))).ShouldBeTrue();
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'AiToggleChanged' AND payload->>'featureId' = '{feature}' AND payload->>'newState' = 'ON'"))
            .ShouldBe(1);

        var (resolve, resolveBody) = await PostAsync("/api/plt/v1/ai-toggle/resolve", new { featureId = feature });
        resolve.StatusCode.ShouldBe(HttpStatusCode.OK, resolveBody?.ToJsonString());
        resolveBody!["allowed"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task The_global_kill_switch_closes_every_gate_within_the_cache_ttl_and_clearing_reopens_it()
    {
        var a = NewFeature();
        var b = NewFeature();
        await EnableAsync(a);
        await EnableAsync(b);
        (await WaitForGateAsync(a, true, TimeSpan.FromSeconds(8))).ShouldBeTrue();

        var (on, onBody) = await PostAsync("/api/plt/v1/ai/kill-switch", new { action = "Activate", scope = "GLOBAL", reason = "test" });
        on.StatusCode.ShouldBe(HttpStatusCode.OK, onBody?.ToJsonString());
        (await WaitForGateAsync(a, false, TimeSpan.FromSeconds(5))).ShouldBeTrue();
        (await WaitForGateAsync(b, false, TimeSpan.FromSeconds(5))).ShouldBeTrue();
        _gate.Check(a).Reason.ShouldBe(AiGateReasons.KillSwitch);
        (await ScalarAsync<long>("SELECT count(*) FROM plt.outbox_message WHERE event_type = 'AiKillSwitchActivated' AND payload->>'scope' = 'GLOBAL'")).ShouldBeGreaterThan(0);

        // A change made behind this process back (another api or worker instance) is seen within the TTL, without the in-process invalidation.
        await ExecAsync("UPDATE plt.ai_kill_switch SET active = false WHERE scope = 'GLOBAL'");
        (await WaitForGateAsync(a, true, TimeSpan.FromSeconds(5))).ShouldBeTrue();

        // An unreadable scope is a validation error, not a switch.
        (await PostAsync("/api/plt/v1/ai/kill-switch", new { action = "Activate", scope = "everything" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        // Only administrators operate it.
        (await PostAsync("/api/plt/v1/ai/kill-switch", new { action = "Activate", scope = "GLOBAL" }, "Staff.ClaimsHandler")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Register_and_bias_events_disable_that_feature_only()
    {
        var a = NewFeature();
        var b = NewFeature();
        var c = NewFeature();
        await EnableAsync(a);
        await EnableAsync(b);
        await EnableAsync(c);

        await using (var scope = Scope())
        {
            var session = scope.ServiceProvider.GetRequiredService<DbSession>();
            var tx = await session.BeginTransactionAsync(TestContext.Current.CancellationToken);
            var ct = TestContext.Current.CancellationToken;
            var status = scope.ServiceProvider.GetRequiredService<AiSystemStatusChangedHandler>();
            await status.HandleAsync(null!, new AiSystemStatusPayload(a, "SUSPENDED"), ct);
            await status.HandleAsync(null!, new AiSystemStatusPayload(c, "APPROVED"), ct);
            await scope.ServiceProvider.GetRequiredService<BiasThresholdBreachedHandler>().HandleAsync(null!, new BiasBreachPayload(b), ct);
            await tx.CommitAsync(TestContext.Current.CancellationToken);
        }

        (await WaitForGateAsync(a, false, TimeSpan.FromSeconds(5))).ShouldBeTrue();
        (await WaitForGateAsync(b, false, TimeSpan.FromSeconds(5))).ShouldBeTrue();
        (await WaitForGateAsync(c, true, TimeSpan.FromSeconds(5))).ShouldBeTrue();
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = 'plt.AiToggle.set' AND object_id IN ('{a}', '{b}') AND actor_id = 'platform-ai'")).ShouldBe(2);
        (await ScalarAsync<string>($"SELECT payload->>'reason' FROM plt.outbox_message WHERE event_type = 'AiToggleChanged' AND payload->>'featureId' = '{a}' AND payload->>'newState' = 'OFF'"))
            .ShouldBe("REGISTER_STATUS");
    }

    [Fact]
    public async Task Invoke_returns_a_deterministic_suggestion_and_records_the_human_outcome()
    {
        var feature = NewFeature();
        await EnableAsync(feature);
        (await WaitForGateAsync(feature, true, TimeSpan.FromSeconds(8))).ShouldBeTrue();

        var (response, body) = await PostAsync("/api/plt/v1/ai/invoke", new { featureId = feature, inputs = new { text = "hello" } });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["outcome"]!.GetValue<string>().ShouldBe("Suggested");
        var id = body["interactionId"]!.GetValue<string>();
        var (_, again) = await PostAsync("/api/plt/v1/ai/invoke", new { featureId = feature, inputs = new { text = "hello" } });
        again!["output"]!["inputDigest"]!.GetValue<string>().ShouldBe(body["output"]!["inputDigest"]!.GetValue<string>());

        var (recorded, recordedBody) = await PostAsync("/api/plt/v1/ai/record-outcome", new { interactionId = id, outcome = "Accepted" });
        recorded.StatusCode.ShouldBe(HttpStatusCode.OK, recordedBody?.ToJsonString());
        (await ScalarAsync<string>($"SELECT outcome FROM plt.ai_interaction WHERE interaction_id = '{id}'")).ShouldBe("Accepted");
        (await PostAsync("/api/plt/v1/ai/record-outcome", new { interactionId = id, outcome = "Rejected" })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_gate_that_closes_during_the_provider_call_discards_the_output_and_returns_503_with_the_inputs_kept()
    {
        var feature = NewFeature();
        await EnableAsync(feature);
        (await WaitForGateAsync(feature, true, TimeSpan.FromSeconds(8))).ShouldBeTrue();
        KillingProvider.KillSwitchSql = $"UPDATE plt.ai_toggle SET enabled = false WHERE feature_id = '{feature}'";
        KillingProvider.ConnectionString = database.SuperuserConnectionString;
        try
        {
            var (response, body) = await PostAsync("/api/plt/v1/ai/invoke", new { featureId = feature, inputs = new { text = "keep-me" } });
            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, body?.ToJsonString());
            body!["code"]?.GetValue<string>().ShouldBe("PLT-ERR-AI-DISABLED");
            body.ToJsonString().ShouldNotContain("DETERMINISTIC_FAKE");
        }
        finally
        {
            KillingProvider.KillSwitchSql = null;
        }

        (await ScalarAsync<string>($"SELECT status FROM plt.ai_interaction WHERE feature_id = '{feature}'")).ShouldBe("Discarded");
        (await ScalarAsync<string>($"SELECT inputs->>'text' FROM plt.ai_interaction WHERE feature_id = '{feature}'")).ShouldBe("keep-me");
        (await ScalarAsync<bool>($"SELECT output IS NULL FROM plt.ai_interaction WHERE feature_id = '{feature}'")).ShouldBeTrue();
    }

    private AsyncServiceScope Scope()
    {
        var scope = _scopes.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.Service("test");
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.ConfigurationHash = ConfigurationHash.Parse(new string('a', 64));
        return scope;
    }

    private sealed class TestRegister : IAiRegisterLookup
    {
        public Task<string?> GetStatusAsync(string featureId, CancellationToken cancellationToken) =>
            Task.FromResult(RegisterStatuses.TryGetValue(featureId, out var status) ? status : null);
    }

    /// <summary>The deterministic fake, except that it can change the database state while the "call" is in flight.</summary>
    private sealed class KillingProvider : IAiProvider
    {
        public static string? KillSwitchSql { get; set; }

        public static string ConnectionString { get; set; } = string.Empty;

        public async Task<JsonObject> InvokeAsync(string featureId, JsonObject inputs, CancellationToken cancellationToken)
        {
            if (KillSwitchSql is { } sql)
            {
                await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
                await using var command = dataSource.CreateCommand(sql);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            return new JsonObject { ["suggestion"] = "DETERMINISTIC_FAKE", ["inputDigest"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(inputs.ToJsonString()))) };
        }
    }
}
