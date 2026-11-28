using System.Net;
using CoreIns.Modules.Billing.Services;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Bil.BillingSlice;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>
/// BO-BIL-12: the BIL side of the BIL-vs-FIN reconciliation. The daily control totals (net debit and line count of the billed receivable LA-02 per
/// currency) are pushed to <c>fin.Reconciliation.exchange</c> and must equal FIN movement of GL-1210 after the day is posted; a FIN-only manual
/// difference shows as a break. The ledger reads (<c>bil.Ledger.query/balance</c>) and the control-total query are covered too.
/// </summary>
public sealed class FinReconciliationTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
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
    public async Task BO_BIL_12_totals_equal_after_a_posted_day_and_the_ledger_reads_agree()
    {
        var (account, total) = await BilledAndPostedAsync();
        (await PushAsync()).ShouldBeGreaterThan(0);
        (await OpenBreaksAsync()).ShouldBe(0, await _slice.ScalarAsync<string>("SELECT string_agg(currency || ' ' || kind || ' src=' || source_amount::text || '/' || coalesce(source_count::text,'-') || ' led=' || ledger_amount::text || '/' || ledger_count::text, '; ') FROM fin.recon_break WHERE state = 'OPEN'"));
        (await _slice.ScalarAsync<long>("SELECT count(*) FROM fin.recon_run WHERE definition_code = 'BIL_RECEIVABLE' AND state = 'CLEAN'")).ShouldBeGreaterThan(0);

        // The same figure as BIL reports for the day through the query.
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var (response, body) = await _slice.PostAsync("/api/bil/v1/reconciliation/fin", new { accountingDate = today, includeEntryIds = true }, Finance);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var control = body!["receivableControl"]!.AsArray().Single()!;
        control.Text("currency").ShouldBe("EUR");
        BillingSlice.Amount(control["amount"]).ShouldBe(total);
        var row = body["totals"]!.AsArray().First(t => t!.Text("account") == "LA-02" && t.Text("eventType") == "BILLED")!;
        row["entryIds"]!.AsArray().Count.ShouldBeGreaterThan(0);

        // A future day is not closed.
        var tomorrow = DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var (future, _) = await _slice.PostAsync("/api/bil/v1/reconciliation/fin", new { accountingDate = tomorrow }, Finance);
        future.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // Ledger balance and query of the account.
        var (balance, balanceBody) = await _slice.GetAsync($"/api/bil/v1/ledger/balance?billingAccountId={account}&account=LA-02", Finance);
        balance.StatusCode.ShouldBe(HttpStatusCode.OK, balanceBody?.ToJsonString());
        var line = balanceBody!["balances"]!.AsArray().Single()!;
        BillingSlice.Amount(line["net"]).ShouldBe(total);
        (await _slice.GetAsync($"/api/bil/v1/ledger/query?billingAccountId={account}&entryType=BILLED&limit=1", Finance)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (page, pageBody) = await _slice.GetAsync($"/api/bil/v1/ledger/query?billingAccountId={account}&limit=1", Finance);
        page.StatusCode.ShouldBe(HttpStatusCode.OK, pageBody?.ToJsonString());
        pageBody!["items"]!.AsArray().Single()!["lines"]!.AsArray().Count.ShouldBeGreaterThan(1);
        pageBody["nextCursor"].ShouldNotBeNull();
        (await _slice.GetAsync("/api/bil/v1/ledger/query?from=nonsense", Finance)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
