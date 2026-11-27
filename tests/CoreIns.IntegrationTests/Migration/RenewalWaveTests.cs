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
/// BO-MIG-07: renewal-based conversion waves. A wave selects the staged policies whose legacy term ends in a window, converts them through an approved
/// mapping and a maker-checker batch, flips routing to CORE with the import, keeps excluded and failed terms on legacy routing, never converts a policy twice
/// and closes only when the count equation holds and the sign-off is ready. All data is synthetic; the importer is a test double.
/// </summary>
public sealed class RenewalWaveTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Manager = "Platform.Admin,Staff.MigrationManager";
    private const string Steward = "Staff.DataSteward";
    private readonly WaveImporter _importer = new();
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
            services.AddSingleton<IMigrationImporter>(_importer);
            services.AddSingleton<IMigrationReconciliationReader>(_importer);
        }));
        return _factory.CreateClient();
    }

    private static readonly string[] AllStates = ["PLANNED", "OFFERED", "CONVERTED", "EXCLUDED", "WITHDRAWN", "NOT_CONVERTED"];

    private static string Id() => Guid.NewGuid().ToString("N")[..10];

    private static JsonObject Policy(string number, string? endDate) => new() { ["number"] = number, ["endDate"] = endDate };

    private async Task<(string Source, string Label)> StageAsync(string prefix, params (string Key, string? EndDate)[] policies)
    {
        var records = new JsonArray();
        foreach (var (key, endDate) in policies)
        {
            records.Add(new JsonObject { ["objectType"] = "Policy", ["legacyKey"] = prefix + key, ["data"] = Policy(prefix + key, endDate) });
        }

        var source = "W" + Id()[..7];
        var label = "S-" + Id();
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/staging/load", new { source, snapshot = label, format = "JSON", content = new JsonObject { ["source"] = source, ["records"] = records }.ToJsonString() }, "alice", Manager);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return (source, label);
    }

    private async Task<string> ApprovedSpecAsync()
    {
        var spec = new
        {
            name = "wave-policy-" + Id(),
            sourceObjectType = "Policy",
            targetModule = "WVT",
            targetObjectType = "Policy",
            fields = new object[]
            {
                new { target = "policy.number", source = "number", transform = "COPY", required = true },
                new { target = "policy.endDate", source = "endDate", transform = "COPY" },
            },
        };
        var (created, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/mapping/specs", spec, "mia", Manager);
        created.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var id = body!["specId"]!.GetValue<string>();
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/mapping/publish", new { specId = id }, "mia", Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (approved, result) = await SendAsync(HttpMethod.Post, "/api/mig/v1/mapping/publish", new { specId = id }, "sam", Steward);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, result?.ToJsonString());
        return id;
    }

    private async Task<JsonNode> WaveAsync(string path, object? body, HttpMethod? method = null, HttpStatusCode expect = HttpStatusCode.OK, string roles = Manager)
    {
        var (response, node) = await SendAsync(method ?? HttpMethod.Post, path, body, "carol", roles);
        response.StatusCode.ShouldBe(expect, node?.ToJsonString());
        return node!;
    }

    private static Dictionary<string, string> States(JsonNode wave) => wave["terms"]!.AsArray().OrderBy(t => t!["legacyKey"]!.GetValue<string>(), StringComparer.Ordinal).ToDictionary(t => t!["legacyKey"]!.GetValue<string>(), t => t!["state"]!.GetValue<string>());

    private static int Count(JsonNode wave, string state) => wave["counts"]![state]!.GetValue<int>();

    private async Task<string> MasterAsync(string key, string date) =>
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/routing/resolve", new { objectType = "Policy", key, date }, "alice", Manager)).Body!["masterState"]!.GetValue<string>();

    private async Task RunBatchAsync(string batch)
    {
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId = batch, dryRun = true }, "alice", Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId = batch, dryRun = false }, "alice", Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (approved, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId = batch, decision = "APPROVE" }, "bob", Manager);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
    }

    [Fact]
    public async Task A_wave_selects_by_window_converts_flips_routing_with_the_import_and_closes_on_the_equation_and_sign_off()
    {
        var p = Id() + "-";
        var (source, label) = await StageAsync(
            p, ("P1", "2027-01-10"), ("P2", "2027-01-20"), ("P3", "2027-01-31"), ("P4", "2027-02-05"), ("P5", "2027-01-15"), ("P6", "2027-01-25"), ("P7", null), ("P8", "not a date"));
        var spec = await ApprovedSpecAsync();
        var name = "wave-" + Id();

        // The window picks P1, P2, P3, P5, P6 only: P4 renews later, P7 and P8 have no usable term end.
        var wave = await WaveAsync("/api/mig/v1/waves/plan", new { name, source, snapshot = label, windowFrom = "2027-01-01", windowTo = "2027-01-31" });
        wave["selectedCount"]!.GetValue<int>().ShouldBe(5);
        wave["added"]!.GetValue<int>().ShouldBe(5);
        wave["equationHolds"]!.GetValue<bool>().ShouldBeTrue();
        Count(wave, "PLANNED").ShouldBe(5);
        States(wave).Keys.Order().ToArray().ShouldBe([p + "P1", p + "P2", p + "P3", p + "P5", p + "P6"]);
        var waveId = wave["waveId"]!.GetValue<string>();

        // Planning again is idempotent; another wave over the same window leaves the claimed policies alone, and one over February finds P4.
        (await WaveAsync("/api/mig/v1/waves/plan", new { name, source, snapshot = label, windowFrom = "2027-01-01", windowTo = "2027-01-31" }))["added"]!.GetValue<int>().ShouldBe(0);
        await WaveAsync("/api/mig/v1/waves/plan", new { name, source, snapshot = label, windowFrom = "2027-01-01", windowTo = "2027-02-28" }, expect: HttpStatusCode.Conflict);
        var other = await WaveAsync("/api/mig/v1/waves/plan", new { name = name + "-b", source, snapshot = label, windowFrom = "2027-01-01", windowTo = "2027-01-31" });
        other["selectedCount"]!.GetValue<int>().ShouldBe(0);
        var feb = await WaveAsync("/api/mig/v1/waves/plan", new { name = name + "-feb", source, snapshot = label, windowFrom = "2027-02-01", windowTo = "2027-02-28" });
        States(feb).ShouldBe(new Dictionary<string, string> { [p + "P4"] = "PLANNED" });

        // An unapproved spec cannot convert; an unknown one is not found.
        var draft = await SendAsync(HttpMethod.Post, "/api/mig/v1/mapping/specs", new { name = "draft-" + Id(), sourceObjectType = "Policy", targetModule = "WVT", targetObjectType = "Policy", fields = new object[] { new { target = "policy.number", source = "number", transform = "COPY" } } }, "mia", Manager);
        await WaveAsync("/api/mig/v1/waves/convert", new { waveId, specId = draft.Body!["specId"]!.GetValue<string>() }, expect: HttpStatusCode.Conflict);
        await WaveAsync("/api/mig/v1/waves/convert", new { waveId, specId = Guid.NewGuid() }, expect: HttpStatusCode.NotFound);

        // P5 is excluded, P3 is offered and then withdrawn by the policyholder; P6's import will fail.
        await WaveAsync("/api/mig/v1/waves/adjust", new { waveId, legacyKey = p + "P5", action = "EXCLUDE", reason = "Cancelled by the customer" });
        var converted = await WaveAsync("/api/mig/v1/waves/convert", new { waveId, specId = spec });
        converted["status"]!.GetValue<string>().ShouldBe("IN_PROGRESS");
        Count(converted, "OFFERED").ShouldBe(4);
        var batch = converted["batches"]!.AsArray().Single()!;
        batch["itemCount"]!.GetValue<int>().ShouldBe(4);
        batch["specId"]!.GetValue<string>().ShouldBe(spec);
        await WaveAsync("/api/mig/v1/waves/adjust", new { waveId, legacyKey = p + "P3", action = "WITHDRAW", reason = "Policyholder moved to another insurer" });
        await WaveAsync("/api/mig/v1/waves/adjust", new { waveId, legacyKey = p + "P5", action = "WITHDRAW", reason = "x" }, expect: HttpStatusCode.Conflict);
        _importer.FailKeys.Add(p + "P6");

        // Nothing is routed to CORE before the batch runs, and the wave cannot close while terms are offered.
        (await MasterAsync(p + "P1", "2027-01-10")).ShouldBe("LegacyMaster");
        (await WaveAsync("/api/mig/v1/waves/close", new { waveId }, expect: HttpStatusCode.Conflict))["detail"]!.GetValue<string>().ShouldContain("TERMS_OFFERED");
        await RunBatchAsync(batch["batchId"]!.GetValue<string>());

        // P1 and P2 are converted and route to CORE from their renewal date; the withdrawn, excluded and failed terms keep legacy routing and the importer never saw P3 or P5.
        var after = await WaveAsync($"/api/mig/v1/waves/{waveId}", null, HttpMethod.Get);
        States(after).ShouldBe(new Dictionary<string, string>
        {
            [p + "P1"] = "CONVERTED", [p + "P2"] = "CONVERTED", [p + "P3"] = "WITHDRAWN", [p + "P5"] = "EXCLUDED", [p + "P6"] = "NOT_CONVERTED",
        });
        after["terms"]!.AsArray().Single(t => t!["legacyKey"]!.GetValue<string>() == p + "P1")!["coreId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        (await MasterAsync(p + "P1", "2027-01-09")).ShouldBe("LegacyMaster");
        (await MasterAsync(p + "P1", "2027-01-10")).ShouldBe("CoreMaster");
        (await MasterAsync(p + "P2", "2027-02-01")).ShouldBe("CoreMaster");
        foreach (var key in new[] { "P3", "P5", "P6" })
        {
            (await MasterAsync(p + key, "2027-03-01")).ShouldBe("LegacyMaster");
        }

        _importer.Imported.Keys.Where(k => k.StartsWith(p, StringComparison.Ordinal)).Order().ToArray().ShouldBe([p + "P1", p + "P2"]);
        _importer.Attempted.Where(k => k.StartsWith(p, StringComparison.Ordinal)).Order().ToArray().ShouldBe([p + "P1", p + "P2", p + "P6"]);

        // The count equation holds: every selected term is in exactly one state.
        after["equationHolds"]!.GetValue<bool>().ShouldBeTrue();
        after["selectedCount"]!.GetValue<int>().ShouldBe(5);
        AllStates.Sum(s => Count(after, s)).ShouldBe(5);

        // The policy that failed is free for a later wave; the converted one is never selected again.
        var later = await WaveAsync("/api/mig/v1/waves/plan", new { name = name + "-later", source, snapshot = label, windowFrom = "2027-01-01", windowTo = "2027-01-31" });
        States(later).Keys.Order().ToArray().ShouldBe([p + "P3", p + "P5", p + "P6"]);

        // The sign-off gates the close: no run yet, then green run and three signatories.
        var blocked = await WaveAsync("/api/mig/v1/waves/close", new { waveId }, expect: HttpStatusCode.Conflict);
        blocked["detail"]!.GetValue<string>().ShouldContain("SIGNOFF");
        await SignOffAsync(name, batch["batchId"]!.GetValue<string>(), _importer.Imported.Keys.Count(k => k.StartsWith(p, StringComparison.Ordinal)));

        var closed = await WaveAsync("/api/mig/v1/waves/close", new { waveId });
        closed["status"]!.GetValue<string>().ShouldBe("CLOSED");
        closed["closedBy"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        closed["signoff"]!["ready"]!.GetValue<bool>().ShouldBeTrue();
        await WaveAsync("/api/mig/v1/waves/adjust", new { waveId, legacyKey = p + "P1", action = "WITHDRAW", reason = "late" }, expect: HttpStatusCode.Conflict);
        (await WaveAsync("/api/mig/v1/waves/close", new { waveId }))["status"]!.GetValue<string>().ShouldBe("CLOSED");

        // The wave tables and the batch link are in the database.
        (await Scalar($"SELECT count(*) FROM mig.wave_term WHERE wave_id = '{waveId}'")).ShouldBe(5);
        (await Scalar($"SELECT count(*) FROM mig.batch WHERE batch_id = '{batch["batchId"]!.GetValue<string>()}' AND mapping_spec_id = '{spec}' AND mapping_content_hash IS NOT NULL")).ShouldBe(1);
    }

    [Fact]
    public async Task A_term_left_planned_is_not_converted_at_close_and_waves_are_checked_for_access_and_input()
    {
        var p = Id() + "-";
        var (source, label) = await StageAsync(p, ("A", "2027-03-05"), ("B", "2027-03-06"));
        var name = "wave-" + Id();
        var wave = await WaveAsync("/api/mig/v1/waves/plan", new { name, source, snapshot = label, windowFrom = "2027-03-01", windowTo = "2027-03-31" });
        var waveId = wave["waveId"]!.GetValue<string>();
        await WaveAsync("/api/mig/v1/waves/adjust", new { waveId, legacyKey = p + "A", action = "EXCLUDE", reason = "Not renewing" });

        // Input and access.
        await WaveAsync("/api/mig/v1/waves/plan", new { name, source, snapshot = label, windowFrom = "2027-03-31", windowTo = "2027-03-01" }, expect: HttpStatusCode.UnprocessableContent);
        await WaveAsync("/api/mig/v1/waves/plan", new { name = "x-" + Id(), source, snapshot = "nope", windowFrom = "2027-03-01", windowTo = "2027-03-31" }, expect: HttpStatusCode.NotFound);
        await WaveAsync("/api/mig/v1/waves/plan", new { name = "y-" + Id(), source, snapshot = label, windowFrom = "2027-03-01", windowTo = "2027-03-31" }, expect: HttpStatusCode.Forbidden, roles: "Staff.Finance");
        await WaveAsync($"/api/mig/v1/waves/{Guid.NewGuid()}", null, HttpMethod.Get, HttpStatusCode.NotFound);
        await WaveAsync($"/api/mig/v1/waves/{waveId}", null, HttpMethod.Get, roles: "Staff.Finance");
        await WaveAsync($"/api/mig/v1/waves/{waveId}?state=BOGUS", null, HttpMethod.Get, HttpStatusCode.UnprocessableContent);
        var onlyPlanned = await WaveAsync($"/api/mig/v1/waves/{waveId}?state=PLANNED", null, HttpMethod.Get);
        States(onlyPlanned).ShouldBe(new Dictionary<string, string> { [p + "B"] = "PLANNED" });

        // B stays PLANNED; with a ready sign-off the close turns it NOT_CONVERTED and the equation still holds.
        await SignOffAsync(name, null, 0);
        var closed = await WaveAsync("/api/mig/v1/waves/close", new { waveId });
        States(closed).ShouldBe(new Dictionary<string, string> { [p + "A"] = "EXCLUDED", [p + "B"] = "NOT_CONVERTED" });
        closed["equationHolds"]!.GetValue<bool>().ShouldBeTrue();
        (await MasterAsync(p + "B", "2027-04-01")).ShouldBe("LegacyMaster");
    }

    [Fact]
    public async Task A_batch_whose_recorded_mapping_hash_no_longer_matches_the_spec_is_not_run()
    {
        var p = Id() + "-";
        var (source, label) = await StageAsync(p, ("A", "2027-04-05"));
        var spec = await ApprovedSpecAsync();
        var wave = await WaveAsync("/api/mig/v1/waves/plan", new { name = "wave-" + Id(), source, snapshot = label, windowFrom = "2027-04-01", windowTo = "2027-04-30" });
        var waveId = wave["waveId"]!.GetValue<string>();
        var converted = await WaveAsync("/api/mig/v1/waves/convert", new { waveId, specId = spec });
        var batch = converted["batches"]!.AsArray().Single()!["batchId"]!.GetValue<string>();

        // The recorded content hash no longer matches the spec: the real run is refused before anything is imported.
        await ExecuteAsync($"UPDATE mig.batch SET mapping_content_hash = 'stale' WHERE batch_id = '{batch}'");
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId = batch, dryRun = true }, "alice", Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (start, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId = batch, dryRun = false }, "alice", Manager);
        start.StatusCode.ShouldBe(HttpStatusCode.Conflict, body?.ToJsonString());
        _importer.Attempted.Any(k => k.StartsWith(p, StringComparison.Ordinal)).ShouldBeFalse();
        States(await WaveAsync($"/api/mig/v1/waves/{waveId}", null, HttpMethod.Get)).ShouldBe(new Dictionary<string, string> { [p + "A"] = "OFFERED" });
    }

    /// <summary>Loads the control total (WVT policies = <paramref name="expected"/>), runs the reconciliation as alice and has the three signatories approve.</summary>
    private async Task SignOffAsync(string scope, string? batch, int expected)
    {
        var totals = new[] { new { module = "WVT", objectType = "Policy", key = "", currency = "EUR", count = (long)expected, amount = "0.00" } };
        var (loaded, loadBody) = await SendAsync(HttpMethod.Post, "/api/mig/v1/reconciliation/control-totals/load", new { scope, totals }, "alice", Manager);
        loaded.StatusCode.ShouldBe(HttpStatusCode.OK, loadBody?.ToJsonString());
        _importer.ReportedCount = expected;
        var (ran, runBody) = await SendAsync(HttpMethod.Post, "/api/mig/v1/reconciliation/run", new { scope, batchId = batch }, "alice", Manager);
        ran.StatusCode.ShouldBe(HttpStatusCode.OK, runBody?.ToJsonString());
        var (requested, requestBody) = await SendAsync(HttpMethod.Post, "/api/mig/v1/signoff/request", new { scope }, "carol", Manager);
        requested.StatusCode.ShouldBe(HttpStatusCode.OK, requestBody?.ToJsonString());
        var signOff = requestBody!["signoffId"]!.GetValue<string>();
        foreach (var (signatory, user, roles) in new[] { ("MIGRATION_LEAD", "bob", Manager), ("FINANCE_CONTROLLER", "dave", "Staff.Finance"), ("COMPLIANCE_OFFICER", "erin", "Staff.ComplianceOfficer") })
        {
            var (decided, decidedBody) = await SendAsync(HttpMethod.Post, "/api/mig/v1/signoff/approve", new { signoffId = signOff, signatory, comment = "checked" }, user, roles);
            decided.StatusCode.ShouldBe(HttpStatusCode.OK, decidedBody?.ToJsonString());
        }
    }

    private async Task<long> Scalar(string sql)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
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

    /// <summary>Test double of a module importer: WVT policies; fails the keys in <see cref="FailKeys"/> on a real run.</summary>
    private sealed class WaveImporter : IMigrationImporter, IMigrationReconciliationReader
    {
        public ConcurrentDictionary<string, Guid> Imported { get; } = new();

        public ConcurrentBag<string> Attempted { get; } = [];

        public ConcurrentBag<string> FailKeys { get; } = [];

        public int ReportedCount { get; set; }

        public string ModuleName => "WVT";

        public Task<ImportOutcome> ImportAsync(MigrationImportItem item, MigrationImportContext context, CancellationToken cancellationToken)
        {
            if (context.DryRun)
            {
                return Task.FromResult(new ImportOutcome(ImportStatus.Imported));
            }

            Attempted.Add(item.LegacyId);
            if (FailKeys.Contains(item.LegacyId))
            {
                return Task.FromResult(new ImportOutcome(ImportStatus.Failed, null, "WVT-ERR-REJECTED", "The policy was rejected."));
            }

            var id = Guid.CreateVersion7();
            Imported[item.LegacyId] = id;
            return Task.FromResult(new ImportOutcome(ImportStatus.Imported, id));
        }

        public Task<IReadOnlyList<ReconciliationFigure>> ReadAsync(Guid? batchId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReconciliationFigure>>([new ReconciliationFigure("Policy", string.Empty, "EUR", ReportedCount, 0m)]);
    }
}
