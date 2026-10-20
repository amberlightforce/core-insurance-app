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

/// <summary>NX-HD-08b: enquiries, redrive, masked archive access, archive DSAR operations and import-contract conformance.</summary>
public sealed class MigrationReadApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Roles = "Platform.Admin,Staff.MigrationManager";
    private readonly Flaky _pty = new("PTY");
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
        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IMigrationImporter>(_pty)));
        return _factory.CreateClient();
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body, string user = "alice", string roles = Roles)
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

        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<string> RunBatchAsync(params string[] legacyIds)
    {
        var items = legacyIds.Select(id => new { module = "PTY", objectType = "Party", legacyId = id, payload = new { name = "Legacy " + id, iban = "GR0000" } }).ToArray();
        var (created, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches", new { name = "read api", items });
        created.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var batch = body.Text("batch.batchId");
        await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId = batch, dryRun = true });
        await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId = batch, dryRun = false });
        var (approved, done) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId = batch, decision = "APPROVE" }, "bob");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, done?.ToJsonString());
        return batch;
    }

    [Fact]
    public async Task Redrive_runs_only_the_failed_items_of_an_approved_batch_again()
    {
        var good = $"G-{Guid.NewGuid():N}";
        var bad = $"B-{Guid.NewGuid():N}";
        _pty.FailOnce.Add(bad);
        var batch = await RunBatchAsync(good, bad);

        var (_, before) = await SendAsync(HttpMethod.Get, $"/api/mig/v1/batches/{batch}", null);
        before.Text("batch.status").ShouldBe("COMPLETED_WITH_ERRORS");

        var (response, redriven) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/redrive", new { batchId = batch });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, redriven?.ToJsonString());
        redriven.Text("batch.status").ShouldBe("COMPLETED");
        redriven!["outcomes"]!.AsArray().Select(o => o.Text("outcome")).ShouldBe(["IMPORTED", "IMPORTED"]);
        _pty.Imported.Count(i => i.Value == good).ShouldBe(1);

        // Nothing left to redrive: a completed batch is refused, and so is an unknown one.
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/redrive", new { batchId = batch })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/redrive", new { batchId = Guid.NewGuid() })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Archive_search_needs_keys_masks_the_record_and_never_returns_the_payload()
    {
        var legacy = $"LEGACY-{Guid.NewGuid():N}";
        var batch = await RunBatchAsync(legacy);
        (await SendAsync(HttpMethod.Get, "/api/mig/v1/archives/search", null)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var (response, page) = await SendAsync(HttpMethod.Get, $"/api/mig/v1/archives/search?keys={legacy}", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        var record = page!["items"]!.AsArray().Single();
        var masked = record.Text("legacyKeyMasked");
        masked.ShouldNotBe(legacy);
        masked.ShouldContain("*");
        record!.ToJsonString().ShouldNotContain("GR0000");
        record!["payloadFields"]!.AsArray().Select(f => f!.GetValue<string>()).ShouldBe(["name", "iban"], ignoreOrder: true);
        record["batchId"]!.GetValue<string>().ShouldBe(batch);

        var (_, got) = await SendAsync(HttpMethod.Get, $"/api/mig/v1/archives/{record.Text("id")}", null);
        got!["records"]!.AsArray().Single().Text("legacyKeyMasked").ShouldBe(masked);
        (await SendAsync(HttpMethod.Get, $"/api/mig/v1/archives/{Guid.NewGuid()}", null)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The archive roles do not include the underwriter.
        (await SendAsync(HttpMethod.Get, $"/api/mig/v1/archives/search?keys={legacy}", null, roles: "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Dsar_operations_export_restrict_and_never_erase_and_reject_bad_input()
    {
        var legacy = $"DSAR-{Guid.NewGuid():N}";
        await RunBatchAsync(legacy);
        var (_, page) = await SendAsync(HttpMethod.Get, $"/api/mig/v1/archives/search?keys={legacy}", null);
        var subject = page!["items"]!.AsArray().Single().Text("coreId");
        object Body(object? ids = null, object? scope = null) => new { dsarId = Guid.NewGuid(), taskId = Guid.NewGuid(), subjectIds = ids ?? new[] { subject }, scope };

        var (exported, export) = await SendAsync(HttpMethod.Post, "/api/mig/v1/archives/subject-export", Body());
        exported.StatusCode.ShouldBe(HttpStatusCode.OK, export?.ToJsonString());
        export!["perItemOutcomes"]!.AsArray().ShouldNotBeEmpty();
        export["exports"]!.AsArray().Single().Text("payload").ShouldContain("MIGRATION_ARCHIVE");

        // Dry-run erase reports what would be restricted and writes nothing.
        var (_, preview) = await SendAsync(HttpMethod.Post, "/api/mig/v1/archives/erase?dryRun=true", Body());
        preview!["perItemOutcomes"]!.AsArray().ShouldContain(o => o.Text("outcome") == "RESTRICTED");
        (await SendAsync(HttpMethod.Get, $"/api/mig/v1/archives/search?keys={legacy}", null)).Body!["items"]![0].Text("restricted").ShouldBe("false");

        var (rectified, note) = await SendAsync(HttpMethod.Post, "/api/mig/v1/archives/rectification-note", Body(scope: new { note = "Name was misspelt in the legacy system." }));
        rectified.StatusCode.ShouldBe(HttpStatusCode.OK, note?.ToJsonString());
        note!["perItemOutcomes"]!.AsArray().ShouldContain(o => o.Text("outcome") == "NOTED");
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/archives/rectification-note", Body())).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // Erase fails closed: the migration archive is restricted under retention, never destroyed.
        var (erased, erase) = await SendAsync(HttpMethod.Post, "/api/mig/v1/archives/erase", Body());
        erased.StatusCode.ShouldBe(HttpStatusCode.OK, erase?.ToJsonString());
        erase!["perItemOutcomes"]!.AsArray().Select(o => o.Text("outcome")).ShouldAllBe(o => o == "RESTRICTED" || o == "NOT_HELD");
        erase.ToJsonString().ShouldNotContain("ERASED");
        (await SendAsync(HttpMethod.Get, $"/api/mig/v1/archives/search?keys={legacy}", null)).Body!["items"]![0].Text("restricted").ShouldBe("true");
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/archives/restrict", Body(scope: new { retentionEnd = "2036-01-01" }))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Bad input is refused, and only compliance/admin roles may call.
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/archives/erase", Body(ids: Array.Empty<Guid>()))).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/archives/erase", Body(ids: new[] { Guid.Empty }))).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/archives/restrict", Body(scope: new { retentionEnd = "soon" }))).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/archives/erase", Body(), roles: "Staff.MigrationManager")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Conformance_reports_importer_and_reverser_registration_per_element()
    {
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/import-contracts/conformance", new { module = "PTY" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["passed"]!.GetValue<bool>().ShouldBeFalse();
        var elements = body["passFailPerContractElement"]!.AsArray();
        elements!.Single(e => e.Text("element") == "IMigrationImporter")!["passed"]!.GetValue<bool>().ShouldBeTrue();

        var (_, importOnly) = await SendAsync(HttpMethod.Post, "/api/mig/v1/import-contracts/conformance", new { module = "PTY", operation = "import" });
        importOnly!["passed"]!.GetValue<bool>().ShouldBeTrue();
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/import-contracts/conformance", new { module = "NOPE" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/import-contracts/conformance", new { module = "PTY", operation = "drop" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Enquiry_needs_a_key_and_shows_the_master_of_each_object()
    {
        (await SendAsync(HttpMethod.Get, "/api/mig/v1/enquiries/get", null)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var policy = $"POL-{Guid.NewGuid():N}";
        var (_, before) = await SendAsync(HttpMethod.Get, $"/api/mig/v1/enquiries/get?policyKey={policy}", null);
        before!["summaryMasterMarks"]![0].Text("master").ShouldBe("NONE");

        await SendAsync(HttpMethod.Post, "/api/mig/v1/routing/override", new { objectType = "Policy", key = policy, masterState = "CORE", from = "2020-01-01", reason = "cut-over" });
        var (response, after) = await SendAsync(HttpMethod.Get, $"/api/mig/v1/enquiries/get?policyKey={policy}&claimKey=CLM-1", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, after?.ToJsonString());
        var marks = after!["summaryMasterMarks"]!.AsArray();
        marks.Single(m => m.Text("objectType") == "Policy").Text("master").ShouldBe("CORE");
        marks.Single(m => m.Text("objectType") == "Claim").Text("master").ShouldBe("NONE");
        after["freshness"]!["asOf"].ShouldNotBeNull();
    }

    /// <summary>Imports into memory; ids listed in <see cref="FailOnce"/> fail on their first real attempt.</summary>
    private sealed class Flaky(string module) : IMigrationImporter
    {
        public ConcurrentDictionary<Guid, string> Imported { get; } = new();

        public ConcurrentBag<string> FailOnce { get; } = [];

        private readonly ConcurrentDictionary<string, bool> _failed = new();

        public string ModuleName => module;

        public Task<ImportOutcome> ImportAsync(MigrationImportItem item, MigrationImportContext context, CancellationToken cancellationToken)
        {
            if (context.DryRun)
            {
                return Task.FromResult(new ImportOutcome(ImportStatus.Imported));
            }

            if (FailOnce.Contains(item.LegacyId) && _failed.TryAdd(item.LegacyId, true))
            {
                return Task.FromResult(new ImportOutcome(ImportStatus.Failed, null, "MIG-ERR-VALIDATION", "Transient failure."));
            }

            var id = Guid.CreateVersion7();
            Imported[id] = item.LegacyId;
            return Task.FromResult(new ImportOutcome(ImportStatus.Imported, id));
        }
    }
}
