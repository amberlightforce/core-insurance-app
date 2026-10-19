using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Migration.Contracts.Spi;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Migration;

/// <summary>SL6-MIG-FRAME: dry run, idempotent import through a stub importer, maker-checker, routing overlap, reverse (D-SL6-05).</summary>
public sealed class MigrationFrameTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Roles = "Platform.Admin,Staff.MigrationManager";
    private readonly StubModule _pty = new("PTY");
    private readonly StubModule _uw = new("UW");
    private ApiHostFactory? _root;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    private HttpClient Client => _client ??= Create();

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_root is not null)
        {
            await _root.DisposeAsync();
        }
    }

    private HttpClient Create()
    {
        _root = new ApiHostFactory(database.AppConnectionString);
        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // The PTY stub can also reverse; the UW stub only imports (no reverser).
            services.AddSingleton<IMigrationImporter>(_pty);
            services.AddSingleton<IMigrationReverser>(_pty);
            services.AddSingleton<IMigrationImporter>(_uw);
        }));
        return _factory.CreateClient();
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body, string user)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, Roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.AcceptLanguage.ParseAdd("en");
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static object Item(string module, string legacy, string? flag = null) =>
        new { module, objectType = "Party", legacyId = legacy, payload = new { name = "Legacy " + legacy, flag } };

    private async Task<string> CreateBatchAsync(params object[] items)
    {
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches", new { name = "wave 1", items }, "alice");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body.Text("batch.batchId");
    }

    private async Task<JsonNode?> StartAsync(string batchId, bool dryRun, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId, dryRun }, "alice");
        response.StatusCode.ShouldBe(expected, body?.ToJsonString());
        return body;
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> ApproveAsync(string batchId, string user) =>
        await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId, decision = "APPROVE" }, user);

    private static string[] Outcomes(JsonNode? body) => [.. body!["outcomes"]!.AsArray().Select(o => o.Text("outcome"))];

    private async Task<string> RunAsync(string batchId)
    {
        await StartAsync(batchId, dryRun: true);
        await StartAsync(batchId, dryRun: false);
        var (response, body) = await ApproveAsync(batchId, "bob");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return batchId;
    }

    [Fact]
    public async Task A_dry_run_writes_nothing_and_a_real_run_twice_skips_existing_the_second_time()
    {
        var batch = await CreateBatchAsync(Item("PTY", $"L-{Guid.NewGuid():N}-1"), Item("PTY", $"L-{Guid.NewGuid():N}-2"));

        // A real run needs a prior dry run.
        (await StartAsync(batch, dryRun: false, HttpStatusCode.UnprocessableEntity)).Text("code").ShouldContain("DRYRUN-REQUIRED");

        var dry = await StartAsync(batch, dryRun: true);
        Outcomes(dry).ShouldBe(["IMPORTED", "IMPORTED"]);
        _pty.Imported.Count.ShouldBe(0);
        _pty.DryRuns.ShouldBe(2);
        (await SendAsync(HttpMethod.Get, $"/api/mig/v1/xrefs?batchId={batch}", null, "alice")).Body!["items"]!.AsArray().Count.ShouldBe(0);

        // The real run waits for a different user: the starter cannot approve it.
        var pending = await StartAsync(batch, dryRun: false);
        pending.Text("batch.status").ShouldBe("PENDING_APPROVAL");
        (await ApproveAsync(batch, "alice")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _pty.Imported.Count.ShouldBe(0);

        var (approved, done) = await ApproveAsync(batch, "bob");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, done?.ToJsonString());
        done.Text("batch.status").ShouldBe("COMPLETED");
        Outcomes(done).ShouldBe(["IMPORTED", "IMPORTED"]);
        _pty.Imported.Count.ShouldBe(2);

        // Same legacy ids again: every item is SKIPPED_EXISTING and the importer is not called.
        var items = (await SendAsync(HttpMethod.Get, $"/api/mig/v1/batches/outcomes?batchId={batch}", null, "alice")).Body!["items"]!.AsArray();
        var again = await CreateBatchAsync([.. items.Select(i => Item("PTY", i.Text("legacyId")))]);
        await StartAsync(again, dryRun: true);
        await StartAsync(again, dryRun: false);
        var second = (await ApproveAsync(again, "bob")).Body;
        Outcomes(second).ShouldBe(["SKIPPED_EXISTING", "SKIPPED_EXISTING"]);
        _pty.Imported.Count.ShouldBe(2);

        var resolved = await SendAsync(HttpMethod.Post, "/api/mig/v1/xrefs/resolve", new { objectType = "Party", source = "PTY", legacyKey = items[0].Text("legacyId") }, "alice");
        resolved.Body.Text("coreId").ShouldBe(items[0].Text("coreId"));
    }

    [Fact]
    public async Task Overlapping_routing_periods_are_rejected_and_resolve_returns_one_master()
    {
        var key = $"POL-{Guid.NewGuid():N}";
        object Override(string state, string from, string? to) => new { objectType = "Policy", key, masterState = state, from, to, reason = "wave 1 cut-over" };

        (await SendAsync(HttpMethod.Post, "/api/mig/v1/routing/override", Override("TRANSITIONING", "2026-01-01", "2026-06-01"), "alice")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var overlap = await SendAsync(HttpMethod.Post, "/api/mig/v1/routing/override", Override("CORE", "2026-03-01", null), "alice");
        overlap.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        overlap.Body.Text("code").ShouldContain("ROUTING-OVERLAP");
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/routing/override", Override("CORE", "2026-06-01", null), "alice")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        async Task<string> MasterAsync(string routeKey, string date) =>
            (await SendAsync(HttpMethod.Post, "/api/mig/v1/routing/resolve", new { objectType = "Policy", key = routeKey, date }, "alice")).Body.Text("masterState");

        (await MasterAsync(key, "2025-12-31")).ShouldBe("LegacyMaster");
        (await MasterAsync(key, "2026-02-01")).ShouldBe("Transitioning");
        (await MasterAsync(key, "2026-06-01")).ShouldBe("CoreMaster");

        // A change inside the open-ended last period ends it where the new one starts.
        var open = $"POL-{Guid.NewGuid():N}";
        await SendAsync(HttpMethod.Post, "/api/mig/v1/routing/override", new { objectType = "Policy", key = open, masterState = "TRANSITIONING", from = "2026-01-01", reason = "start" }, "alice");
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/routing/override", new { objectType = "Policy", key = open, masterState = "CORE", from = "2026-03-01", reason = "switch" }, "alice"))
            .Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await MasterAsync(open, "2026-02-28")).ShouldBe("Transitioning");
        (await MasterAsync(open, "2026-03-01")).ShouldBe("CoreMaster");
    }

    [Fact]
    public async Task Reverse_refuses_objects_with_activity_per_object_and_a_module_without_reverser_makes_the_batch_not_reversible()
    {
        var batch = await RunAsync(await CreateBatchAsync(Item("PTY", $"R-{Guid.NewGuid():N}-1"), Item("PTY", $"R-{Guid.NewGuid():N}-2", flag: "ACTIVITY")));

        var (response, reversed) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/reverse", new { batchId = batch, dryRun = false }, "alice");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, reversed?.ToJsonString());
        reversed.Text("batch.status").ShouldBe("REVERSAL_BLOCKED");
        Outcomes(reversed).ShouldBe(["REVERSED", "REVERSAL_BLOCKED"]);
        reversed!["outcomes"]![1].Text("errorCode").ShouldBe("MIG-ERR-REVERSAL-BLOCKED");

        var uwBatch = await RunAsync(await CreateBatchAsync(Item("UW", $"U-{Guid.NewGuid():N}")));
        var (_, notReversible) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/reverse", new { batchId = uwBatch, dryRun = false }, "alice");
        notReversible.Text("batch.status").ShouldBe("NOT_REVERSIBLE");
    }

    /// <summary>A stub module: imports into memory, reverses everything except objects flagged ACTIVITY.</summary>
    private sealed class StubModule(string module) : IMigrationImporter, IMigrationReverser
    {
        public ConcurrentDictionary<Guid, (Guid Batch, bool Touched)> Imported { get; } = new();

        public int DryRuns { get; private set; }

        public string ModuleName => module;

        public Task<ImportOutcome> ImportAsync(MigrationImportItem item, MigrationImportContext context, CancellationToken cancellationToken)
        {
            if (context.DryRun)
            {
                DryRuns++;
                return Task.FromResult(new ImportOutcome(ImportStatus.Imported));
            }

            var id = Guid.CreateVersion7();
            var touched = item.Payload.TryGetProperty("flag", out var flag) && flag.ValueKind == JsonValueKind.String && flag.GetString() == "ACTIVITY";
            Imported[id] = (context.BatchId, touched);
            return Task.FromResult(new ImportOutcome(ImportStatus.Imported, id));
        }

        public Task<IReadOnlyList<ReversalOutcome>> ReverseAsync(Guid batchId, bool dryRun, CancellationToken cancellationToken)
        {
            IReadOnlyList<ReversalOutcome> result =
            [
                .. Imported.Where(i => i.Value.Batch == batchId).Select(i => i.Value.Touched
                    ? new ReversalOutcome("Party", i.Key, false, MigrationReversalCodes.Blocked, "Post-migration activity.")
                    : new ReversalOutcome("Party", i.Key, true)),
            ];
            return Task.FromResult(result);
        }
    }
}
