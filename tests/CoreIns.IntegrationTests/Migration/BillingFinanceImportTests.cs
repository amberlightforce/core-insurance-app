using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Migration;

/// <summary>
/// SL6-MIG-BIL-IMPORT: opening receivables, a SEPA mandate and the FIN opening-balance journal through a migration batch. The opening
/// receivable equals the legacy outstanding to the cent, the FIN journal reconciles to the legacy trial balance (0.00 difference),
/// a double import is a no-op (xref), a mismatch is refused, and the reversal is refused after collection (D-SL6-05). Amounts are illustrative.
/// </summary>
public sealed class BillingFinanceImportTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
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
    public async Task Opening_receivable_mandate_and_journal_import_reconcile_skip_on_repeat_and_reverse_only_untouched()
    {
        var id = Guid.NewGuid().ToString("N");
        var payer = Guid.NewGuid();
        var invoice1 = $"INV-{id}-1";
        var invoice2 = $"INV-{id}-2";
        var mandate = $"UMR{id}"[..30];
        var opening = $"TB-{id}";
        var batch = await CreateBatchAsync(
            Receivable(invoice1, payer, 250.35m, 200.00m, 50.35m),
            Receivable(invoice2, payer, 100.00m, 100.00m),
            Mandate(mandate, payer),
            OpeningBalance(opening, 1000.00m, 1000.00m));

        // Dry run: validated, nothing written.
        Outcomes(await StartAsync(batch, dryRun: true)).ShouldBe(["IMPORTED", "IMPORTED", "IMPORTED", "IMPORTED"]);
        (await CountAsync($"SELECT count(*) FROM bil.migration_receivable WHERE legacy_invoice_number IN ('{invoice1}', '{invoice2}')")).ShouldBe(0);
        (await CountAsync($"SELECT count(*) FROM fin.migration_import WHERE legacy_id = '{opening}'")).ShouldBe(0);

        // Real run: another user approves.
        (await StartAsync(batch, dryRun: false)).Text("batch.status").ShouldBe("PENDING_APPROVAL");
        (await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId = batch, decision = "APPROVE" }, "alice")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (approved, done) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId = batch, decision = "APPROVE" }, "bob");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, done?.ToJsonString());
        Outcomes(done).ShouldBe(["IMPORTED", "IMPORTED", "IMPORTED", "IMPORTED"]);

        // BIL: the opening receivable is the legacy outstanding to the cent, origin MIGRATION; the mandate keeps reference and signature date.
        (await ScalarAsync<decimal>($"SELECT outstanding_amount FROM bil.migration_receivable WHERE legacy_invoice_number = '{invoice1}'")).ShouldBe(250.35m);
        (await ScalarAsync<string>($"SELECT origin FROM bil.migration_receivable WHERE legacy_invoice_number = '{invoice1}'")).ShouldBe("MIGRATION");
        (await ScalarAsync<long>($"SELECT count(DISTINCT billing_account_id) FROM bil.migration_receivable WHERE payer_party_id = '{payer}'")).ShouldBe(1);
        (await ScalarAsync<string>($"SELECT mandate_reference || '/' || signature_date::text || '/' || origin FROM bil.migration_mandate WHERE mandate_reference = '{mandate}'"))
            .ShouldBe($"{mandate}/2024-03-15/MIGRATION");

        // FIN: one balanced journal reconciled to the trial-balance totals, nothing on income or expense accounts.
        var journalId = done!["outcomes"]![3].Text("coreId");
        (await ScalarAsync<decimal>($"SELECT sum(amount) FROM fin.journal_line WHERE journal_id = '{journalId}' AND side = 'DEBIT'")).ShouldBe(1000.00m);
        (await ScalarAsync<decimal>($"SELECT sum(amount) FROM fin.journal_line WHERE journal_id = '{journalId}' AND side = 'CREDIT'")).ShouldBe(1000.00m);
        (await ScalarAsync<string>($"SELECT source_module || '/' || source_event_type FROM fin.journal_entry WHERE journal_id = '{journalId}'")).ShouldBe("MIG/OpeningBalanceImported");
        (await ScalarAsync<decimal>($"SELECT trial_balance_debit - trial_balance_credit FROM fin.migration_import WHERE journal_id = '{journalId}'")).ShouldBe(0.00m);

        // Double import is a no-op: the xref skips every object.
        var again = await CreateBatchAsync(
            Receivable(invoice1, payer, 250.35m, 200.00m, 50.35m), Mandate(mandate, payer), OpeningBalance(opening, 1000.00m, 1000.00m));
        await StartAsync(again, dryRun: true);
        await StartAsync(again, dryRun: false);
        var (_, second) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId = again, decision = "APPROVE" }, "bob");
        Outcomes(second).ShouldBe(["SKIPPED_EXISTING", "SKIPPED_EXISTING", "SKIPPED_EXISTING"]);
        (await CountAsync($"SELECT count(*) FROM bil.migration_receivable WHERE legacy_invoice_number = '{invoice1}'")).ShouldBe(1);
        (await CountAsync($"SELECT count(*) FROM fin.migration_import WHERE legacy_id = '{opening}'")).ShouldBe(1);

        // Cash collected against the second receivable: reversal is blocked for it, the others are reversed.
        await ExecuteAsync($"UPDATE bil.migration_receivable SET collected_amount = 100.00 WHERE legacy_invoice_number = '{invoice2}'");
        var (response, reversed) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/reverse", new { batchId = batch, dryRun = false }, "alice");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, reversed?.ToJsonString());
        reversed.Text("batch.status").ShouldBe("REVERSAL_BLOCKED");
        var results = reversed!["outcomes"]!.AsArray().Select(o => o.Text("outcome")).Order(StringComparer.Ordinal).ToArray();
        results.ShouldBe(["REVERSAL_BLOCKED", "REVERSED", "REVERSED", "REVERSED"]);
        (await CountAsync($"SELECT count(*) FROM bil.migration_receivable WHERE legacy_invoice_number = '{invoice1}'")).ShouldBe(0);
        (await CountAsync($"SELECT count(*) FROM bil.migration_receivable WHERE legacy_invoice_number = '{invoice2}'")).ShouldBe(1);
        (await CountAsync($"SELECT count(*) FROM bil.migration_mandate WHERE mandate_reference = '{mandate}'")).ShouldBe(0);
        (await CountAsync($"SELECT count(*) FROM fin.journal_entry WHERE reverses_journal_id = '{journalId}'")).ShouldBe(1);
    }

    [Fact]
    public async Task A_sum_mismatch_a_trial_balance_difference_and_an_income_account_are_refused_with_codes()
    {
        var id = Guid.NewGuid().ToString("N");
        var payer = Guid.NewGuid();
        var batch = await CreateBatchAsync(
            Receivable($"INV-{id}-X", payer, 250.35m, 200.00m, 50.30m),
            OpeningBalance($"TB-{id}-X", 1000.00m, 1000.00m, debitTotal: 999.99m),
            OpeningBalance($"TB-{id}-Y", 1000.00m, 1000.00m, creditAccount: "GL-4210"));
        var dry = await StartAsync(batch, dryRun: true);
        Outcomes(dry).ShouldBe(["FAILED", "FAILED", "FAILED"]);
        dry!["outcomes"]![0].Text("errorCode").ShouldBe("MIG-ERR-SUM-MISMATCH");
        dry["outcomes"]![1].Text("errorCode").ShouldBe("MIG-ERR-RECONCILIATION");
        dry["outcomes"]![2].Text("errorCode").ShouldBe("MIG-ERR-PNL-ACCOUNT");
    }

    private static object Receivable(string invoice, Guid payer, decimal outstanding, params decimal[] items) => new
    {
        module = "BIL", objectType = "Receivable", legacyId = invoice,
        payload = new
        {
            legacyInvoiceNumber = invoice, payerPartyId = payer, policyLegacyNumber = "LEG-POL-1", dueDate = "2026-09-01", originalAmount = outstanding + 10m,
            outstanding, openItems = items.Select(a => new { amount = a, description = "premium instalment" }).ToArray(),
        },
    };

    private static object Mandate(string reference, Guid payer) => new
    {
        module = "BIL", objectType = "Mandate", legacyId = reference,
        payload = new { mandateReference = reference, payerPartyId = payer, signatureDate = "2024-03-15", ibanMasked = "GR16 **** **** **** **** *** 7890" },
    };

    private static object OpeningBalance(string legacy, decimal debit, decimal credit, decimal? debitTotal = null, string creditAccount = "GL-2110") => new
    {
        module = "FIN", objectType = "OpeningBalance", legacyId = legacy,
        payload = new
        {
            legacyId = legacy, accountingDate = "2026-09-30", currency = "EUR",
            lines = new object[]
            {
                new { accountCode = "GL-1210", debit = debit - 399.50m, credit = 0m },
                new { accountCode = "GL-1215", debit = 399.50m, credit = 0m },
                new { accountCode = creditAccount, debit = 0m, credit = credit },
            },
            trialBalance = new { debit = debitTotal ?? debit, credit },
        },
    };

    private async Task<string> CreateBatchAsync(params object[] items)
    {
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches", new { name = "bil-fin", items }, "alice");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body.Text("batch.batchId");
    }

    private async Task<JsonNode?> StartAsync(string batchId, bool dryRun)
    {
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId, dryRun }, "alice");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body;
    }

    private static string[] Outcomes(JsonNode? body) => [.. body!["outcomes"]!.AsArray().Select(o => o.Text("outcome"))];

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body, string user)
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

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = _db.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync(Ct);
        return (T)Convert.ChangeType(value!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private Task<long> CountAsync(string sql) => ScalarAsync<long>(sql);

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _db.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
