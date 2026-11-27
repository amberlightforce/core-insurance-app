using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Migration.Commands;
using CoreIns.Modules.Migration.Contracts.Spi;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Migration;

/// <summary>BO-MIG-01: a large approved batch runs on the worker in chunks, with pause/resume, a cursor and crash recovery.</summary>
public sealed class BatchBackgroundRunTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Roles = "Platform.Admin,Staff.MigrationManager";
    private readonly CountingImporter _importer = new();
    private ApiHostFactory? _root;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    private HttpClient Client => _client ??= Create();

    private BatchWorkerRunner Worker => _factory!.Services.GetRequiredService<BatchWorkerRunner>();

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
        _factory = _root.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Migration:Batches:InlineRunMaxItems"] = "0",
                ["Migration:Batches:ChunkSize"] = "50",
                ["Migration:Batches:LeaseSeconds"] = "1",
            }));
            builder.ConfigureTestServices(services => services.AddSingleton<IMigrationImporter>(_importer));
        });
        var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(15); // the dry run of 10,000 items still runs in the request
        return client;
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

    private async Task<string> ApprovedBatchAsync(int count)
    {
        var prefix = Guid.NewGuid().ToString("N");
        var items = Enumerable.Range(1, count).Select(i => new { module = "BGT", objectType = "Party", legacyId = $"{prefix}-{i}", payload = new { n = i } }).ToArray();
        var (created, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches", new { name = "background", items }, "alice");
        created.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var batch = body.Text("batch.batchId");
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId = batch, dryRun = true }, "alice")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId = batch, dryRun = false }, "alice")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (approved, result) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId = batch, decision = "APPROVE" }, "bob");

        // The run is queued, not done: 202 and RUNNING.
        approved.StatusCode.ShouldBe(HttpStatusCode.Accepted, result?.ToJsonString());
        result.Text("batch.status").ShouldBe("RUNNING");
        _importer.Calls.Count.ShouldBe(0);
        return batch;
    }

    private async Task<JsonNode> GetAsync(string batch) => (await SendAsync(HttpMethod.Get, $"/api/mig/v1/batches/{batch}", null, "alice")).Body!["batch"]!;

    [Fact]
    public async Task Pause_in_the_middle_then_resume_finishes_without_duplicates()
    {
        var batch = await ApprovedBatchAsync(220);
        var paused = false;
        _importer.Hook = async calls =>
        {
            if (calls == 120 && !paused)
            {
                paused = true;
                (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/pause", new { batchId = batch }, "alice")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
            }
        };

        // The pause lands during the third chunk; that chunk is finished and the run stops at its boundary.
        (await Worker.RunAsync(null, TestContext.Current.CancellationToken)).ShouldBe(0);
        var held = await GetAsync(batch);
        held.Text("status").ShouldBe("PAUSED");
        held["processedCount"]!.GetValue<int>().ShouldBe(150);
        _importer.Calls.Count.ShouldBe(150);

        // A worker pass over a paused batch does nothing.
        (await Worker.RunAsync(null, TestContext.Current.CancellationToken)).ShouldBe(0);
        _importer.Calls.Count.ShouldBe(150);

        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/resume", new { batchId = batch }, "alice")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Worker.RunAsync(null, TestContext.Current.CancellationToken)).ShouldBe(1);

        var done = await GetAsync(batch);
        done.Text("status").ShouldBe("COMPLETED");
        done["processedCount"]!.GetValue<int>().ShouldBe(220);
        done["failedCount"]!.GetValue<int>().ShouldBe(0);
        _importer.Calls.Count.ShouldBe(220);
        _importer.Calls.Values.ShouldAllBe(n => n == 1);
    }

    [Fact]
    public async Task A_worker_that_stops_mid_run_is_taken_over_from_the_cursor_after_its_lease_expires()
    {
        var batch = await ApprovedBatchAsync(130);

        // One chunk, then the worker "crashes" (it returns without releasing its lease).
        (await Worker.RunAsync(1, TestContext.Current.CancellationToken)).ShouldBe(0);
        (await GetAsync(batch))["processedCount"]!.GetValue<int>().ShouldBe(50);

        // While the lease is held another worker leaves the batch alone.
        (await Worker.RunAsync(null, TestContext.Current.CancellationToken)).ShouldBe(0);
        _importer.Calls.Count.ShouldBe(50);

        await Task.Delay(1500, TestContext.Current.CancellationToken);
        (await Worker.RunAsync(null, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await GetAsync(batch)).Text("status").ShouldBe("COMPLETED");
        _importer.Calls.Count.ShouldBe(130);
        _importer.Calls.Values.ShouldAllBe(n => n == 1);
    }

    [Fact]
    public async Task A_batch_of_ten_thousand_items_completes()
    {
        var batch = await ApprovedBatchAsync(10_000);
        (await Worker.RunAsync(null, TestContext.Current.CancellationToken)).ShouldBe(1);
        var done = await GetAsync(batch);
        done.Text("status").ShouldBe("COMPLETED");
        done["processedCount"]!.GetValue<int>().ShouldBe(10_000);
        _importer.Calls.Count.ShouldBe(10_000);
    }

    private sealed class CountingImporter : IMigrationImporter
    {
        public ConcurrentDictionary<string, int> Calls { get; } = new();

        public Func<int, Task>? Hook { get; set; }

        public string ModuleName => "BGT";

        public async Task<ImportOutcome> ImportAsync(MigrationImportItem item, MigrationImportContext context, CancellationToken cancellationToken)
        {
            if (context.DryRun)
            {
                return new ImportOutcome(ImportStatus.Imported);
            }

            var total = Calls.Count + 1;
            Calls.AddOrUpdate(item.LegacyId, 1, (_, n) => n + 1);
            if (Hook is { } hook)
            {
                await hook(total);
            }

            return new ImportOutcome(ImportStatus.Imported, Guid.CreateVersion7());
        }
    }
}
