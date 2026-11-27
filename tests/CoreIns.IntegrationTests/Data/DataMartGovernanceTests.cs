using System.Globalization;
using System.Net;
using System.Net.Http.Json;
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

/// <summary>BO-DAT-04: adjust, sign-off (second person) and export of a mart run.</summary>
public sealed class DataMartGovernanceTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private const string Entity = "GR-TEST";
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static JsonObject Money(string amount) => new() { ["amount"] = amount, ["currency"] = "EUR" };

    private static EventEnvelope Envelope(ModuleCode producer, string eventType, string aggregateType, string product, JsonObject payload) => new()
    {
        EventId = EventId.New(),
        EventType = EventTypeName.Parse(eventType),
        SchemaVersion = "1.0",
        Producer = producer,
        AggregateType = aggregateType,
        AggregateId = Guid.CreateVersion7().ToString(),
        AggregateSequence = 1,
        OccurredAt = Instant.Parse("2026-10-05T00:00:00Z"),
        RecordedAt = Instant.Parse("2026-10-05T00:00:00Z"),
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

    private async Task<string> SeedRunAsync(string user)
    {
        var product = $"MOTOR-{Guid.NewGuid():N}";
        var term = Guid.NewGuid();
        var issued = Envelope(ModuleCode.POL, "PolicyIssued", "Policy", product, new JsonObject { ["termId"] = term, ["transactionId"] = Guid.NewGuid(), ["issuedAt"] = "2026-10-01T00:00:00Z", ["documentIds"] = new JsonArray() });
        var invoice = Envelope(ModuleCode.BIL, "InvoiceIssued", "BillingAccount", product, new JsonObject
        {
            ["invoiceId"] = Guid.NewGuid(),
            ["kind"] = "INVOICE",
            ["termIds"] = new JsonArray(term.ToString()),
            ["totalsByCategory"] = new JsonArray(new JsonObject { ["category"] = "PREMIUM", ["amount"] = Money("100.00") }),
        });
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var ingestion = scope.ServiceProvider.GetRequiredService<EventIngestion>();
            await ingestion.IngestAsync(issued, TestContext.Current.CancellationToken);
            await ingestion.IngestAsync(invoice, TestContext.Current.CancellationToken);
        }

        var run = await SendAsUserAsync("/api/dat/v1/marts/run", new { mart = "PORTFOLIO_PREMIUM", period = "2026", productCode = product, validAt = "2026-12-31" }, user, HttpStatusCode.Accepted);
        return run.Text("runId") + "|" + product;
    }

    private async Task<JsonNode?> SendAsUserAsync(string path, object? body, string user, HttpStatusCode expected, HttpMethod? method = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, Admin);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        if (request.Method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = text.Length == 0 ? null : JsonNode.Parse(text);
        response.StatusCode.ShouldBe(expected, text);
        return json;
    }

    private async Task<long> AuditCountAsync(string operation, string runId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM plt.audit_event WHERE operation = '{operation}' AND outcome = 'Succeeded' AND object_id = '{runId}'", connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static object Adjust(string runId, string key, string amount, string reason = "Correction", string evidence = "DOC-1") =>
        new { runId, figureKey = key, amount = new { amount, currency = "EUR" }, reason, evidence };

    [Fact]
    public async Task Adjustment_adds_a_line_but_never_changes_the_stored_figure_and_is_audited()
    {
        var parts = (await SeedRunAsync("maker")).Split('|');
        var runId = parts[0];
        var key = $"GWP|{parts[1]}|2026-10|";
        var adjusted = await SendAsUserAsync("/api/dat/v1/marts/adjust", Adjust(runId, key, "-12.50", "Late credit note", "DOC-1234"), "maker", HttpStatusCode.OK);
        adjusted.Text("adjustedValue").ShouldBe("87.5000");

        var get = await SendAsUserAsync($"/api/dat/v1/marts/{runId}", null, "maker", HttpStatusCode.OK, HttpMethod.Get);
        var figure = get!["figures"]!.AsArray().Single(f => f!["key"]!.ToString() == key)!;
        decimal.Parse(figure["value"]!.ToString(), CultureInfo.InvariantCulture).ShouldBe(100m);
        decimal.Parse(figure["adjustedValue"]!.ToString(), CultureInfo.InvariantCulture).ShouldBe(87.5m);
        get["run"]!["adjustments"]!.AsArray().Count.ShouldBe(1);
        (await AuditCountAsync("dat.Mart.adjust", runId)).ShouldBe(1);

        // Reason and evidence are required; a count figure cannot be adjusted.
        await SendAsUserAsync("/api/dat/v1/marts/adjust", Adjust(runId, key, "1.00", reason: ""), "maker", HttpStatusCode.BadRequest);
        await SendAsUserAsync("/api/dat/v1/marts/adjust", Adjust(runId, $"PREMIUM_LINES|{parts[1]}|2026-10|", "1.00"), "maker", HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Signoff_needs_a_second_person_export_needs_signoff_and_a_later_adjustment_invalidates_it()
    {
        var parts = (await SeedRunAsync("maker")).Split('|');
        var runId = parts[0];
        var key = $"GWP|{parts[1]}|2026-10|";

        await SendAsUserAsync("/api/dat/v1/marts/export", new { runId, format = "CSV" }, "checker", HttpStatusCode.UnprocessableEntity); // unsigned
        await SendAsUserAsync("/api/dat/v1/marts/sign-off", new { runId }, "maker", HttpStatusCode.UnprocessableEntity); // the maker ran it

        await SendAsUserAsync("/api/dat/v1/marts/adjust", Adjust(runId, key, "5.00"), "adjuster", HttpStatusCode.OK);
        await SendAsUserAsync("/api/dat/v1/marts/sign-off", new { runId }, "adjuster", HttpStatusCode.UnprocessableEntity); // the adjuster

        var signed = await SendAsUserAsync("/api/dat/v1/marts/sign-off", new { runId, comment = "Checked" }, "checker", HttpStatusCode.OK);
        signed.Text("status").ShouldBe("SIGNED_OFF");
        (await AuditCountAsync("dat.Mart.signOff", runId)).ShouldBe(1);
        var get = await SendAsUserAsync($"/api/dat/v1/marts/{runId}", null, "checker", HttpStatusCode.OK, HttpMethod.Get);
        get.Text("run.status").ShouldBe("SIGNED_OFF");

        var csv = await SendAsUserAsync("/api/dat/v1/marts/export", new { runId, format = "CSV" }, "checker", HttpStatusCode.OK);
        var again = await SendAsUserAsync("/api/dat/v1/marts/export", new { runId, format = "CSV" }, "checker", HttpStatusCode.OK);
        csv.Text("content").ShouldBe(again.Text("content"));
        csv.Text("manifestHash").ShouldBe(again.Text("manifestHash"));
        csv.Text("content").ShouldContain($"# snapshotHash: {csv.Text("snapshotHash")}");
        csv.Text("content").ShouldContain($"{key},GWP,{parts[1]},2026-10,,EUR,100.0000,5.0000,105.0000,1");
        var json = await SendAsUserAsync("/api/dat/v1/marts/export", new { runId, format = "JSON" }, "checker", HttpStatusCode.OK);
        JsonNode.Parse(json.Text("content"))!["header"]!["snapshotHash"]!.ToString().ShouldBe(csv.Text("snapshotHash"));

        // A later adjustment invalidates the sign-off: export is refused until a (second) person signs again.
        var late = await SendAsUserAsync("/api/dat/v1/marts/adjust", Adjust(runId, key, "1.00", "Late", "DOC-2"), "late", HttpStatusCode.OK);
        late.Text("signOffInvalidated").ShouldBe("true");
        await SendAsUserAsync("/api/dat/v1/marts/export", new { runId, format = "CSV" }, "checker", HttpStatusCode.UnprocessableEntity);
        (await SendAsUserAsync($"/api/dat/v1/marts/{runId}", null, "checker", HttpStatusCode.OK, HttpMethod.Get)).Text("run.status").ShouldBe("COMPLETED");
        await SendAsUserAsync("/api/dat/v1/marts/sign-off", new { runId }, "late", HttpStatusCode.UnprocessableEntity);
        await SendAsUserAsync("/api/dat/v1/marts/sign-off", new { runId }, "checker", HttpStatusCode.OK);
        await SendAsUserAsync("/api/dat/v1/marts/export", new { runId, format = "CSV" }, "checker", HttpStatusCode.OK);
    }
}
