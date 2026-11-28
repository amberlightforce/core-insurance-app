using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Migration.Contracts.Spi;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CoreIns.IntegrationTests.Migration;

/// <summary>
/// BO-MIG-12: rehearsal, cut-over and rollback plan. Steps run strictly in order and store their timings; the go/no-go step reads the sign-off status
/// (a cut-over refuses a no-go, a rehearsal records it); a rollback needs a dry run, reverses the batches last first through the batch reverse operation and
/// sets the CORE routing of the converted wave terms back to the legacy master. All data is synthetic; the module importer and reverser are test doubles.
/// </summary>
public sealed class CutoverRollbackTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Manager = "Platform.Admin,Staff.MigrationManager";
    private const string Steward = "Staff.DataSteward";
    private readonly Module _module = new();
    private ApiHostFactory? _root;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
            services.AddSingleton<IMigrationImporter>(_module);
            services.AddSingleton<IMigrationReverser>(_module);
            services.AddSingleton<IMigrationReconciliationReader>(_module);
        }));
        return _factory.CreateClient();
    }

    private static string Id() => Guid.NewGuid().ToString("N")[..10];

    private async Task<(string Wave, string Batch, string Prefix)> ConvertedWaveAsync(params string[] keys)
    {
        var prefix = Id() + "-";
        var records = new JsonArray();
        foreach (var key in keys)
        {
            records.Add(new JsonObject { ["objectType"] = "Policy", ["legacyKey"] = prefix + key, ["data"] = new JsonObject { ["number"] = prefix + key, ["endDate"] = "2027-05-10" } });
        }

        var source = "C" + Id()[..7];
        var label = "S-" + Id();
        var staged = await SendAsync(HttpMethod.Post, "/api/mig/v1/staging/load", new { source, snapshot = label, format = "JSON", content = new JsonObject { ["source"] = source, ["records"] = records }.ToJsonString() }, "alice", Manager);
        staged.Response.StatusCode.ShouldBe(HttpStatusCode.OK, staged.Body?.ToJsonString());
        var spec = new { name = "cut-policy-" + Id(), sourceObjectType = "Policy", targetModule = "CUT", targetObjectType = "Policy", fields = new object[] { new { target = "policy.number", source = "number", transform = "COPY", required = true } } };
        var created = await SendAsync(HttpMethod.Post, "/api/mig/v1/mapping/specs", spec, "mia", Manager);
        var specId = created.Body!["specId"]!.GetValue<string>();
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/mapping/publish", new { specId }, "mia", Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/mapping/publish", new { specId }, "sam", Steward)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var name = "cwave-" + Id();
        var planned = await SendAsync(HttpMethod.Post, "/api/mig/v1/waves/plan", new { name, source, snapshot = label, windowFrom = "2027-05-01", windowTo = "2027-05-31" }, "carol", Manager);
        planned.Response.StatusCode.ShouldBe(HttpStatusCode.OK, planned.Body?.ToJsonString());
        var converted = await SendAsync(HttpMethod.Post, "/api/mig/v1/waves/convert", new { waveId = planned.Body!["waveId"]!.GetValue<string>(), specId }, "carol", Manager);
        converted.Response.StatusCode.ShouldBe(HttpStatusCode.OK, converted.Body?.ToJsonString());
        return (name, converted.Body!["batches"]!.AsArray().Single()!["batchId"]!.GetValue<string>(), prefix);
    }

    private async Task RunBatchAsync(string batch)
    {
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId = batch, dryRun = true }, "alice", Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId = batch, dryRun = false }, "alice", Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var approved = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId = batch, decision = "APPROVE" }, "bob", Manager);
        approved.Response.StatusCode.ShouldBe(HttpStatusCode.OK, approved.Body?.ToJsonString());
    }

    private async Task<JsonNode> CutoverAsync(string op, object body, HttpStatusCode expect = HttpStatusCode.OK)
    {
        var (response, node) = await SendAsync(HttpMethod.Post, "/api/mig/v1/cutover/" + op, body, "carol", Manager);
        response.StatusCode.ShouldBe(expect, node?.ToJsonString());
        return node!;
    }

    private async Task<string> MasterAsync(string key, string date) =>
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/routing/resolve", new { objectType = "Policy", key, date }, "alice", Manager)).Body!["masterState"]!.GetValue<string>();

    private async Task SignOffAsync(string scope, string batch, int expected)
    {
        var totals = new[] { new { module = "CUT", objectType = "Policy", key = "", currency = "EUR", count = (long)expected, amount = "0.00" } };
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/reconciliation/control-totals/load", new { scope, totals }, "alice", Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _module.ReportedCount = expected;
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/reconciliation/run", new { scope, batchId = batch }, "alice", Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var requested = await SendAsync(HttpMethod.Post, "/api/mig/v1/signoff/request", new { scope }, "carol", Manager);
        requested.Response.StatusCode.ShouldBe(HttpStatusCode.OK, requested.Body?.ToJsonString());
        var signOff = requested.Body!["signoffId"]!.GetValue<string>();
        foreach (var (signatory, user, roles) in new[] { ("MIGRATION_LEAD", "bob", Manager), ("FINANCE_CONTROLLER", "dave", "Staff.Finance"), ("COMPLIANCE_OFFICER", "erin", "Staff.ComplianceOfficer") })
        {
            var decided = await SendAsync(HttpMethod.Post, "/api/mig/v1/signoff/approve", new { signoffId = signOff, signatory, comment = "checked" }, user, roles);
            decided.Response.StatusCode.ShouldBe(HttpStatusCode.OK, decided.Body?.ToJsonString());
        }
    }

    [Fact]
    public async Task Steps_run_in_order_store_timings_and_a_rehearsal_records_a_no_go_that_a_cutover_refuses()
    {
        var (scope, batch, _) = await ConvertedWaveAsync("A", "B");
        var rehearsal = "rehearsal-" + Id();
        var plan = await CutoverAsync("plan", new { name = rehearsal, kind = "REHEARSAL", scope, batchIds = new[] { batch } });
        var planId = plan["planId"]!.GetValue<string>();
        plan["steps"]!.AsArray().Select(s => s!["kind"]!.GetValue<string>()).ToArray().ShouldBe(["GO_NO_GO", "BATCH_RUN"]);

        // Idempotent by name; a different definition under the same name, an unknown batch, a bad kind and a missing role are refused.
        (await CutoverAsync("plan", new { name = rehearsal, kind = "REHEARSAL", scope, batchIds = new[] { batch } }))["planId"]!.GetValue<string>().ShouldBe(planId);
        await CutoverAsync("plan", new { name = rehearsal, kind = "CUTOVER", scope, batchIds = new[] { batch } }, HttpStatusCode.Conflict);
        await CutoverAsync("plan", new { name = "u-" + Id(), kind = "REHEARSAL", scope, batchIds = new[] { Guid.NewGuid() } }, HttpStatusCode.NotFound);
        await CutoverAsync("plan", new { name = "v-" + Id(), kind = "DRESS", scope, batchIds = new[] { batch } }, HttpStatusCode.UnprocessableContent);
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/cutover/plan", new { name = "w-" + Id(), kind = "REHEARSAL", scope, batchIds = new[] { batch } }, "frank", "Staff.Finance")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Order: step 2 cannot start before step 1 is done, and a step cannot complete before it starts.
        await CutoverAsync("start-step", new { planId, seq = 2 }, HttpStatusCode.Conflict);
        await CutoverAsync("complete-step", new { planId, seq = 1 }, HttpStatusCode.Conflict);
        await CutoverAsync("start-step", new { planId, seq = 1 });
        await CutoverAsync("start-step", new { planId, seq = 1 }, HttpStatusCode.Conflict);

        // No reconciliation or sign-off exists yet: the rehearsal records NO_GO with the blockers and carries on.
        var afterGo = await CutoverAsync("complete-step", new { planId, seq = 1 });
        var go = afterGo["steps"]!.AsArray()[0]!;
        go["status"]!.GetValue<string>().ShouldBe("DONE");
        go["result"]!.GetValue<string>().ShouldBe("NO_GO");
        go["note"]!.GetValue<string>().ShouldContain("NO_RUN");
        go["durationMs"]!.GetValue<long>().ShouldBeGreaterThanOrEqualTo(0);

        // The batch step needs its batch COMPLETED.
        await CutoverAsync("start-step", new { planId, seq = 2 });
        await CutoverAsync("complete-step", new { planId, seq = 2 }, HttpStatusCode.Conflict);
        await RunBatchAsync(batch);
        var done = await CutoverAsync("complete-step", new { planId, seq = 2, note = "synthetic rehearsal" });
        done["status"]!.GetValue<string>().ShouldBe("COMPLETED");
        done["steps"]!.AsArray().All(s => s!["status"]!.GetValue<string>() == "DONE" && s["startedAt"] is not null && s["completedAt"] is not null && s["durationMs"] is not null).ShouldBeTrue();
        await CutoverAsync("start-step", new { planId, seq = 2 }, HttpStatusCode.Conflict);

        // The timings are stored.
        (await Scalar($"SELECT count(*) FROM mig.cutover_step WHERE plan_id = '{planId}' AND started_at IS NOT NULL AND completed_at IS NOT NULL AND duration_ms >= 0")).ShouldBe(2);

        // A cut-over of the same scope is a no-go until the sign-off is ready, then the go/no-go passes.
        var cutover = await CutoverAsync("plan", new { name = "cutover-" + Id(), kind = "CUTOVER", scope, batchIds = new[] { batch } });
        var cutoverId = cutover["planId"]!.GetValue<string>();
        await CutoverAsync("start-step", new { planId = cutoverId, seq = 1 });
        (await CutoverAsync("complete-step", new { planId = cutoverId, seq = 1 }, HttpStatusCode.Conflict))["detail"]!.GetValue<string>().ShouldContain("No-go");
        await SignOffAsync(scope, batch, 2);
        var green = await CutoverAsync("complete-step", new { planId = cutoverId, seq = 1 });
        green["steps"]!.AsArray()[0]!["result"]!.GetValue<string>().ShouldBe("GO");
    }

    [Fact]
    public async Task Rollback_needs_a_dry_run_reverses_the_batch_and_restores_routing_and_a_blocked_reversal_keeps_it()
    {
        var (scope, batch, p) = await ConvertedWaveAsync("A", "B");
        await RunBatchAsync(batch);
        (await MasterAsync(p + "A", "2027-05-10")).ShouldBe("CoreMaster");
        var plan = await CutoverAsync("plan", new { name = "cutover-" + Id(), kind = "REHEARSAL", scope, batchIds = new[] { batch } });
        var planId = plan["planId"]!.GetValue<string>();

        // Nothing has run yet.
        await CutoverAsync("rollback", new { planId, dryRun = true }, HttpStatusCode.Conflict);
        await CutoverAsync("start-step", new { planId, seq = 1 });
        await CutoverAsync("complete-step", new { planId, seq = 1 });
        await CutoverAsync("start-step", new { planId, seq = 2 });
        await CutoverAsync("complete-step", new { planId, seq = 2 });

        // A real rollback needs the dry run first; the dry run changes nothing.
        await CutoverAsync("rollback", new { planId, dryRun = false }, HttpStatusCode.UnprocessableContent);
        var preview = await CutoverAsync("rollback", new { planId, dryRun = true });
        preview["status"]!.GetValue<string>().ShouldBe("COMPLETED");
        preview["rollbackPreviewedAt"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        var previewed = preview["rollback"]!.AsArray().Single()!;
        previewed["reversed"]!.GetValue<int>().ShouldBe(2);
        previewed["routingRestored"]!.GetValue<int>().ShouldBe(2);
        (await MasterAsync(p + "A", "2027-05-10")).ShouldBe("CoreMaster");
        _module.Reversed.ShouldBeEmpty();

        // The module blocks the reversal: the plan is ROLLBACK_BLOCKED and the routing stays on CORE.
        _module.BlockReversal = true;
        var blocked = await CutoverAsync("rollback", new { planId, dryRun = false });
        blocked["status"]!.GetValue<string>().ShouldBe("ROLLBACK_BLOCKED");
        blocked["rollback"]!.AsArray().Single()!["blocked"]!.GetValue<int>().ShouldBe(2);
        (await MasterAsync(p + "A", "2027-05-10")).ShouldBe("CoreMaster");

        // Once the module can reverse, the rollback completes: the batch is REVERSED, both policies route back to the legacy master and the terms are free again.
        _module.BlockReversal = false;
        var rolledBack = await CutoverAsync("rollback", new { planId, dryRun = false });
        rolledBack["status"]!.GetValue<string>().ShouldBe("ROLLED_BACK");
        rolledBack["rolledBackAt"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        var outcome = rolledBack["rollback"]!.AsArray().Single()!;
        outcome["status"]!.GetValue<string>().ShouldBe("REVERSED");
        outcome["routingRestored"]!.GetValue<int>().ShouldBe(2);
        rolledBack["steps"]!.AsArray()[1]!["status"]!.GetValue<string>().ShouldBe("ROLLED_BACK");
        _module.Reversed.Count.ShouldBe(2);
        (await MasterAsync(p + "A", "2027-05-10")).ShouldBe("LegacyMaster");
        (await MasterAsync(p + "B", "2027-06-01")).ShouldBe("LegacyMaster");
        (await Scalar($"SELECT count(*) FROM mig.wave_term WHERE batch_id = '{batch}' AND state = 'NOT_CONVERTED' AND core_id IS NULL")).ShouldBe(2);
        (await Scalar($"SELECT count(*) FROM mig.batch WHERE batch_id = '{batch}' AND status = 'REVERSED'")).ShouldBe(1);
        await CutoverAsync("rollback", new { planId, dryRun = false }, HttpStatusCode.Conflict);
    }

    private async Task<long> Scalar(string sql)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body, string user, string roles)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
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

        var response = await Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    /// <summary>Test double of the CUT module: imports policies, reports its count and reverses them unless <see cref="BlockReversal"/> is set.</summary>
    private sealed class Module : IMigrationImporter, IMigrationReverser, IMigrationReconciliationReader
    {
        private readonly ConcurrentDictionary<Guid, ConcurrentBag<Guid>> _byBatch = new();

        public ConcurrentBag<Guid> Reversed { get; } = [];

        public bool BlockReversal { get; set; }

        public int ReportedCount { get; set; }

        public string ModuleName => "CUT";

        public Task<ImportOutcome> ImportAsync(MigrationImportItem item, MigrationImportContext context, CancellationToken cancellationToken)
        {
            if (context.DryRun)
            {
                return Task.FromResult(new ImportOutcome(ImportStatus.Imported));
            }

            var id = Guid.CreateVersion7();
            _byBatch.GetOrAdd(context.BatchId, _ => []).Add(id);
            return Task.FromResult(new ImportOutcome(ImportStatus.Imported, id));
        }

        public Task<IReadOnlyList<ReversalOutcome>> ReverseAsync(Guid batchId, bool dryRun, CancellationToken cancellationToken)
        {
            var ids = _byBatch.TryGetValue(batchId, out var bag) ? bag.ToArray() : [];
            if (!dryRun && !BlockReversal)
            {
                foreach (var id in ids)
                {
                    Reversed.Add(id);
                }
            }

            return Task.FromResult<IReadOnlyList<ReversalOutcome>>([.. ids.Select(id => BlockReversal
                ? new ReversalOutcome("Policy", id, false, MigrationReversalCodes.Blocked, "The policy has activity after migration.")
                : new ReversalOutcome("Policy", id, true))]);
        }

        public Task<IReadOnlyList<ReconciliationFigure>> ReadAsync(Guid? batchId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReconciliationFigure>>([new ReconciliationFigure("Policy", string.Empty, "EUR", ReportedCount, 0m)]);
    }
}
