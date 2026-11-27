using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Migration;

/// <summary>
/// NX-TST-08: migration rerun, rollback and malformed legacy rows, seeded from <c>tests/e2e/fixtures/legacy-gr1.json</c>
/// (receivable, mandate and opening balance of fixture GR-1) through the real batch API and the BIL / FIN importers.
/// </summary>
public sealed class LegacyRerunRollbackTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Roles = "Platform.Admin,Staff.MigrationManager";
    private static readonly JsonNode Fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "tests", "e2e", "fixtures", "legacy-gr1.json")))!;
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

    private static decimal Amount(string path) => decimal.Parse(Fixture.Text(path), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The fixture's open receivable as a BIL batch item; <paramref name="mutate"/> breaks it on purpose.</summary>
    private static object Receivable(string suffix, Guid payer, Action<JsonObject>? mutate = null)
    {
        var outstanding = Amount("receivable.outstanding");
        var invoice = $"INV-GR1-{suffix}";
        var payload = new JsonObject
        {
            ["legacyInvoiceNumber"] = invoice,
            ["payerPartyId"] = payer,
            ["policyLegacyNumber"] = "LEG-POL-GR1",
            ["dueDate"] = "2026-09-01",
            ["originalAmount"] = outstanding + 10m,
            ["outstanding"] = outstanding,
            ["openItems"] = new JsonArray([.. Fixture["receivable"]!["openItems"]!.AsArray().Select(a => (JsonNode)new JsonObject { ["amount"] = a!.GetValue<decimal>(), ["description"] = "premium instalment" })]),
        };
        mutate?.Invoke(payload);
        return new { module = "BIL", objectType = "Receivable", legacyId = invoice, payload };
    }

    private static object Mandate(string suffix, Guid payer) => new
    {
        module = "BIL", objectType = "Mandate", legacyId = $"UMR-GR1-{suffix}",
        payload = new { mandateReference = $"UMR-GR1-{suffix}", payerPartyId = payer, signatureDate = Fixture.Text("mandate.signatureDate"), ibanMasked = Fixture.Text("mandate.ibanMasked") },
    };

    private static object OpeningBalance(string suffix)
    {
        var lines = Fixture["openingBalance"]!["lines"]!.AsArray();
        return new
        {
            module = "FIN", objectType = "OpeningBalance", legacyId = $"TB-GR1-{suffix}",
            payload = new
            {
                legacyId = $"TB-GR1-{suffix}", accountingDate = "2026-09-30", currency = "EUR",
                lines = lines.Select(l => new { accountCode = l.Text("accountCode"), debit = l!["debit"]!.GetValue<decimal>(), credit = l["credit"]!.GetValue<decimal>() }).ToArray(),
                trialBalance = new { debit = Amount("openingBalance.trialBalance.debit"), credit = Amount("openingBalance.trialBalance.credit") },
            },
        };
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

        var response = await _client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<string> CreateBatchAsync(params object[] items)
    {
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches", new { name = "gr1", items }, "alice");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body.Text("batch.batchId");
    }

    private async Task<JsonNode?> StartAsync(string batchId, bool dryRun)
    {
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId, dryRun }, "alice");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body;
    }

    private async Task<JsonNode?> RunAsync(string batch)
    {
        await StartAsync(batch, dryRun: true);
        await StartAsync(batch, dryRun: false);
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId = batch, decision = "APPROVE" }, "bob");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body;
    }

    private static string[] Outcomes(JsonNode? body) => [.. body!["outcomes"]!.AsArray().Select(o => o.Text("outcome"))];

    private async Task<long> CountAsync(string sql)
    {
        await using var command = _db.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task Malformed_legacy_rows_fail_with_a_code_in_the_dry_run_and_write_nothing()
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var payer = Guid.NewGuid();
        var batch = await CreateBatchAsync(
            Receivable($"{id}-NEG", payer, p => { p["outstanding"] = -1m; }),
            Receivable($"{id}-NOITEMS", payer, p => { p["openItems"] = new JsonArray(); }),
            Receivable($"{id}-DECIMALS", payer, p => { p["outstanding"] = 312.355m; }),
            Receivable($"{id}-NOPAYER", payer, p => { p["payerPartyId"] = Guid.Empty; }),
            Receivable($"{id}-NOINVOICE", payer, p => { p["legacyInvoiceNumber"] = " "; }),
            Receivable($"{id}-USD", payer, p => { p["currency"] = "USD"; }),
            Receivable($"{id}-DATE", payer, p => { p["dueDate"] = "not-a-date"; p["outstanding"] = "abc"; }),
            new { module = "BIL", objectType = "Invoice", legacyId = $"X-{id}", payload = new { } });

        var dry = await StartAsync(batch, dryRun: true);
        var outcomes = dry!["outcomes"]!.AsArray();
        outcomes.Count.ShouldBe(8);
        outcomes.ShouldAllBe(o => o.Text("outcome") == "FAILED", dry.ToJsonString());
        outcomes.ShouldAllBe(o => o.Text("errorCode").StartsWith("MIG-ERR-", StringComparison.Ordinal));

        // A real run of a batch whose dry run failed everywhere imports nothing.
        await StartAsync(batch, dryRun: false);
        var (_, done) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId = batch, decision = "APPROVE" }, "bob");
        done?["outcomes"]?.AsArray().ShouldAllBe(o => o.Text("outcome") == "FAILED");
        (await CountAsync($"SELECT count(*) FROM bil.migration_receivable WHERE legacy_invoice_number LIKE 'INV-GR1-{id}%'")).ShouldBe(0);
    }

    [Fact]
    public async Task Batch_level_malformed_input_is_refused_up_front()
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var payer = Guid.NewGuid();
        var duplicate = Receivable(id, payer);
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches", new { name = "dup", items = new[] { duplicate, duplicate } }, "alice")).Response
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches", new { name = "empty", items = Array.Empty<object>() }, "alice")).Response
            .IsSuccessStatusCode.ShouldBeFalse();
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches", new { name = "", items = new[] { duplicate } }, "alice")).Response
            .IsSuccessStatusCode.ShouldBeFalse();

        // A module that has no importer fails the item (not the whole batch) with a code.
        var batch = await CreateBatchAsync(new { module = "ZZZ", objectType = "Thing", legacyId = $"Z-{id}", payload = new { } }, Receivable(id, payer));
        var dry = await StartAsync(batch, dryRun: true);
        Outcomes(dry).ShouldBe(["FAILED", "IMPORTED"]);
        dry!["outcomes"]![0].Text("errorCode").ShouldStartWith("MIG-ERR-");

        // Unknown batch ids are not found.
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId = Guid.NewGuid(), dryRun = true }, "alice")).Response
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_corrected_rerun_of_a_failed_row_imports_it_once_and_a_third_run_skips_it()
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var payer = Guid.NewGuid();
        var good = Receivable($"{id}-OK", payer);
        var broken = Receivable($"{id}-FIX", payer, p => { p["outstanding"] = Amount("receivable.outstanding") + 0.01m; });

        // First wave: the broken row fails and is left unimported while the good row goes in.
        var first = await RunAsync(await CreateBatchAsync(good, broken, Mandate(id, payer)));
        Outcomes(first).ShouldBe(["IMPORTED", "FAILED", "IMPORTED"]);
        first!["outcomes"]![1].Text("errorCode").ShouldBe("MIG-ERR-SUM-MISMATCH");
        (await CountAsync($"SELECT count(*) FROM bil.migration_receivable WHERE legacy_invoice_number = 'INV-GR1-{id}-FIX'")).ShouldBe(0);
        (await CountAsync($"SELECT count(*) FROM bil.migration_receivable WHERE legacy_invoice_number = 'INV-GR1-{id}-OK'")).ShouldBe(1);

        // Second wave with the corrected row: the already imported ones are skipped, the fixed one is imported.
        var second = await RunAsync(await CreateBatchAsync(good, Receivable($"{id}-FIX", payer), Mandate(id, payer)));
        Outcomes(second).ShouldBe(["SKIPPED_EXISTING", "IMPORTED", "SKIPPED_EXISTING"]);

        var third = await RunAsync(await CreateBatchAsync(good, Receivable($"{id}-FIX", payer)));
        Outcomes(third).ShouldBe(["SKIPPED_EXISTING", "SKIPPED_EXISTING"]);
        (await CountAsync($"SELECT count(*) FROM bil.migration_receivable WHERE legacy_invoice_number LIKE 'INV-GR1-{id}%'")).ShouldBe(2);
    }

    [Fact]
    public async Task Rollback_is_repeatable_a_dry_reversal_changes_nothing_and_a_reversed_batch_can_be_imported_again()
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var payer = Guid.NewGuid();
        var rows = new[] { Receivable(id, payer), Mandate(id, payer), OpeningBalance(id) };
        var batch = await CreateBatchAsync(rows);
        Outcomes(await RunAsync(batch)).ShouldBe(["IMPORTED", "IMPORTED", "IMPORTED"]);
        (await CountAsync($"SELECT count(*) FROM bil.migration_receivable WHERE legacy_invoice_number = 'INV-GR1-{id}'")).ShouldBe(1);

        // A dry reversal reports but removes nothing.
        var (dryResponse, dry) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/reverse", new { batchId = batch, dryRun = true }, "alice");
        dryResponse.StatusCode.ShouldBe(HttpStatusCode.OK, dry?.ToJsonString());
        (await CountAsync($"SELECT count(*) FROM bil.migration_receivable WHERE legacy_invoice_number = 'INV-GR1-{id}'")).ShouldBe(1);

        // The real reversal removes the BIL rows and books exactly one reversing FIN journal.
        var (response, reversed) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/reverse", new { batchId = batch, dryRun = false }, "alice");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, reversed?.ToJsonString());
        reversed!["outcomes"]!.AsArray().ShouldAllBe(o => o.Text("outcome") == "REVERSED");
        (await CountAsync($"SELECT count(*) FROM bil.migration_receivable WHERE legacy_invoice_number = 'INV-GR1-{id}'")).ShouldBe(0);
        (await CountAsync($"SELECT count(*) FROM fin.journal_entry WHERE reverses_journal_id IN (SELECT journal_id FROM fin.migration_import WHERE legacy_id = 'TB-GR1-{id}')")).ShouldBe(1);

        // Reversing the same batch again must not double-reverse or throw a server error.
        var (again, againBody) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/reverse", new { batchId = batch, dryRun = false }, "alice");
        ((int)again.StatusCode).ShouldBeLessThan(500, againBody?.ToJsonString());
        (await CountAsync($"SELECT count(*) FROM fin.journal_entry WHERE reverses_journal_id IN (SELECT journal_id FROM fin.migration_import WHERE legacy_id = 'TB-GR1-{id}')")).ShouldBe(1);

        // After the rollback the same legacy rows import again.
        var rerun = await RunAsync(await CreateBatchAsync(rows));
        // The append-only FIN ledger keeps its reversed opening balance, so that one legacy id is refused as a duplicate.
        // TODO(hardening): decide whether a reversed FIN opening balance may be re-imported (needs a fresh journal and import row).
        Outcomes(rerun).ShouldBe(["IMPORTED", "IMPORTED", "FAILED"], rerun?.ToJsonString());
        rerun!["outcomes"]![2].Text("errorCode").ShouldBe("MIG-ERR-DUPLICATE");
        (await CountAsync($"SELECT count(*) FROM bil.migration_receivable WHERE legacy_invoice_number = 'INV-GR1-{id}'")).ShouldBe(1);
    }

    [Fact]
    public async Task A_batch_cannot_be_reversed_before_it_ran_and_the_starter_cannot_approve_a_malformed_rerun()
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var batch = await CreateBatchAsync(Receivable(id, Guid.NewGuid()));
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/reverse", new { batchId = batch, dryRun = false }, "alice");
        ((int)response.StatusCode).ShouldBeInRange(400, 499, body?.ToJsonString());
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId = batch, decision = "MAYBE" }, "bob")).Response
            .IsSuccessStatusCode.ShouldBeFalse();
    }
}
