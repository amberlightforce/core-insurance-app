using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Data.Commands;
using CoreIns.Modules.Data.Services;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Data;

/// <summary>BO-DAT-03: mart run, get, figure and compare over the conformed facts.</summary>
public sealed class DataMartTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private const string Entity = "GR-TEST";
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static EventEnvelope Envelope(ModuleCode producer, string eventType, string aggregateType, JsonObject payload, string occurred, string product) => new()
    {
        EventId = EventId.New(),
        EventType = EventTypeName.Parse(eventType),
        SchemaVersion = "1.0",
        Producer = producer,
        AggregateType = aggregateType,
        AggregateId = Guid.CreateVersion7().ToString(),
        AggregateSequence = 1,
        OccurredAt = Instant.Parse(occurred),
        RecordedAt = Instant.Parse(occurred),
        LegalEntity = LegalEntityCode.Parse(Entity),
        Jurisdiction = Jurisdiction.Parse("GR"),
        ConfigurationHash = ConfigurationHash.Parse(new string('a', 64)),
        BusinessKeys = BusinessKeys.Empty.With("policyId", Guid.NewGuid().ToString()).With("productCode", product),
        CorrelationId = CorrelationId.New(),
        Actor = ActorRef.User("user-1"),
        Origin = EventOrigin.Live,
        DataClassification = DataClassification.P0,
        Payload = payload,
    };

    private static JsonObject Money(string amount) => new() { ["amount"] = amount, ["currency"] = "EUR" };

    private static JsonObject Money3(string amount) => new() { ["transaction"] = Money(amount), ["functional"] = Money(amount), ["group"] = Money(amount) };

    private static EventEnvelope Issued(Guid term, string product) =>
        Envelope(ModuleCode.POL, "PolicyIssued", "Policy", new JsonObject { ["termId"] = term, ["transactionId"] = Guid.NewGuid(), ["issuedAt"] = "2026-10-01T00:00:00Z", ["documentIds"] = new JsonArray() }, "2026-10-01T00:00:00Z", product);

    private static EventEnvelope Invoice(Guid invoice, Guid term, string product, string premium, string occurred = "2026-10-05T00:00:00Z") =>
        Envelope(ModuleCode.BIL, "InvoiceIssued", "BillingAccount", new JsonObject
        {
            ["invoiceId"] = invoice,
            ["kind"] = "INVOICE",
            ["termIds"] = new JsonArray(term.ToString()),
            ["totalsByCategory"] = new JsonArray(new JsonObject { ["category"] = "PREMIUM", ["amount"] = Money(premium) }),
        }, occurred, product);

    private async Task IngestAsync(EventEnvelope envelope)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<EventIngestion>().IngestAsync(envelope, TestContext.Current.CancellationToken);
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private async Task<JsonNode?> RunAsync(object body, HttpStatusCode expected = HttpStatusCode.Accepted)
    {
        using var client = _factory.CreateClient();
        var (response, json) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/marts/run", body, roles: Admin);
        response.StatusCode.ShouldBe(expected, json?.ToJsonString());
        return json;
    }

    private async Task<JsonNode?> GetAsync(string path, string roles = Admin, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var client = _factory.CreateClient();
        var (response, body) = await SendAsync(client, HttpMethod.Get, path, null, roles: roles);
        response.StatusCode.ShouldBe(expected, body?.ToJsonString());
        return body;
    }

    private static decimal Dec(JsonNode? node) => decimal.Parse(node!.ToString(), CultureInfo.InvariantCulture);

    private static JsonNode? Figure(JsonNode? get, string key) => get!["figures"]!.AsArray().FirstOrDefault(f => f!["key"]!.ToString() == key);

    [Fact]
    public async Task Same_inputs_give_the_same_hash_and_a_correction_shows_in_compare()
    {
        var product = $"MOTOR-{Guid.NewGuid():N}";
        var invoice = Guid.NewGuid();
        var term = Guid.NewGuid();
        await IngestAsync(Issued(term, product));
        await IngestAsync(Invoice(invoice, term, product, "100.00"));
        await IngestAsync(Invoice(Guid.NewGuid(), term, product, "50.00", "2026-11-04T00:00:00Z"));

        var request = new { mart = "PORTFOLIO_PREMIUM", period = "2026", productCode = product, validAt = "2026-12-31" };
        var one = await RunAsync(request);
        var two = await RunAsync(request);
        one.Text("snapshotHash").ShouldBe(two.Text("snapshotHash"));
        one.Text("runId").ShouldNotBe(two.Text("runId"));
        one.Text("legalStatus").ShouldBe("NotRegulatory");
        one.Text("status").ShouldBe("COMPLETED");

        var get = await GetAsync($"/api/dat/v1/marts/{one.Text("runId")}");
        Dec(Figure(get, $"GWP|{product}|2026-10|")!["value"]).ShouldBe(100m);
        Dec(Figure(get, $"GWP|{product}|2026-11|")!["value"]).ShouldBe(50m);
        get.Text("run.snapshotHash").ShouldBe(one.Text("snapshotHash"));
        ((JsonArray)get!["lineagePointer"]!["sources"]!).Count.ShouldBe(1);

        var single = await GetAsync($"/api/dat/v1/marts/figure?runId={one.Text("runId")}&figureKey=GWP|{product}|2026-10|");
        Dec(single!["figures"]![0]!["value"]).ShouldBe(100m);
        single.Text("lineagePointer.sourceRowCount").ShouldBe("1");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        await IngestAsync(Invoice(invoice, term, product, "120.00")); // restated invoice, same business key
        var corrected = await RunAsync(request);
        corrected.Text("snapshotHash").ShouldNotBe(one.Text("snapshotHash"));

        var runs = await GetAsync($"/api/dat/v1/marts/compare?runA={one.Text("runId")}&runB={corrected.Text("runId")}");
        var diff = runs!["diff"]!.AsArray().Single(d => d!["key"]!.ToString() == $"GWP|{product}|2026-10|")!;
        Dec(diff["delta"]).ShouldBe(20m);
        diff.Text("status").ShouldBe("CHANGED");

        // Two record-time instants without stored runs: just before the restatement vs now.
        var recorded = (DateTime)(await ScalarAsync($"SELECT min(recorded_from) FROM rpt_conformed.fact_premium WHERE invoice_id = '{invoice}' AND amount = 120"))!;
        var before = recorded.AddMilliseconds(-1).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        var asOf = await GetAsync($"/api/dat/v1/marts/compare?mart=PORTFOLIO_PREMIUM&period=2026&productCode={product}&validAt=2026-12-31&knownAtA={before}");
        Dec(asOf!["diff"]!.AsArray().Single(d => d!["key"]!.ToString() == $"GWP|{product}|2026-10|")!["delta"]).ShouldBe(20m);
        asOf.Text("lineagePointer.sameHash").ShouldBe("false");
    }

    [Fact]
    public async Task Claims_experience_reuses_the_kpi_definitions_and_the_ri_mart_sums_cessions()
    {
        var product = $"HOME-{Guid.NewGuid():N}";
        var term = Guid.NewGuid();
        await IngestAsync(Issued(term, product));
        await IngestAsync(Invoice(Guid.NewGuid(), term, product, "200.00"));
        var claim = Guid.NewGuid();
        await IngestAsync(Envelope(ModuleCode.CLM, "ReserveChanged", "Claim", new JsonObject
        {
            ["claimId"] = claim, ["kind"] = "RESERVE", ["delta"] = Money3("100.00"), ["productCode"] = product, ["policyTermId"] = Guid.NewGuid(),
        }, "2026-10-10T00:00:00Z", product));
        await IngestAsync(Envelope(ModuleCode.CLM, "PaymentIssued", "Claim", new JsonObject
        {
            ["claimId"] = claim, ["paymentId"] = Guid.NewGuid(), ["amount"] = Money3("50.00"),
        }, "2026-10-12T00:00:00Z", product));

        var run = await RunAsync(new { mart = "CLAIMS_EXPERIENCE", productCode = product, validAt = "2026-12-31" });
        var get = await GetAsync($"/api/dat/v1/marts/{run.Text("runId")}");
        Dec(Figure(get, $"CLAIMS_PAID|{product}||")!["value"]).ShouldBe(50m);
        Dec(Figure(get, $"OPEN_RESERVE|{product}||")!["value"]).ShouldBe(100m);
        Dec(Figure(get, $"LOSS_RATIO|{product}||")!["value"]).ShouldBe(0.75m); // (50 + 100) / 200

        // RI: two cession deltas of one contract, inserted into the raw ledger directly.
        var contract = Guid.NewGuid();
        foreach (var (ceded, commission) in new[] { ("40.00", "4.00"), ("10.00", "1.00") })
        {
            var envelope = new JsonObject
            {
                ["payload"] = new JsonObject { ["riContractId"] = contract, ["cededAmount"] = Money3(ceded), ["commissionAmount"] = Money3(commission) },
            };
            await ScalarAsync($"""
                INSERT INTO rpt_raw.event_ledger (event_id, registry_name, event_type, producer, schema_version, aggregate_type, aggregate_id, aggregate_sequence, occurred_at, legal_entity, origin, envelope, received_at)
                VALUES ('{Guid.CreateVersion7()}', 'ri.CessionCalculated', 'CessionCalculated', 'RI', '1.0', 'Policy', '{Guid.NewGuid()}', 1, '2026-10-15T00:00:00Z', '{Entity}', 'LIVE', '{envelope.ToJsonString()}'::jsonb, '2026-10-15T00:00:00Z')
                """);
        }

        var ri = await RunAsync(new { mart = "RI_CESSION_SUMMARY", period = "2026-10", validAt = "2026-12-31" });
        var riGet = await GetAsync($"/api/dat/v1/marts/{ri.Text("runId")}");
        Dec(Figure(riGet, $"CEDED_PREMIUM|ALL|2026-10|{contract}")!["value"]).ShouldBe(50m);
        Dec(Figure(riGet, $"CEDED_COMMISSION|ALL|2026-10|{contract}")!["value"]).ShouldBe(5m);
        Dec(Figure(riGet, $"CESSION_COUNT|ALL|2026-10|{contract}")!["value"]).ShouldBe(2m);
    }

    [Fact]
    public async Task Ri_mart_nets_a_recomputed_cession_because_events_are_incremental_deltas()
    {
        var contract = Guid.NewGuid();
        var policy = Guid.NewGuid();
        foreach (var (ceded, commission) in new[] { ("100.00", "10.00"), ("-30.00", "-3.00") }) // original set, then a recomputation delta set
        {
            var envelope = new JsonObject
            {
                ["payload"] = new JsonObject { ["riContractId"] = contract, ["deltaSetId"] = Guid.NewGuid(), ["cededAmount"] = Money3(ceded), ["commissionAmount"] = Money3(commission) },
            };
            await ScalarAsync($"""
                INSERT INTO rpt_raw.event_ledger (event_id, registry_name, event_type, producer, schema_version, aggregate_type, aggregate_id, aggregate_sequence, occurred_at, legal_entity, origin, envelope, received_at)
                VALUES ('{Guid.CreateVersion7()}', 'ri.CessionCalculated', 'CessionCalculated', 'RI', '1.0', 'Policy', '{policy}', 1, '2026-10-15T00:00:00Z', '{Entity}', 'LIVE', '{envelope.ToJsonString()}'::jsonb, '2026-10-15T00:00:00Z')
                """);
        }

        var run = await RunAsync(new { mart = "RI_CESSION_SUMMARY", period = "2026-10", validAt = "2026-12-31" });
        var get = await GetAsync($"/api/dat/v1/marts/{run.Text("runId")}");
        Dec(Figure(get, $"CEDED_PREMIUM|ALL|2026-10|{contract}")!["value"]).ShouldBe(70m);
        Dec(Figure(get, $"CEDED_COMMISSION|ALL|2026-10|{contract}")!["value"]).ShouldBe(7m);
    }

    [Fact]
    public async Task Bad_input_other_entities_and_non_data_roles_are_refused()
    {
        await RunAsync(new { mart = "PORTFOLIO_PREMIUM", period = "nonsense" }, HttpStatusCode.BadRequest);
        await RunAsync(new { mart = "PORTFOLIO_PREMIUM", entity = "GR-OTHER" }, HttpStatusCode.BadRequest);
        await GetAsync($"/api/dat/v1/marts/{Guid.NewGuid()}", expected: HttpStatusCode.NotFound);
        await GetAsync("/api/dat/v1/marts/not-a-guid", expected: HttpStatusCode.NotFound);
        await GetAsync($"/api/dat/v1/marts/{Guid.NewGuid()}", roles: "Staff.Billing", expected: HttpStatusCode.Forbidden);
        await GetAsync("/api/dat/v1/marts/compare", roles: "Staff.Billing", expected: HttpStatusCode.Forbidden);
    }
}
