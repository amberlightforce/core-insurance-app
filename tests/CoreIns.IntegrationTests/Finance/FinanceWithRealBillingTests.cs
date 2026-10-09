using System.Globalization;
using System.Net;
using CoreIns.IntegrationTests.Bil;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Finance;

/// <summary>
/// The real POL bind → real BIL sub-ledger → FIN journals, with the SL-BIL branch
/// merged locally. Every real BillingEntryPosted line must match exactly one FIN rule and nothing is suspended.
/// </summary>
public sealed class FinanceWithRealBillingTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private BillingSlice _slice = null!;

    public async ValueTask InitializeAsync()
    {
        _slice = new BillingSlice(database);
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    [Fact]
    public async Task E2E01_bind_bill_pay_posts_every_real_BIL_entry_into_balanced_journals()
    {
        var policy = await _slice.BindAsync();
        await _slice.DrainAsync();
        var invoice = await _slice.InvoiceOfAsync(policy.PolicyId);
        var account = invoice.Text("invoice.billingAccountId");

        // D-SLC-15: MKT is the configuration authority, so the envelope's hash is the hash in the payload.
        var bound = (await _slice.EnvelopesAsync(policy.PolicyId, "PolicyBound")).ShouldHaveSingleItem();
        bound.ConfigurationHash.ToString().ShouldBe(bound.Payload["configurationHash"]!.GetValue<string>());
        bound.Payload["currency"]!.GetValue<string>().ShouldBe("EUR");

        // D-SLC-19a: the invoice shows the tax line as provisional (illustrative rate, not Settled); premium carries no status.
        var items = invoice["invoiceItems"]!.AsArray().Select(i => i!.AsObject()).ToList();
        items.Where(i => i["chargeCategory"]!.GetValue<string>() == "TAX").ShouldNotBeEmpty();
        foreach (var item in items.Where(i => i["chargeCategory"]!.GetValue<string>() == "TAX"))
        {
            item["provisional"]!.GetValue<bool>().ShouldBeTrue(item.ToJsonString());
            item["legalStatus"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        }

        items.Where(i => i["chargeCategory"]!.GetValue<string>() != "TAX").ShouldAllBe(i => i["legalStatus"] == null);
        var (paid, payment) = await _slice.PostAsync("/api/bil/v1/payments/take", new
        {
            billingAccountId = account,
            amount = new { amount = policy.Total.ToString(CultureInfo.InvariantCulture), currency = "EUR" },
            method = "BANK_TRANSFER",
            invoiceId = invoice.Text("invoice.invoiceId"),
        });
        paid.StatusCode.ShouldBe(HttpStatusCode.Created, payment?.ToJsonString());
        await _slice.DrainAsync();

        var summary = await _slice.ScalarAsync<string>($"""
            SELECT string_agg(coalesce(payload->>'eventType', '?') || ':' || status || coalesce(':' || exception_reason || ':' || exception_detail, ''), ' | ' ORDER BY received_at)
              FROM fin.business_event WHERE event_type = 'BillingEntryPosted' AND billing_account_id = '{account}'
            """);
        TestContext.Current.SendDiagnosticMessage("FIN intake: " + summary);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM fin.business_event WHERE event_type = 'BillingEntryPosted' AND billing_account_id = '{account}' AND status <> 'POSTED'"))
            .ShouldBe(0, summary);
        (await _slice.ScalarAsync<string>($"SELECT string_agg(DISTINCT payload->>'eventType', ',' ORDER BY payload->>'eventType') FROM fin.business_event WHERE event_type = 'BillingEntryPosted' AND billing_account_id = '{account}'"))
            .ShouldBe("ALLOCATED,BILLED,IPT_DUE,RECEIVED,WRITTEN");

        string Net(string gl) => $"""
            SELECT coalesce(sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END), 0) FROM fin.journal_line
             WHERE billing_account_id = '{account}' AND account_code = '{gl}'
            """;
        var premium = -await _slice.ScalarAsync<decimal>(Net("GL-2110"));
        var ipt = -await _slice.ScalarAsync<decimal>(Net("GL-2410"));
        TestContext.Current.SendDiagnosticMessage($"premium {premium}, IPT {ipt}, total {policy.Total}");
        (premium + ipt).ShouldBe(policy.Total);
        (await _slice.ScalarAsync<decimal>(Net("GL-1110"))).ShouldBe(policy.Total);
        foreach (var gl in new[] { "GL-1215", "GL-1210", "GL-2411", "GL-2540" })
        {
            (await _slice.ScalarAsync<decimal>(Net(gl))).ShouldBe(0m, gl);
        }

        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM fin.journal_line WHERE billing_account_id = '{account}' AND account_code = 'GL-2110' AND (policy_number IS NULL OR gl_key IS NULL OR coverage_code IS NULL)"))
            .ShouldBe(0);

        // The premium path posts under the rule set in force: v3 (SL3-FIN-RULES, effective 2026-10-01; its premium rules are v2's unchanged), and the
        // ALLOCATED entry is its own journal that clears GL-1210 against GL-2540 (E2E-01 read the ledger before it, 2026-10-08).
        (await _slice.ScalarAsync<string>($"""
            SELECT string_agg(DISTINCT je.rule_set_version::text, ',') FROM fin.journal_entry je
             WHERE EXISTS (SELECT 1 FROM fin.journal_line jl WHERE jl.journal_id = je.journal_id AND jl.billing_account_id = '{account}')
            """)).ShouldBe("3");
        (await _slice.ScalarAsync<string>($"""
            SELECT string_agg(jl.account_code || ' ' || jl.side, ',' ORDER BY jl.account_code, jl.side) FROM (
              SELECT DISTINCT jl.account_code, jl.side FROM fin.journal_line jl JOIN fin.journal_entry je USING (journal_id)
               WHERE jl.billing_account_id = '{account}' AND 'AL-RECEIVABLE' = ANY (je.rule_codes)) jl
            """)).ShouldBe("GL-1210 CREDIT,GL-2540 DEBIT");
    }
}
