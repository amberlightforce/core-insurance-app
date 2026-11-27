using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Data.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Data;

/// <summary>BO-DAT-05: lineage trace to source events, coverage control totals and the mart run list.</summary>
public sealed class DataLineageTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
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
        Envelope(ModuleCode.POL, "PolicyIssued", "Policy", new JsonObject { ["termId"] = term, ["transactionId"] = Guid.NewGuid(), ["issuedAt"] = "2026-01-01T00:00:00Z", ["documentIds"] = new JsonArray() }, "2026-01-01T00:00:00Z", product);

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

    private async Task<JsonNode?> CallAsync(HttpMethod method, string path, object? body = null, string roles = Admin, string? user = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        if (user is not null)
        {
            request.Headers.Add(TestAuthHandler.UserHeader, user);
        }

        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(expected, text);
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    private static decimal Dec(JsonNode? node) => decimal.Parse(node!.ToString(), CultureInfo.InvariantCulture);

    private Task<JsonNode?> RunAsync(object body, string? user = null) => CallAsync(HttpMethod.Post, "/api/dat/v1/marts/run", body, user: user, expected: HttpStatusCode.Accepted);

    private static JsonNode? CoverageLine(JsonNode? body, string array, string name, string period) =>
        body![array]!.AsArray().FirstOrDefault(l => l!["name"]!.ToString() == name && l["period"]!.ToString() == period);

    [Fact]
    public async Task Trace_reaches_the_source_rows_and_the_raw_event_ids_and_lists_adjustments()
    {
        var product = $"MOTOR-{Guid.NewGuid():N}";
        var term = Guid.NewGuid();
        await IngestAsync(Issued(term, product));
        var first = Invoice(Guid.NewGuid(), term, product, "100.00");
        var second = Invoice(Guid.NewGuid(), term, product, "50.00", "2026-10-20T00:00:00Z");
        await IngestAsync(first);
        await IngestAsync(second);

        var run = await RunAsync(new { mart = "PORTFOLIO_PREMIUM", period = "2026", productCode = product, validAt = "2026-12-31" }, "maker");
        var runId = run.Text("runId");
        var key = $"GWP|{product}|2026-10|";
        await CallAsync(HttpMethod.Post, "/api/dat/v1/marts/adjust", new { runId, figureKey = key, amount = new { amount = "-5.00", currency = "EUR" }, reason = "Credit", evidence = "DOC-9" }, user: "maker");

        var trace = await CallAsync(HttpMethod.Get, $"/api/dat/v1/lineage/trace?runId={runId}&figureKey={Uri.EscapeDataString(key)}");
        var refs = trace!["sourceRecordsRef"]!;
        refs.Text("sourceCount").ShouldBe("2");
        refs.Text("tracedCount").ShouldBe("2");
        refs["complete"]!.GetValue<bool>().ShouldBeTrue();
        var rows = refs["rows"]!.AsArray();
        rows.Count.ShouldBe(2);
        rows.Select(r => r!["source"]!.ToString()).Distinct().Single().ShouldBe("rpt_conformed.fact_premium");
        var eventIds = rows.Select(r => r!["sourceEventId"]!.ToString()).ToHashSet();
        eventIds.ShouldBe(new HashSet<string> { first.EventId.ToString(), second.EventId.ToString() }, ignoreOrder: true);
        refs["rawEvents"]!.AsArray().Select(e => e!["eventId"]!.ToString()).ToHashSet().ShouldBe(eventIds, ignoreOrder: true);
        refs["rawEvents"]!.AsArray().All(e => e!["registry"]!.ToString() == "bil.InvoiceIssued").ShouldBeTrue();
        refs["adjustments"]!.AsArray().Single()!["reason"]!.ToString().ShouldBe("Credit");
        trace!["graph"]!["nodes"]!.AsArray().Count.ShouldBeGreaterThanOrEqualTo(4);
        trace["nextCursor"].ShouldBeNull();

        // The node shorthand and paging by limit reach the same rows.
        var paged = await CallAsync(HttpMethod.Get, $"/api/dat/v1/lineage/trace?node={runId}/{Uri.EscapeDataString(key)}&limit=1");
        paged!["sourceRecordsRef"]!["rows"]!.AsArray().Count.ShouldBe(1);
        var cursor = paged.Text("nextCursor");
        cursor.ShouldNotBeNullOrEmpty();
        var rest = await CallAsync(HttpMethod.Get, $"/api/dat/v1/lineage/trace?runId={runId}&figureKey={Uri.EscapeDataString(key)}&limit=1&cursor={cursor}");
        rest!["sourceRecordsRef"]!["rows"]!.AsArray().Single()!["sourceEventId"]!.ToString().ShouldNotBe(paged["sourceRecordsRef"]!["rows"]![0]!["sourceEventId"]!.ToString());
        rest["nextCursor"].ShouldBeNull();
    }

    [Fact]
    public async Task Trace_of_the_claims_loss_ratio_covers_premium_and_claim_rows_and_the_ri_mart_traces_raw_cessions()
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
        var trace = await CallAsync(HttpMethod.Get, $"/api/dat/v1/lineage/trace?runId={run.Text("runId")}&figureKey={Uri.EscapeDataString($"LOSS_RATIO|{product}||")}");
        var refs = trace!["sourceRecordsRef"]!;
        refs["complete"]!.GetValue<bool>().ShouldBeTrue();
        refs.Text("tracedCount").ShouldBe("3");
        refs["rows"]!.AsArray().Select(r => r!["source"]!.ToString()).Distinct().Order().ToArray()
            .ShouldBe(["rpt_conformed.fact_claim_movement", "rpt_conformed.fact_premium"]);

        var contract = Guid.NewGuid();
        var cession = Guid.CreateVersion7();
        var envelope = new JsonObject { ["payload"] = new JsonObject { ["riContractId"] = contract, ["cededAmount"] = Money3("40.00"), ["commissionAmount"] = Money3("4.00") } };
        await ScalarAsync($"""
            INSERT INTO rpt_raw.event_ledger (event_id, registry_name, event_type, producer, schema_version, aggregate_type, aggregate_id, aggregate_sequence, occurred_at, legal_entity, origin, envelope, received_at)
            VALUES ('{cession}', 'ri.CessionCalculated', 'CessionCalculated', 'RI', '1.0', 'Policy', '{Guid.NewGuid()}', 1, '2026-10-15T00:00:00Z', '{Entity}', 'LIVE', '{envelope.ToJsonString()}'::jsonb, '2026-10-15T00:00:00Z')
            """);
        var ri = await RunAsync(new { mart = "RI_CESSION_SUMMARY", period = "2026-10", validAt = "2026-12-31" });
        var riTrace = await CallAsync(HttpMethod.Get, $"/api/dat/v1/lineage/trace?runId={ri.Text("runId")}&figureKey={Uri.EscapeDataString($"CEDED_PREMIUM|ALL|2026-10|{contract}")}");
        var riRefs = riTrace!["sourceRecordsRef"]!;
        riRefs["complete"]!.GetValue<bool>().ShouldBeTrue();
        riRefs["rows"]!.AsArray().Single()!["sourceEventId"]!.ToString().ShouldBe(cession.ToString());
        Dec(riRefs["rows"]![0]!["amount"]).ShouldBe(40m);
        riRefs["rawEvents"]!.AsArray().Single()!["registry"]!.ToString().ShouldBe("ri.CessionCalculated");
    }

    [Fact]
    public async Task Trace_refuses_bad_input_unknown_runs_and_other_roles()
    {
        await CallAsync(HttpMethod.Get, "/api/dat/v1/lineage/trace", expected: HttpStatusCode.BadRequest);
        await CallAsync(HttpMethod.Get, $"/api/dat/v1/lineage/trace?runId={Guid.NewGuid()}&figureKey=x&direction=downstream", expected: HttpStatusCode.BadRequest);
        await CallAsync(HttpMethod.Get, $"/api/dat/v1/lineage/trace?runId={Guid.NewGuid()}&figureKey=x", expected: HttpStatusCode.NotFound);
        await CallAsync(HttpMethod.Get, $"/api/dat/v1/lineage/trace?runId={Guid.NewGuid()}&figureKey=x", roles: "Staff.Billing", expected: HttpStatusCode.Forbidden);
        await CallAsync(HttpMethod.Get, "/api/dat/v1/coverage/status", roles: "Staff.Billing", expected: HttpStatusCode.Forbidden);
        await CallAsync(HttpMethod.Get, "/api/dat/v1/coverage/status?period=nonsense", expected: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Coverage_counts_projected_quarantined_unhandled_and_shows_a_dropped_projection_as_a_gap()
    {
        var product = $"COV-{Guid.NewGuid():N}";
        var month = DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture); // the period is the ingestion (received) month
        var previous = DateTime.UtcNow.AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var term = Guid.NewGuid();
        await IngestAsync(Issued(term, product));
        var kept = Invoice(Guid.NewGuid(), term, product, "10.00", "2026-03-05T00:00:00Z");
        var dropped = Invoice(Guid.NewGuid(), term, product, "20.00", "2026-03-06T00:00:00Z");
        await IngestAsync(kept);
        await IngestAsync(dropped);

        // An unhandled type (POL PolicyBound has no conformed fact) and an event refused at ingestion.
        await IngestAsync(Envelope(ModuleCode.POL, "PolicyBound", "Policy", new JsonObject { ["termId"] = term }, "2026-03-07T00:00:00Z", product));
        var refused = new JsonObject { ["legalEntity"] = Entity }.ToJsonString();
        await ScalarAsync($"""
            INSERT INTO rpt_raw.quarantine (quarantine_id, event_id, registry_name, reasons, envelope, status, received_at)
            VALUES ('{Guid.CreateVersion7()}', '{Guid.NewGuid()}', 'bil.InvoiceIssued', '["bad"]'::jsonb, '{refused}'::jsonb, 'QUARANTINED', now())
            """);

        var before = await CallAsync(HttpMethod.Get, $"/api/dat/v1/coverage/status?period={month}");
        var premium = CoverageLine(before, "tables", "fact_premium", month)!;
        Dec(premium["received"]).ShouldBeGreaterThanOrEqualTo(2m);
        Dec(premium["gap"]).ShouldBe(0m);
        CoverageLine(before, "notHandled", "pol.PolicyBound", month).ShouldNotBeNull();

        // The projection of one event is lost (its guard row and conformed rows vanish): it becomes a gap.
        await ScalarAsync($"DELETE FROM rpt_conformed.projected_event WHERE event_id = '{dropped.EventId}'");
        var after = await CallAsync(HttpMethod.Get, $"/api/dat/v1/coverage/status?period={month}");
        var gapped = CoverageLine(after, "tables", "fact_premium", month)!;
        Dec(gapped["gap"]).ShouldBe(1m);
        Dec(gapped["projected"]).ShouldBe(Dec(premium["projected"]) - 1m);
        Dec(after!["gapCount"]).ShouldBe(1m);
        var mart = CoverageLine(after, "marts", "PORTFOLIO_PREMIUM", month)!;
        Dec(mart["gap"]).ShouldBe(1m);
        Dec(mart["quarantined"]).ShouldBe(Dec(premium["quarantined"]));
        Dec(mart["quarantined"]).ShouldBeGreaterThanOrEqualTo(1m);

        // Another period is unaffected.
        var other = await CallAsync(HttpMethod.Get, $"/api/dat/v1/coverage/status?period={previous}");
        CoverageLine(other, "tables", "fact_premium", previous).ShouldBeNull();
    }

    [Fact]
    public async Task Mart_run_list_is_newest_first_keyset_paged_filtered_and_shows_sign_off_validity()
    {
        var product = $"LIST-{Guid.NewGuid():N}";
        var ids = new List<string>();
        foreach (var user in new[] { "maker", "maker", "maker" })
        {
            var run = await RunAsync(new { mart = "PORTFOLIO_PREMIUM", period = "2026", productCode = product, validAt = "2026-12-31" }, user);
            ids.Add(run.Text("runId"));
        }

        var ri = await RunAsync(new { mart = "RI_CESSION_SUMMARY", period = "2026-10", validAt = "2026-12-31" }, "maker");

        var all = await CallAsync(HttpMethod.Get, "/api/dat/v1/marts/runs?limit=200");
        var listed = all!["items"]!.AsArray().Select(i => i!["runId"]!.ToString()).ToList();
        listed.IndexOf(ri.Text("runId")).ShouldBeLessThan(listed.IndexOf(ids[2]));
        listed.IndexOf(ids[2]).ShouldBeLessThan(listed.IndexOf(ids[1]));
        listed.IndexOf(ids[1]).ShouldBeLessThan(listed.IndexOf(ids[0]));
        var item = all["items"]!.AsArray().Single(i => i!["runId"]!.ToString() == ids[0])!;
        item.Text("mart").ShouldBe("PORTFOLIO_PREMIUM");
        item.Text("status").ShouldBe("COMPLETED");
        item.Text("createdBy").ShouldNotBeNullOrEmpty();
        item.Text("snapshotHash").Length.ShouldBe(64);

        var filtered = await CallAsync(HttpMethod.Get, "/api/dat/v1/marts/runs?mart=PORTFOLIO_PREMIUM&limit=2");
        filtered!["items"]!.AsArray().Count.ShouldBe(2);
        filtered["items"]!.AsArray().All(i => i!["mart"]!.ToString() == "PORTFOLIO_PREMIUM").ShouldBeTrue();
        var next = await CallAsync(HttpMethod.Get, $"/api/dat/v1/marts/runs?mart=PORTFOLIO_PREMIUM&limit=2&cursor={filtered.Text("nextCursor")}");
        next!["items"]!.AsArray().Select(i => i!["runId"]!.ToString()).ShouldNotContain(filtered["items"]![0]!["runId"]!.ToString());

        // A second person signs off: SIGNED_OFF with a valid sign-off; a later adjustment invalidates it again.
        var runId = ids[2];
        await CallAsync(HttpMethod.Post, "/api/dat/v1/marts/sign-off", new { runId }, user: "checker");
        var signed = (await CallAsync(HttpMethod.Get, "/api/dat/v1/marts/runs?limit=200"))!["items"]!.AsArray().Single(i => i!["runId"]!.ToString() == runId)!;
        signed.Text("status").ShouldBe("SIGNED_OFF");
        signed["signOff"]!["valid"]!.GetValue<bool>().ShouldBeTrue();

        await CallAsync(HttpMethod.Get, "/api/dat/v1/marts/runs?mart=NOPE", expected: HttpStatusCode.BadRequest);
        await CallAsync(HttpMethod.Get, "/api/dat/v1/marts/runs?cursor=%%%", expected: HttpStatusCode.BadRequest);
        await CallAsync(HttpMethod.Get, "/api/dat/v1/marts/runs", roles: "Staff.Billing", expected: HttpStatusCode.Forbidden);
    }
}
