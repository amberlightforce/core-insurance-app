using System.Net;
using CoreIns.Modules.Billing.Services;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Bil.BillingSlice;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>
/// Own database: BO-BIL-12, the BIL side of the BIL-vs-FIN reconciliation. The daily control totals (net debit and line count of the billed receivable LA-02 per
/// currency) are pushed to <c>fin.Reconciliation.exchange</c> and must equal FIN movement of GL-1210 after the day is posted; a FIN-only manual
/// difference shows as a break. The ledger reads (<c>bil.Ledger.query/balance</c>) and the control-total query are covered too.
/// </summary>
public sealed class FinReconciliationDifferenceTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Finance = "Staff.Finance";
    private BillingSlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _slice = new BillingSlice(database);
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private Task<int> PushAsync() => _slice.Policy.Factory.Services.GetRequiredService<FinControlPublisher>().RunAsync(2, null, Ct);

    private Task<long> OpenBreaksAsync() => _slice.ScalarAsync<long>("SELECT count(*) FROM fin.recon_break WHERE definition_code = 'BIL_RECEIVABLE' AND state = 'OPEN'");

    private async Task<(string Account, decimal Total)> BilledAndPostedAsync()
    {
        var policy = await _slice.BindAsync();
        await _slice.DrainAsync();
        var invoice = await _slice.InvoiceOfAsync(policy.PolicyId);
        return (invoice.Text("invoice.billingAccountId"), policy.Total);
    }

    [Fact]
    public async Task BO_BIL_12_a_FIN_only_manual_difference_is_reported_as_a_break()
    {
        await BilledAndPostedAsync();
        await PushAsync();
        (await OpenBreaksAsync()).ShouldBe(0);

        // A manual journal booked in FIN only: one more cent on the receivable control account.
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await database.ExecuteAsSuperuserAsync(
            $"""
            BEGIN;
            WITH src AS (SELECT * FROM fin.journal_entry WHERE source_module = 'BIL' ORDER BY posted_at LIMIT 1),
            j AS (
              INSERT INTO fin.journal_entry (journal_id, journal_number, legal_entity_id, legal_entity_code, jurisdiction, book, accounting_date, business_date,
                  period_id, source_type, source_module, source_event_type, source_event_ids, source_ref, rule_set_id, rule_set_version, rule_codes,
                  functional_currency, correlation_id, posted_at, posted_by)
              SELECT gen_random_uuid(), 'JNL2026' || floor(random() * 9000000000 + 1000000000)::bigint, legal_entity_id, legal_entity_code, jurisdiction, book, accounting_date, business_date,
                  period_id, source_type, 'BIL', source_event_type, ARRAY[]::uuid[], 'bil12-manual', rule_set_id, rule_set_version, ARRAY['RAW'],
                  functional_currency, 'x', now(), 'test' FROM src RETURNING *)
            INSERT INTO fin.journal_line (line_id, journal_id, line_no, legal_entity_id, book, account_code, side, amount, currency, amount_functional, functional_currency, rule_code, business_date)
            SELECT gen_random_uuid(), j.journal_id, n.no, j.legal_entity_id, j.book, n.account, n.side, 0.01, 'EUR', 0.01, 'EUR', 'RAW', DATE '{today}'
            FROM j, (VALUES (1, 'GL-1210', 'DEBIT'), (2, 'GL-1215', 'CREDIT')) AS n(no, account, side);
            COMMIT;
            """, Ct);

        await PushAsync();
        (await OpenBreaksAsync()).ShouldBeGreaterThan(0);
        var kinds = await _slice.ScalarAsync<string>("SELECT string_agg(kind || ':' || (ledger_amount - source_amount)::text, ',' ORDER BY kind) FROM fin.recon_break WHERE definition_code = 'BIL_RECEIVABLE' AND state = 'OPEN'");
        kinds.ShouldContain("AMOUNT:0.01");
    }
}
