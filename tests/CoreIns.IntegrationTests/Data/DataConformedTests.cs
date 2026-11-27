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
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Data;

/// <summary>BO-DAT-02: conformed bitemporal facts projected from the raw ledger, and the as-of / aggregate / KPI reads.</summary>
public sealed class DataConformedTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private const string Entity = "GR-TEST";
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static EventEnvelope Envelope(ModuleCode producer, string eventType, string aggregateType, JsonObject payload, string occurred, string product, string legalEntity = Entity, Guid? id = null) => new()
    {
        EventId = id is { } given ? new EventId(given) : EventId.New(),
        EventType = EventTypeName.Parse(eventType),
        SchemaVersion = "1.0",
        Producer = producer,
        AggregateType = aggregateType,
        AggregateId = Guid.CreateVersion7().ToString(),
        AggregateSequence = 1,
        OccurredAt = Instant.Parse(occurred),
        RecordedAt = Instant.Parse(occurred),
        LegalEntity = LegalEntityCode.Parse(legalEntity),
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

    private static EventEnvelope Issued(Guid term, string product, string issuedAt = "2026-10-01T00:00:00Z", string legalEntity = Entity, Guid? id = null) =>
        Envelope(ModuleCode.POL, "PolicyIssued", "Policy", new JsonObject { ["termId"] = term, ["transactionId"] = Guid.NewGuid(), ["issuedAt"] = issuedAt, ["documentIds"] = new JsonArray() }, issuedAt, product, legalEntity, id);

    private static EventEnvelope Invoice(Guid invoice, Guid term, string product, string premium, string kind = "INVOICE", string occurred = "2026-10-05T00:00:00Z", string legalEntity = Entity) =>
        Envelope(ModuleCode.BIL, "InvoiceIssued", "BillingAccount", new JsonObject
        {
            ["invoiceId"] = invoice,
            ["kind"] = kind,
            ["termIds"] = new JsonArray(term.ToString()),
            ["totalsByCategory"] = new JsonArray(
                new JsonObject { ["category"] = "PREMIUM", ["amount"] = Money(premium) },
                new JsonObject { ["category"] = "TAX", ["amount"] = Money("10.00") }),
        }, occurred, product, legalEntity);

    private static EventEnvelope Reserve(Guid claim, string product, string delta, string occurred = "2026-10-10T00:00:00Z") =>
        Envelope(ModuleCode.CLM, "ReserveChanged", "Claim", new JsonObject
        {
            ["claimId"] = claim, ["kind"] = "RESERVE", ["delta"] = Money3(delta), ["productCode"] = product, ["policyTermId"] = Guid.NewGuid(),
        }, occurred, product);

    private static EventEnvelope Paid(Guid claim, Guid payment, string amount, string product, string occurred = "2026-10-12T00:00:00Z") =>
        Envelope(ModuleCode.CLM, "PaymentIssued", "Claim", new JsonObject { ["claimId"] = claim, ["paymentId"] = payment, ["amount"] = Money3(amount) }, occurred, product);

    private async Task<IngestOutcome> IngestAsync(EventEnvelope envelope)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<EventIngestion>().IngestAsync(envelope, TestContext.Current.CancellationToken);
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private async Task<JsonNode?> GetAsync(string path, string roles = Admin, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var client = _factory.CreateClient();
        var (response, body) = await SendAsync(client, HttpMethod.Get, path, null, roles: roles);
        response.StatusCode.ShouldBe(expected, body?.ToJsonString());
        return body;
    }

    private static decimal Dec(JsonNode? body, string path) => decimal.Parse(body.Text(path), CultureInfo.InvariantCulture);

    private static decimal Amount(JsonNode? body, string kpi) => decimal.Parse(body.Text($"values.{kpi}.amount"), CultureInfo.InvariantCulture);

    [Fact]
    public async Task Policy_term_history_is_bitemporal_and_a_duplicate_is_projected_once()
    {
        var product = $"MOTOR-{Guid.NewGuid():N}";
        var term = Guid.NewGuid();
        var issued = Guid.CreateVersion7();
        (await IngestAsync(Issued(term, product, id: issued))).ShouldBe(IngestOutcome.Ingested);
        (await IngestAsync(Issued(term, product, id: issued))).ShouldBe(IngestOutcome.Duplicate);
        (await IngestAsync(Envelope(ModuleCode.POL, "PolicyCancelled", "Policy", new JsonObject
        {
            ["transactionId"] = Guid.NewGuid(), ["termId"] = term, ["reason"] = "CUSTOMER", ["effectiveDate"] = "2026-11-01",
        }, "2026-10-20T00:00:00Z", product))).ShouldBe(IngestOutcome.Ingested);

        (await ScalarAsync($"SELECT count(*) FROM rpt_conformed.projected_event WHERE event_id = '{issued}'")).ShouldBe(1L, $"{await ScalarAsync($"SELECT reasons::text FROM rpt_raw.quarantine WHERE event_id = '{issued}'")}");
        // Issue row truncated at the cancellation date, the cancellation row open-ended, the superseded open row closed in record time.
        (await ScalarAsync($"SELECT count(*) FROM rpt_conformed.fact_policy_term WHERE term_id = '{term}'")).ShouldBe(3L);

        var during = await GetAsync($"/api/dat/v1/queries/as-of?view=fact_policy_term&validAt=2026-10-15T00:00:00Z&filters=termId={term}");
        during.Text("rows.0.status").ShouldBe("IN_FORCE");
        during.Text("asMetadata.view").ShouldBe("fact_policy_term");
        var after = await GetAsync($"/api/dat/v1/queries/as-of?view=fact_policy_term&validAt=2026-11-15T00:00:00Z&filters=termId={term}");
        after.Text("rows.0.status").ShouldBe("CANCELLED");
        after.Text("rows.0.productCode").ShouldBe(product);
    }

    [Fact]
    public async Task A_correction_writes_a_new_recorded_row_and_as_of_before_and_after_differ()
    {
        var product = $"HOME-{Guid.NewGuid():N}";
        var term = Guid.NewGuid();
        var invoice = Guid.NewGuid();
        await IngestAsync(Issued(term, product));
        await IngestAsync(Invoice(invoice, term, product, "100.00"));
        var kpiUrl = $"/api/dat/v1/kpis/get?filters=productCode={product}&period=2026-10&validAt=2026-12-31";
        Amount(await GetAsync(kpiUrl), "GWP").ShouldBe(100m);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        await IngestAsync(Invoice(invoice, term, product, "120.00")); // restated invoice: same business key, new event

        // Never an update of the value: both versions exist, the first one closed in record time.
        (await ScalarAsync($"SELECT count(*) FROM rpt_conformed.fact_premium WHERE invoice_id = '{invoice}' AND category = 'PREMIUM'")).ShouldBe(2L);
        (await ScalarAsync($"SELECT count(*) FROM rpt_conformed.fact_premium WHERE invoice_id = '{invoice}' AND category = 'PREMIUM' AND recorded_to IS NULL")).ShouldBe(1L);
        Amount(await GetAsync(kpiUrl), "GWP").ShouldBe(120m);

        var recorded = (DateTime)(await ScalarAsync($"SELECT min(recorded_from) FROM rpt_conformed.fact_premium WHERE invoice_id = '{invoice}' AND amount = 120"))!;
        var before = recorded.AddMilliseconds(-1).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        Amount(await GetAsync($"{kpiUrl}&knownAt={before}"), "GWP").ShouldBe(100m);

        var asOf = await GetAsync($"/api/dat/v1/queries/as-of?view=fact_premium&validAt=2026-12-31&knownAt={before}&filters=productCode={product},invoiceId={invoice}");
        Dec(asOf, "rows.0.amount").ShouldBe(100m);
        var hashBefore = asOf.Text("hash");
        var latest = await GetAsync($"/api/dat/v1/queries/as-of?view=fact_premium&validAt=2026-12-31&filters=productCode={product},invoiceId={invoice}");
        Dec(latest, "rows.0.amount").ShouldBe(120m);
        latest.Text("hash").ShouldNotBe(hashBefore);
    }

    [Fact]
    public async Task Kpis_equal_the_sum_of_the_seeded_events_and_the_aggregate_groups_by_product_and_month()
    {
        var product = $"MOTOR-{Guid.NewGuid():N}";
        var term = Guid.NewGuid();
        var claim = Guid.NewGuid();
        var payment = Guid.NewGuid();
        await IngestAsync(Issued(term, product));
        await IngestAsync(Invoice(Guid.NewGuid(), term, product, "100.00"));
        await IngestAsync(Invoice(Guid.NewGuid(), term, product, "200.00", occurred: "2026-11-05T00:00:00Z"));
        await IngestAsync(Invoice(Guid.NewGuid(), term, product, "50.00", kind: "CREDIT_NOTE", occurred: "2026-11-06T00:00:00Z"));
        await IngestAsync(Reserve(claim, product, "500.00"));
        await IngestAsync(Reserve(claim, product, "-100.00", "2026-10-14T00:00:00Z"));
        await IngestAsync(Paid(claim, payment, "80.00", product));
        await IngestAsync(Envelope(ModuleCode.CLM, "PaymentVoided", "Claim", new JsonObject { ["claimId"] = claim, ["paymentId"] = payment, ["reason"] = "VOIDED" }, "2026-10-13T00:00:00Z", product));
        await IngestAsync(Paid(claim, Guid.NewGuid(), "30.00", product, "2026-11-02T00:00:00Z"));

        var all = await GetAsync($"/api/dat/v1/kpis/get?filters=productCode={product}&validAt=2026-12-31");
        Amount(all, "GWP").ShouldBe(250m); // 100 + 200 - 50 (TAX excluded)
        Amount(all, "CLAIMS_PAID").ShouldBe(30m); // 80 - 80 (voided) + 30
        Amount(all, "OPEN_RESERVE").ShouldBe(400m);
        Dec(all, "values.LOSS_RATIO.ratio").ShouldBe(1.72m); // (30 + 400) / 250
        all.Text("definitionVersion").ShouldBe("1.0");

        var october = await GetAsync($"/api/dat/v1/kpis/get?kpiCode=GWP&filters=productCode={product}&period=2026-10&validAt=2026-12-31");
        Amount(october, "GWP").ShouldBe(100m);
        ((JsonObject)october!["values"]!).Count.ShouldBe(1);

        var aggregate = await GetAsync($"/api/dat/v1/populations/aggregate?productSlot={product}&validAt=2026-12-31");
        aggregate.Text("rowsOrAggregates.0.month").ShouldBe("2026-10");
        Dec(aggregate, "rowsOrAggregates.0.gwp").ShouldBe(100m);
        aggregate.Text("rowsOrAggregates.1.month").ShouldBe("2026-11");
        Dec(aggregate, "rowsOrAggregates.1.gwp").ShouldBe(150m);
        Dec(aggregate, "rowsOrAggregates.1.claimsPaid").ShouldBe(30m);
        aggregate.Text("rowsOrAggregates.0.inForceTerms").ShouldBe("1");

        var page = await GetAsync($"/api/dat/v1/populations/aggregate?productSlot={product}&validAt=2026-12-31&limit=1");
        page.Text("rowsOrAggregates.0.month").ShouldBe("2026-10");
        var cursor = page.Text("nextCursor");
        var second = await GetAsync($"/api/dat/v1/populations/aggregate?productSlot={product}&validAt=2026-12-31&limit=1&cursor={cursor}");
        second.Text("rowsOrAggregates.0.month").ShouldBe("2026-11");

        var rows1 = await GetAsync($"/api/dat/v1/queries/as-of?view=fact_claim_movement&validAt=2026-12-31&filters=productCode={product}&limit=2");
        ((JsonArray)rows1!["rows"]!).Count.ShouldBe(2);
        var rows2 = await GetAsync($"/api/dat/v1/queries/as-of?view=fact_claim_movement&validAt=2026-12-31&filters=productCode={product}&limit=2&cursor={rows1.Text("nextCursor")}");
        rows2.Text("rows.0.rowId").ShouldNotBe(rows1.Text("rows.0.rowId"));
        rows2.Text("rows.0.rowId").ShouldNotBe(rows1.Text("rows.1.rowId"));
    }

    [Fact]
    public async Task Another_legal_entity_never_sees_the_rows()
    {
        var product = $"MARINE-{Guid.NewGuid():N}";
        var term = Guid.NewGuid();
        await IngestAsync(Issued(term, product, legalEntity: "GR-OTHER"));
        await IngestAsync(Invoice(Guid.NewGuid(), term, product, "999.00", legalEntity: "GR-OTHER"));

        // The caller's entity is GR-TEST (stamp): nothing of GR-OTHER is visible.
        Amount(await GetAsync($"/api/dat/v1/kpis/get?filters=productCode={product}&validAt=2026-12-31"), "GWP").ShouldBe(0m);
        var rows = await GetAsync($"/api/dat/v1/queries/as-of?view=fact_premium&validAt=2026-12-31&filters=productCode={product}");
        ((JsonArray)rows!["rows"]!).Count.ShouldBe(0);
        (await ScalarAsync($"SELECT count(*) FROM rpt_conformed.fact_premium WHERE product_code = '{product}' AND legal_entity = 'GR-OTHER'")).ShouldBe(2L);
    }

    [Fact]
    public async Task A_payload_that_cannot_be_conformed_is_quarantined_and_the_raw_event_is_kept()
    {
        var id = Guid.CreateVersion7();
        var broken = Envelope(ModuleCode.BIL, "InvoiceIssued", "BillingAccount", new JsonObject { ["invoiceId"] = Guid.NewGuid(), ["kind"] = "INVOICE" }, "2026-10-05T00:00:00Z", "X", id: id);
        (await IngestAsync(broken)).ShouldBe(IngestOutcome.Ingested);
        (await ScalarAsync($"SELECT count(*) FROM rpt_raw.event_ledger WHERE event_id = '{id}'")).ShouldBe(1L);
        (await ScalarAsync($"SELECT count(*) FROM rpt_raw.quarantine WHERE event_id = '{id}' AND status = 'QUARANTINED' AND reasons::text LIKE '%projection:%'")).ShouldBe(1L);
        (await ScalarAsync($"SELECT count(*) FROM rpt_conformed.projected_event WHERE event_id = '{id}'")).ShouldBe(0L);
    }

    [Fact]
    public async Task Replay_projects_events_missing_from_the_conformed_layer_once()
    {
        var product = $"FIRE-{Guid.NewGuid():N}";
        var term = Guid.NewGuid();
        var invoice = Guid.NewGuid();
        var envelope = Invoice(invoice, term, product, "75.00", occurred: "2026-10-06T00:00:00Z");
        await IngestAsync(envelope);
        // Simulate a projection that never ran (e.g. the projector was added after the events were ingested).
        await ScalarAsync($"DELETE FROM rpt_conformed.fact_premium WHERE invoice_id = '{invoice}'");
        await ScalarAsync($"DELETE FROM rpt_conformed.projected_event WHERE event_id = '{envelope.EventId.Value}'");

        using var client = _factory.CreateClient();
        var body = new { contract = "bil.InvoiceIssued", from = "2026-10-06T00:00:00Z", to = "2026-10-06T00:00:00Z" };
        var (first, one) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/ingestion/replay", body, roles: Admin);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted, one?.ToJsonString());
        one.Text("counts.projected").ShouldBe("1");
        var (_, two) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/ingestion/replay", body, roles: Admin);
        two.Text("counts.projected").ShouldBe("0");
        (await ScalarAsync($"SELECT count(*) FROM rpt_conformed.fact_premium WHERE invoice_id = '{invoice}' AND category = 'PREMIUM'")).ShouldBe(1L);
    }

    [Fact]
    public async Task Non_data_roles_and_bad_input_are_refused()
    {
        foreach (var path in new[] { "/api/dat/v1/queries/as-of?view=fact_premium", "/api/dat/v1/populations/aggregate", "/api/dat/v1/kpis/get" })
        {
            await GetAsync(path, roles: "Staff.Billing", expected: HttpStatusCode.Forbidden);
        }

        await GetAsync("/api/dat/v1/queries/as-of?view=nope", expected: (HttpStatusCode)422);
        await GetAsync("/api/dat/v1/queries/as-of?view=fact_premium&validAt=garbage", expected: HttpStatusCode.BadRequest);
        await GetAsync("/api/dat/v1/kpis/get?kpiCode=NOPE", expected: HttpStatusCode.BadRequest);
        await GetAsync("/api/dat/v1/populations/aggregate?cursor=%%%", expected: HttpStatusCode.BadRequest);
    }
}
