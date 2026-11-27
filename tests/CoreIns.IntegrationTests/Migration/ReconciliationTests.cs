using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Migration;

/// <summary>
/// BO-MIG-10: the reconciliation engine. Legacy control totals are loaded per scope and compared with what BIL, FIN and RI hold of the migrated
/// batch (read through their reconciliation readers), per object type, key and currency, to the cent. Amounts are illustrative.
/// </summary>
public sealed class ReconciliationTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Roles = "Platform.Admin,Staff.MigrationManager";
    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _client = _factory.CreateClient();
        _db = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task A_matching_run_is_green_and_a_seeded_one_cent_difference_is_reported_with_its_key()
    {
        var (batch, _) = await ImportAsync(receivable1: 250.35m, receivable2: 100.00m, receivableLedger: 350.35m, ledgerOther: 0m);
        var scope = "match-" + Guid.NewGuid().ToString("N");
        var receivablesBefore = await CountAsync($"SELECT count(*) FROM bil.migration_receivable WHERE batch_id = '{batch}'");

        await LoadAsync(scope, Total("BIL", "Receivable", "", 2, "350.35"), Total("FIN", "OpeningBalance", "", 1, "350.35"), Total("FIN", "OpeningBalance", "GL-1210", 1, "350.35"),
            Total("RI", "TreatyYear", "", 1, "1200.00"));
        var (response, green) = await SendAsync(HttpMethod.Post, "/api/mig/v1/reconciliation/run", new { scope, batchId = batch });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, green?.ToJsonString());
        green!["green"]!.GetValue<bool>().ShouldBeTrue(green.ToJsonString());
        green["varianceCount"]!.GetValue<int>().ShouldBe(0);
        green["results"]!.AsArray().Select(r => r.Text("objectType")).ShouldContain("ReceivableVsLedger");

        // The run is read-only on the modules.
        (await CountAsync($"SELECT count(*) FROM bil.migration_receivable WHERE batch_id = '{batch}'")).ShouldBe(receivablesBefore);

        // Reload with one cent less outstanding in the legacy: variance reported on exactly that key; the stored run is readable.
        await LoadAsync(scope, Total("BIL", "Receivable", "", 2, "350.36"), Total("FIN", "OpeningBalance", "", 1, "350.35"), Total("FIN", "OpeningBalance", "GL-1210", 1, "350.35"),
            Total("RI", "TreatyYear", "", 1, "1200.00"));
        var (_, red) = await SendAsync(HttpMethod.Post, "/api/mig/v1/reconciliation/run", new { scope, batchId = batch });
        red!["green"]!.GetValue<bool>().ShouldBeFalse();
        red["variances"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray().ShouldBe(["BIL/Receivable//EUR"]);
        var row = red["results"]!.AsArray().Single(r => r.Text("status") == "VARIANCE");
        row.Text("legacyAmount").ShouldBe("350.36");
        row.Text("coreAmount").ShouldBe("350.35");
        row.Text("amountVariance").ShouldBe("-0.01");

        var (getResponse, latest) = await SendAsync(HttpMethod.Get, $"/api/mig/v1/reconciliation/results?scope={scope}", null);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK, latest?.ToJsonString());
        latest.Text("runId").ShouldBe(red.Text("runId"));
        (await SendAsync(HttpMethod.Get, $"/api/mig/v1/reconciliation/results?runId={green.Text("runId")}", null)).Body!["green"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task The_bil_receivable_must_equal_the_fin_receivable_account_per_currency_and_a_missing_total_or_a_bad_amount_is_refused()
    {
        // BIL opens 350.35; FIN opens only 350.00 on the receivable account (0.35 sits on GL-1215): the legacy totals agree with each module,
        // the BIL-vs-FIN cross-check does not.
        var (batch, _) = await ImportAsync(receivable1: 250.35m, receivable2: 100.00m, receivableLedger: 350.00m, ledgerOther: 0.35m);
        var scope = "xcheck-" + Guid.NewGuid().ToString("N");
        await LoadAsync(scope, Total("BIL", "Receivable", "", 2, "350.35"), Total("FIN", "OpeningBalance", "", 1, "350.35"), Total("FIN", "OpeningBalance", "GL-1210", 1, "350.00"));
        var (_, report) = await SendAsync(HttpMethod.Post, "/api/mig/v1/reconciliation/run", new { scope, batchId = batch });
        report!["variances"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray().ShouldBe(["BIL/FIN/ReceivableVsLedger/GL-1210/EUR"]);
        var cross = report["results"]!.AsArray().Single(r => r.Text("objectType") == "ReceivableVsLedger");
        cross.Text("legacyAmount").ShouldBe("350.35");
        cross.Text("coreAmount").ShouldBe("350.00");
        cross.Text("amountVariance").ShouldBe("-0.35");

        // A currency the core does not hold is MISSING_CORE; more than two decimals and an empty scope are refused.
        var missing = "missing-" + Guid.NewGuid().ToString("N");
        await LoadAsync(missing, Total("BIL", "Receivable", "", 1, "10.00", "USD"));
        var (_, missingReport) = await SendAsync(HttpMethod.Post, "/api/mig/v1/reconciliation/run", new { scope = missing, batchId = batch });
        missingReport!["results"]!.AsArray().Single(r => r.Text("currency") == "USD").Text("status").ShouldBe("MISSING_CORE");

        var (bad, _) = await SendAsync(HttpMethod.Post, "/api/mig/v1/reconciliation/control-totals/load", new { scope = "bad", totals = new[] { Total("BIL", "Receivable", "", 1, "10.005") } });
        bad.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/reconciliation/run", new { scope = "nothing-loaded-" + Guid.NewGuid().ToString("N") })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    private static object Total(string module, string objectType, string key, long count, string amount, string currency = "EUR") =>
        new { module, objectType, key, currency, count, amount };

    private async Task LoadAsync(string scope, params object[] totals)
    {
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/reconciliation/control-totals/load", new { scope, totals });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
    }

    /// <summary>Imports two receivables, one opening balance (GL-1210 and GL-1215 against GL-2110) and one RI treaty year as one batch.</summary>
    private async Task<(string Batch, string Id)> ImportAsync(decimal receivable1, decimal receivable2, decimal receivableLedger, decimal ledgerOther)
    {
        var id = Guid.NewGuid().ToString("N");
        var payer = Guid.NewGuid();
        var opening = $"TB-{id}";
        var lines = new List<object> { new { accountCode = "GL-1210", debit = receivableLedger, credit = 0m } };
        if (ledgerOther > 0m)
        {
            lines.Add(new { accountCode = "GL-1215", debit = ledgerOther, credit = 0m });
        }

        var total = receivableLedger + ledgerOther;
        lines.Add(new { accountCode = "GL-2110", debit = 0m, credit = total });
        var items = new object[]
        {
            Receivable($"INV-{id}-1", payer, receivable1),
            Receivable($"INV-{id}-2", payer, receivable2),
            new
            {
                module = "FIN", objectType = "OpeningBalance", legacyId = opening,
                payload = new { legacyId = opening, accountingDate = "2026-09-30", currency = "EUR", lines, trialBalance = new { debit = total, credit = total } },
            },
            new { module = "RI", objectType = "TreatyYear", legacyId = $"TY-{id}", payload = new { treatyId = $"XOL-{id}", contractYear = 2026, openingAggregate = 1200.00m } },
        };
        var (created, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches", new { name = "recon", items });
        created.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var batch = body.Text("batch.batchId");
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId = batch, dryRun = true })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId = batch, dryRun = false })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (approved, done) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId = batch, decision = "APPROVE" }, "bob");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, done?.ToJsonString());
        done!["outcomes"]!.AsArray().Select(o => o.Text("outcome")).ShouldAllBe(o => o == "IMPORTED", done.ToJsonString());
        return (batch, id);
    }

    private static object Receivable(string invoice, Guid payer, decimal outstanding) => new
    {
        module = "BIL", objectType = "Receivable", legacyId = invoice,
        payload = new
        {
            legacyInvoiceNumber = invoice, payerPartyId = payer, policyLegacyNumber = "LEG-POL-1", dueDate = "2026-09-01", originalAmount = outstanding + 10m,
            outstanding, openItems = new[] { new { amount = outstanding, description = "premium instalment" } },
        },
    };

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body, string user = "alice")
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, Roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.AcceptLanguage.ParseAdd("en");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await _client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var command = _db.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}
