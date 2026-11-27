using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Data.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Data;

/// <summary>BO-DAT-01: data-contract registry, raw-layer event ingestion (dedupe, quarantine), release and replay.</summary>
public sealed class DataIngestionTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private static readonly string[] PolicyIdOnly = ["policyId"];
    private static readonly string[] BothFields = ["policyId", "grossPremium"];
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static EventEnvelope Envelope(string eventType, JsonObject payload, Guid? id = null) => new()
    {
        EventId = id is { } given ? new EventId(given) : EventId.New(),
        EventType = EventTypeName.Parse(eventType),
        SchemaVersion = "1.0",
        Producer = ModuleCode.POL,
        AggregateType = "Policy",
        AggregateId = Guid.CreateVersion7().ToString(),
        AggregateSequence = 1,
        OccurredAt = Instant.Parse("2026-10-07T08:00:00Z"),
        RecordedAt = Instant.Parse("2026-10-07T08:00:01Z"),
        LegalEntity = LegalEntityCode.Parse("GR-TEST"),
        Jurisdiction = Jurisdiction.Parse("GR"),
        ConfigurationHash = ConfigurationHash.Parse(new string('a', 64)),
        BusinessKeys = BusinessKeys.Empty.With("policyId", Guid.NewGuid().ToString()),
        CorrelationId = CorrelationId.New(),
        Actor = ActorRef.User("user-1"),
        Origin = EventOrigin.Live,
        DataClassification = DataClassification.P0,
        Payload = payload,
    };

    private async Task<IngestOutcome> IngestAsync(EventEnvelope envelope)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<EventIngestion>().IngestAsync(envelope, TestContext.Current.CancellationToken);
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task RegisterAsync(HttpClient client, object document, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/data-contracts/register", new { contractDocument = document }, roles: Admin);
        response.StatusCode.ShouldBe(expected, body?.ToJsonString());
    }

    [Fact]
    public void Every_report_relevant_event_has_a_raw_layer_handler()
    {
        var registry = _factory.Services.GetRequiredService<EventHandlerRegistry>();
        foreach (var (producer, eventType) in EventIngestion.ReportRelevant)
        {
            registry.For(EventRouting.Key(producer.ToString(), eventType, 1)).ShouldContain(h => h.Module == ModuleCode.DAT);
        }
    }

    [Fact]
    public async Task A_duplicate_event_is_ingested_once()
    {
        var id = Guid.CreateVersion7();
        var payload = new JsonObject { ["policyId"] = "p-1" };
        (await IngestAsync(Envelope("PolicyIssued", payload, id))).ShouldBe(IngestOutcome.Ingested);
        (await IngestAsync(Envelope("PolicyIssued", payload, id))).ShouldBe(IngestOutcome.Duplicate);
        (await CountAsync($"SELECT count(*) FROM rpt_raw.event_ledger WHERE event_id = '{id}'")).ShouldBe(1);
        (await CountAsync($"SELECT count(*) FROM rpt_raw.quarantine WHERE event_id = '{id}'")).ShouldBe(0);
    }

    [Fact]
    public async Task A_malformed_payload_is_quarantined_then_released_once_the_contract_allows_it()
    {
        using var client = _factory.CreateClient();
        var name = "pol.PolicyChanged";
        await RegisterAsync(client, new
        {
            name,
            requiredFields = BothFields,
            properties = new { policyId = "string", grossPremium = "number" },
        });

        var id = Guid.CreateVersion7();
        (await IngestAsync(Envelope("PolicyChanged", new JsonObject { ["policyId"] = "p-2" }, id))).ShouldBe(IngestOutcome.Quarantined);
        (await CountAsync($"SELECT count(*) FROM rpt_raw.quarantine WHERE event_id = '{id}' AND status = 'QUARANTINED'")).ShouldBe(1);
        (await CountAsync($"SELECT count(*) FROM rpt_raw.event_ledger WHERE event_id = '{id}'")).ShouldBe(0);

        var quarantineId = await QuarantineIdAsync(id);
        var (_, stillBad) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/quarantine/release", new { ids = new[] { quarantineId } }, roles: Admin);
        stillBad.Text("outcomePerRecord.0.outcome").ShouldBe("STILL_INVALID");

        // Relaxing a required field is a compatible change (next minor).
        await RegisterAsync(client, new { name, requiredFields = PolicyIdOnly, properties = new { policyId = "string", grossPremium = "number" } });

        var (dry, dryBody) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/quarantine/release?dryRun=true", new { ids = new[] { quarantineId } }, roles: Admin);
        dry.StatusCode.ShouldBe(HttpStatusCode.OK, dryBody?.ToJsonString());
        (await CountAsync($"SELECT count(*) FROM rpt_raw.event_ledger WHERE event_id = '{id}'")).ShouldBe(0);

        var (released, body) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/quarantine/release", new { ids = new[] { quarantineId } }, roles: Admin);
        released.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("outcomePerRecord.0.outcome").ShouldBe("RELEASED");
        (await CountAsync($"SELECT count(*) FROM rpt_raw.event_ledger WHERE event_id = '{id}'")).ShouldBe(1);
        (await CountAsync($"SELECT count(*) FROM rpt_raw.quarantine WHERE event_id = '{id}' AND status = 'RELEASED'")).ShouldBe(1);

        var (_, again) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/quarantine/release", new { ids = new[] { quarantineId } }, roles: Admin);
        again.Text("outcomePerRecord.0.outcome").ShouldBe("ALREADY_RELEASED");
    }

    [Fact]
    public async Task Contract_versions_follow_the_compatibility_rules_and_are_readable()
    {
        using var client = _factory.CreateClient();
        var name = "pol.PolicyCancelled";
        await RegisterAsync(client, new { name, requiredFields = PolicyIdOnly, properties = new { policyId = "string" } });

        // Adding an optional field is compatible (1.1); the same document again is a no-op.
        var added = new { name, requiredFields = PolicyIdOnly, properties = new { policyId = "string", reason = "string" } };
        await RegisterAsync(client, added);
        await RegisterAsync(client, added);

        // Retyping a field without a new major is refused (DAT-ERR-001); with a new major it is accepted.
        var retyped = new { name, requiredFields = PolicyIdOnly, properties = new { policyId = "integer", reason = "string" } };
        var (refused, problem) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/data-contracts/register", new { contractDocument = retyped }, roles: Admin);
        refused.StatusCode.ShouldBe((HttpStatusCode)422, problem?.ToJsonString());
        problem!.ToJsonString().ShouldContain("DAT-ERR-001");
        await RegisterAsync(client, new { name, major = 2, requiredFields = PolicyIdOnly, properties = new { policyId = "integer", reason = "string" } });

        var (got, body) = await SendAsync(client, HttpMethod.Get, $"/api/dat/v1/data-contracts/{name}", null, roles: Admin);
        got.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("contractVersions.0.version").ShouldBe("1.0");
        body.Text("contractVersions.1.version").ShouldBe("1.1");
        body.Text("contractVersions.2.version").ShouldBe("2.0");

        var (listed, page) = await SendAsync(client, HttpMethod.Get, "/api/dat/v1/data-contracts?producer=POL&limit=200", null, roles: Admin);
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        page!.ToJsonString().ShouldContain(name);

        (await SendAsync(client, HttpMethod.Get, "/api/dat/v1/data-contracts/pol.Nope", null, roles: Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Replay_is_idempotent_and_does_not_change_the_ledger()
    {
        using var client = _factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            (await IngestAsync(Envelope("PolicyLapsed", new JsonObject { ["policyId"] = $"p-{i}" }))).ShouldBe(IngestOutcome.Ingested);
        }

        var before = await CountAsync("SELECT count(*) FROM rpt_raw.event_ledger");
        var body = new { contract = "pol.PolicyLapsed", from = "2026-10-01T00:00:00Z", to = "2026-10-31T00:00:00Z" };
        var (first, one) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/ingestion/replay", body, roles: Admin);
        var (second, two) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/ingestion/replay", body, roles: Admin);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted, one?.ToJsonString());
        second.StatusCode.ShouldBe(HttpStatusCode.Accepted, two?.ToJsonString());
        one.Text("counts.scanned").ShouldBe("3");
        one.Text("counts.valid").ShouldBe("3");
        two.Text("counts.scanned").ShouldBe("3");
        (await CountAsync("SELECT count(*) FROM rpt_raw.event_ledger")).ShouldBe(before);

        var (bad, _) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/ingestion/replay", new { from = "x" }, roles: Admin);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Non_data_roles_are_forbidden()
    {
        using var client = _factory.CreateClient();
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, "/api/dat/v1/data-contracts/register", new { contractDocument = new { name = "pol.PolicyBound" } }),
            (HttpMethod.Get, "/api/dat/v1/data-contracts", null),
            (HttpMethod.Get, "/api/dat/v1/data-contracts/pol.PolicyBound", null),
            (HttpMethod.Post, "/api/dat/v1/ingestion/replay", new { contract = "pol.PolicyBound" }),
            (HttpMethod.Post, "/api/dat/v1/quarantine/release", new { ids = new[] { Guid.NewGuid() } }),
        })
        {
            (await SendAsync(client, method, path, body, roles: "Staff.Billing")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }
    }

    private async Task<Guid> QuarantineIdAsync(Guid eventId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand($"SELECT quarantine_id FROM rpt_raw.quarantine WHERE event_id = '{eventId}'", connection);
        return (Guid)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
