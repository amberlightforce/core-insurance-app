using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Platform.Ai;
using CoreIns.SharedKernel.Ai;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Platform;

/// <summary>
/// SL6-AI-FEATURES main path through the real Host: the five AI-assisted endpoints refuse with 503 while their feature is off,
/// answer with a suggestion plus interaction id once it is approved and on, refuse again after the kill switch, and a pending
/// CHN ticket stays decidable by a human the whole time.
/// </summary>
public sealed class AiFeaturesTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private static readonly string[] Features = ["AI-CLM-01", "AI-PTY-01", "AI-BIL-01", "AI-UW-01", "AI-CHN-02"];
    private static readonly ConcurrentDictionary<string, string> Register = new(StringComparer.Ordinal);

    private readonly ApiHostFactory _root = new(database.AppConnectionString, settings: new Dictionary<string, string?> { ["Platform:Ai:CacheSeconds"] = "1" });
    private HttpClient? _client;
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? _configured;

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_configured is not null)
        {
            await _configured.DisposeAsync();
        }

        await _root.DisposeAsync();
    }

    private sealed class ApprovingRegister : IAiRegisterLookup
    {
        public Task<string?> GetStatusAsync(string featureId, CancellationToken cancellationToken) =>
            Task.FromResult(Register.TryGetValue(featureId, out var status) ? status : null);
    }

    private HttpClient Client()
    {
        _configured ??= _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddScoped<IAiRegisterLookup, ApprovingRegister>()));
        return _client ??= _configured.CreateClient();
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(string path, object body) => SendAsync(Client(), HttpMethod.Post, path, body, roles: Admin);

    private async Task WaitForGateAsync(bool allowed)
    {
        var gate = _configured!.Services.GetRequiredService<IAiGate>();
        var until = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < until && Features.Any(f => gate.Check(f).IsAllowed != allowed))
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        Features.All(f => gate.Check(f).IsAllowed == allowed).ShouldBeTrue();
    }

    private static readonly object FnolCall = new { text = "Rear-end collision on 2026-10-03, plate ΥΝΖ 4821, no injuries." };
    private static readonly object PtyCall = new
    {
        a = new { name = "Maria Papadopoulou", identifier = "123456789", birthDate = "1980-05-17" },
        b = new { name = "maria papadopoulou", identifier = "123456789", birthDate = "1980-05-17" },
    };
    private static readonly object BilCall = new
    {
        payment = new { amount = 120.50m, reference = "INV-0042", payerName = "Maria" },
        candidates = new[] { new { invoiceNo = "INV-0041", openAmount = 99m }, new { invoiceNo = "INV-0042", openAmount = 120.50m } },
    };
    private static readonly object UwCall = new { referralId = "R-1", reasons = new[] { "Sum insured above authority" }, sumInsured = 750000m, product = "PROPERTY" };

    private static object ToolsCall(string tool) => new { jsonrpc = "2.0", id = 7, method = "tools/call", @params = new { name = tool, arguments = new { policyId = Guid.NewGuid() } } };

    private async Task AssertAllRefusedAsync()
    {
        foreach (var (path, body) in new (string, object)[]
        {
            ("/api/clm/v1/fnol/assist", FnolCall), ("/api/pty/v1/parties/explain-duplicate", PtyCall), ("/api/bil/v1/suspense/suggest-match", BilCall),
            ("/api/uw/v1/referrals/summarize", UwCall), ("/api/chn/v1/agent/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" }),
        })
        {
            var (response, problem) = await PostAsync(path, body);
            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, $"{path} {problem?.ToJsonString()}");
            problem.Text("code").ShouldBe("PLT-ERR-AI-DISABLED");
        }
    }

    [Fact]
    public async Task Each_feature_is_gated_suggests_when_on_refuses_after_the_kill_switch_and_a_pending_ticket_stays_human_decidable()
    {
        // 1. Off by default: 503 neutral refusal on all five.
        await AssertAllRefusedAsync();

        // 2. Approve in the (test) register and switch on.
        foreach (var feature in Features)
        {
            Register[feature] = "APPROVED";
            var (response, body) = await PostAsync("/api/plt/v1/ai-toggle/set", new { featureId = feature, enabled = true });
            response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        }

        await WaitForGateAsync(true);

        // 3. Allowed: a suggestion and an interaction id each.
        var (r1, fnol) = await PostAsync("/api/clm/v1/fnol/assist", FnolCall);
        r1.StatusCode.ShouldBe(HttpStatusCode.OK, fnol?.ToJsonString());
        fnol.Text("suggestion.cause").ShouldBe("COLLISION");
        fnol.Text("suggestion.lossDate").ShouldBe("2026-10-03");
        Guid.TryParse(fnol.Text("interactionId"), out _).ShouldBeTrue();

        var (r2, pty) = await PostAsync("/api/pty/v1/parties/explain-duplicate", PtyCall);
        r2.StatusCode.ShouldBe(HttpStatusCode.OK, pty?.ToJsonString());
        pty.Text("suggestion.likelihood").ShouldBe("LIKELY_DUPLICATE");
        Guid.TryParse(pty.Text("interactionId"), out _).ShouldBeTrue();

        var (r3, bil) = await PostAsync("/api/bil/v1/suspense/suggest-match", BilCall);
        r3.StatusCode.ShouldBe(HttpStatusCode.OK, bil?.ToJsonString());
        bil.Text("suggestion.matches.0.invoiceNo").ShouldBe("INV-0042");

        var (r4, uw) = await PostAsync("/api/uw/v1/referrals/summarize", UwCall);
        r4.StatusCode.ShouldBe(HttpStatusCode.OK, uw?.ToJsonString());
        uw.Text("suggestion.summary").ShouldContain("Sum insured above authority");

        var (r5, list) = await PostAsync("/api/chn/v1/agent/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        r5.StatusCode.ShouldBe(HttpStatusCode.OK, list?.ToJsonString());
        list!["result"]!["tools"]!.AsArray().Count.ShouldBeGreaterThan(0);

        // 4. A state-changing tool raises a PENDING ticket; nothing else happens.
        var (r6, call) = await PostAsync("/api/chn/v1/agent/mcp", ToolsCall("bind_policy"));
        r6.StatusCode.ShouldBe(HttpStatusCode.OK, call?.ToJsonString());
        var ticketId = call.Text("result.ticket.ticketId");
        call.Text("result.ticket.status").ShouldBe("PENDING");

        // 5. Kill switch: all five refuse again, with no state change.
        var (killed, killBody) = await PostAsync("/api/plt/v1/ai/kill-switch", new { action = "Activate", scope = "GLOBAL", reason = "test" });
        killed.StatusCode.ShouldBe(HttpStatusCode.OK, killBody?.ToJsonString());
        await WaitForGateAsync(false);
        await AssertAllRefusedAsync();

        // 6. The manual path is untouched: a human reads and approves the pending ticket while AI is off.
        var (got, ticket) = await SendAsync(Client(), HttpMethod.Get, $"/api/chn/v1/tickets/{ticketId}", roles: Admin);
        got.StatusCode.ShouldBe(HttpStatusCode.OK, ticket?.ToJsonString());
        ticket.Text("status").ShouldBe("PENDING");

        var (approved, decided) = await PostAsync("/api/chn/v1/tickets/approve", new { ticketId });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, decided?.ToJsonString());
        decided.Text("status").ShouldBe("APPROVED");

        var (stale, staleBody) = await PostAsync("/api/chn/v1/tickets/reject", new { ticketId });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, staleBody?.ToJsonString());

        var (again, _) = await PostAsync("/api/chn/v1/tickets/approve", new { ticketId });
        again.StatusCode.ShouldBe(HttpStatusCode.OK); // repeating the same decision is idempotent
    }
}
