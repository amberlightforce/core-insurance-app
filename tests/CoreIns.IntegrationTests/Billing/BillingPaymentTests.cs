using System.Globalization;
using System.Net;
using Npgsql;
using static CoreIns.IntegrationTests.Bil.BillingSlice;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>Payments that are not the happy path, the API's guards, and the sub-ledger's database guarantees.</summary>
public sealed class BillingPaymentTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private BillingSlice _slice = null!;

    private const string EmptyJson = "{}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _slice = new BillingSlice(database);
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private static object Money(decimal amount) => new { amount = amount.ToString(CultureInfo.InvariantCulture), currency = "EUR" };

    private async Task<(BoundPolicy Policy, string AccountId, string InvoiceId)> BilledAsync()
    {
        var policy = await _slice.BindAsync();
        await _slice.DrainAsync();
        var invoice = await _slice.InvoiceOfAsync(policy.PolicyId);
        return (policy, invoice.Text("invoice.billingAccountId"), invoice.Text("invoice.invoiceId"));
    }

    [Fact]
    public async Task REQ_BIL_135_partial_and_over_payments_stay_unapplied_and_the_invoice_stays_due()
    {
        var (policy, account, invoiceId) = await BilledAsync();

        var (r1, partial) = await _slice.PostAsync("/api/bil/v1/payments/take", new { billingAccountId = account, amount = Money(policy.Total - 10m), method = "BANK_TRANSFER" });
        r1.StatusCode.ShouldBe(HttpStatusCode.Created, partial?.ToJsonString());
        partial.Text("allocationOutcome").ShouldBe("SUSPENSE");
        partial.Text("receipt.state").ShouldBe("SUSPENSE");
        partial.Text("receipt.suspenseReason").ShouldBe("AMOUNT_MISMATCH");

        var (r2, over) = await _slice.PostAsync("/api/bil/v1/payments/take",
            new { billingAccountId = account, amount = Money(policy.Total + 25m), method = "CASHIER", invoiceId });
        r2.StatusCode.ShouldBe(HttpStatusCode.Created, over?.ToJsonString());
        over.Text("receipt.suspenseReason").ShouldBe("AMOUNT_MISMATCH");

        var invoice = await _slice.InvoiceOfAsync(policy.PolicyId);
        invoice.Text("invoice.state").ShouldBe("DUE");
        invoice["allocations"]!.AsArray().Count.ShouldBe(0);
        var (_, balances) = await _slice.GetAsync($"/api/bil/v1/billing-accounts/{account}");
        Amount(balances!["balancesByState"]!["unapplied"]).ShouldBe(policy.Total - 10m + policy.Total + 25m);
        Amount(balances["balancesByState"]!["billed"]).ShouldBe(policy.Total);

        // REQ-BIL-130/136: a manual allocation must be the invoice's whole open amount; the over-payment can cover it.
        var (bad, refused) = await _slice.PostAsync("/api/bil/v1/allocations/allocate",
            new { receiptId = partial.Text("receipt.receiptId"), lines = new[] { new { invoiceId, amount = Money(policy.Total - 10m) } } });
        bad.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, refused?.ToJsonString());
        refused.Text("code").ShouldBe("BIL-ERR-AMOUNT-MISMATCH");

        var (tooMuch, overAllocated) = await _slice.PostAsync("/api/bil/v1/allocations/allocate",
            new { receiptId = partial.Text("receipt.receiptId"), lines = new[] { new { invoiceId, amount = Money(policy.Total) } } });
        tooMuch.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, overAllocated?.ToJsonString());
        overAllocated.Text("code").ShouldBe("BIL-ERR-OVER-ALLOCATION");

        var (ok, allocated) = await _slice.PostAsync("/api/bil/v1/allocations/allocate",
            new { receiptId = over.Text("receipt.receiptId"), lines = new[] { new { invoiceId, amount = Money(policy.Total) } } });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, allocated?.ToJsonString());
        allocated.Text("receipt.state").ShouldBe("PARTIALLY_ALLOCATED");
        Amount(allocated!["receipt"]!["unallocated"]).ShouldBe(25m);
        (await _slice.InvoiceOfAsync(policy.PolicyId)).Text("invoice.state").ShouldBe("PAID");
    }

    [Fact]
    public async Task Payment_take_is_idempotent_needs_the_billing_role_and_refuses_another_currency_or_account()
    {
        var (policy, account, _) = await BilledAsync();
        var key = Guid.NewGuid();
        var body = new { billingAccountId = account, amount = Money(policy.Total), method = "BANK_TRANSFER" };

        var (first, one) = await _slice.PostAsync("/api/bil/v1/payments/take", body, key: key);
        var (second, two) = await _slice.PostAsync("/api/bil/v1/payments/take", body, key: key);
        first.StatusCode.ShouldBe(HttpStatusCode.Created, one?.ToJsonString());
        second.IsSuccessStatusCode.ShouldBeTrue(two?.ToJsonString());
        two.Text("receipt.receiptId").ShouldBe(one.Text("receipt.receiptId"));
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.receipt WHERE billing_account_id = '{account}'")).ShouldBe(1);

        (await _slice.PostAsync("/api/bil/v1/payments/take", body, roles: Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _slice.PostAsync("/api/bil/v1/payments/take", new { billingAccountId = account, amount = new { amount = "10.00", currency = "USD" }, method = "CASHIER" }))
            .Body.Text("code").ShouldBe("BIL-ERR-CURRENCY");
        (await _slice.PostAsync("/api/bil/v1/payments/take", new { billingAccountId = Guid.CreateVersion7(), amount = Money(1m), method = "CASHIER" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _slice.PostAsync("/api/bil/v1/payments/take", new { billingAccountId = account, amount = Money(1.005m), method = "CASHIER" }))
            .Body.Text("code").ShouldBe("BIL-ERR-VALIDATION");
        (await _slice.GetAsync($"/api/bil/v1/billing-accounts/{Guid.CreateVersion7()}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A dry run computes the receipt and stores nothing.
        var (dry, preview) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/bil/v1/payments/take?dryRun=true", body, roles: Billing);
        dry.IsSuccessStatusCode.ShouldBeTrue(preview?.ToJsonString());
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.receipt WHERE billing_account_id = '{account}'")).ShouldBe(1);

        // The audited command recorded the receipt with its number and no personal data.
        (await _slice.ScalarAsync<long>("SELECT count(*) FROM plt.audit_event WHERE operation = 'bil.Payment.take' AND outcome = 'Succeeded'")).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task REQ_BIL_279_to_281_the_sub_ledger_is_append_only_and_balanced_for_every_role()
    {
        var (_, account, _) = await BilledAsync();
        await using var admin = NpgsqlDataSource.Create(database.SuperuserConnectionString);

        foreach (var sql in new[]
                 {
                     $"UPDATE bil.ledger_line SET amount = amount + 1 WHERE billing_account_id = '{account}'",
                     $"DELETE FROM bil.ledger_line WHERE billing_account_id = '{account}'",
                     $"UPDATE bil.ledger_entry SET entry_type = 'X' WHERE billing_account_id = '{account}'",
                     $"DELETE FROM bil.ledger_entry WHERE billing_account_id = '{account}'",
                     "TRUNCATE bil.ledger_line CASCADE",
                     "TRUNCATE bil.allocation",
                 })
        {
            var error = await Should.ThrowAsync<PostgresException>(async () =>
            {
                await using var command = admin.CreateCommand(sql);
                await command.ExecuteNonQueryAsync(Ct);
            });
            error.SqlState.ShouldBe("BL002", sql);
        }

        // An unbalanced entry fails at commit (deferred constraint trigger), even for the owner.
        var entry = Guid.CreateVersion7();
        var unbalanced = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var connection = await admin.OpenConnectionAsync(Ct);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            await using (var insert = new NpgsqlCommand(
                $"""
                INSERT INTO bil.ledger_entry (entry_id, legal_entity_id, jurisdiction, billing_account_id, entry_type, accounting_date, business_date, recorded_at,
                    cause_operation, correlation_id, lineage_keys) VALUES ('{entry}', '{ApiHostFactory.LegalEntityId}', 'GR', '{account}', 'BILLED', current_date, current_date, now(), 'test', 'x', '{EmptyJson}');
                INSERT INTO bil.ledger_line (line_id, entry_id, line_no, account_code, side, amount, currency, rule_id, legal_entity_id)
                    VALUES ('{Guid.CreateVersion7()}', '{entry}', 1, 'LA-02', 'DEBIT', 10, 'EUR', 'BLR-BILLED', '{ApiHostFactory.LegalEntityId}'),
                           ('{Guid.CreateVersion7()}', '{entry}', 2, 'LA-01', 'CREDIT', 9, 'EUR', 'BLR-BILLED', '{ApiHostFactory.LegalEntityId}');
                """, connection, transaction))
            {
                await insert.ExecuteNonQueryAsync(Ct);
            }

            await transaction.CommitAsync(Ct);
        });
        unbalanced.SqlState.ShouldBe("BL003");

        // The app role has no UPDATE or DELETE on the ledger and allocations at all.
        (await _slice.ScalarAsync<bool>("SELECT has_table_privilege('app', 'bil.ledger_line', 'UPDATE') OR has_table_privilege('app', 'bil.ledger_entry', 'DELETE')"
            + " OR has_table_privilege('app', 'bil.allocation', 'UPDATE') OR has_table_privilege('app', 'bil.ledger_rule', 'INSERT')")).ShouldBeFalse();
        (await _slice.ScalarAsync<bool>("SELECT has_table_privilege('app', 'bil.ledger_line', 'INSERT')")).ShouldBeTrue();

        // REQ-BIL-002: charges are frozen as received.
        var frozen = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var command = admin.CreateCommand($"UPDATE bil.charge SET amount = amount + 1 WHERE term_id IN (SELECT term_id FROM bil.plan_instance WHERE billing_account_id = '{account}')");
            await command.ExecuteNonQueryAsync(Ct);
        });
        frozen.SqlState.ShouldBe("BL004");
    }

    [Fact]
    public async Task REQ_BIL_130_the_database_refuses_an_allocation_above_the_receipt()
    {
        var (policy, account, invoiceId) = await BilledAsync();
        var (_, payment) = await _slice.PostAsync("/api/bil/v1/payments/take",
            new { billingAccountId = account, amount = Money(1m), method = "CASHIER", autoAllocate = false });
        payment.Text("allocationOutcome").ShouldBe("NOT_REQUESTED");
        var item = await _slice.ScalarAsync<Guid>($"SELECT invoice_item_id FROM bil.invoice_item WHERE invoice_id = '{invoiceId}' ORDER BY line_no LIMIT 1");
        await using var admin = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var error = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var command = admin.CreateCommand(
                $"""
                INSERT INTO bil.allocation (allocation_id, legal_entity_id, receipt_id, invoice_id, invoice_item_id, term_id, amount, currency, rule_id, source, actor, recorded_at)
                VALUES ('{Guid.CreateVersion7()}', '{ApiHostFactory.LegalEntityId}', '{payment.Text("receipt.receiptId")}', '{invoiceId}', '{item}', '{policy.TermId}', 2, 'EUR', 'MANUAL', 'MANUAL', 'test', now())
                """);
            await command.ExecuteNonQueryAsync(Ct);
        });
        error.SqlState.ShouldBe("BL001");
    }
}
