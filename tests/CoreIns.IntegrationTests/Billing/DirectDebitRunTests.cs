using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CoreIns.Modules.Billing;
using CoreIns.Modules.Billing.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static CoreIns.IntegrationTests.Bil.BillingSlice;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>
/// BO-BIL-06b: the SEPA direct-debit collection run. Due instalments of DIRECT_DEBIT terms with an active mandate are written into one
/// pain.008 file (through the bank-file port, IBAN only in the file), collected once, settled into a DIRECT_DEBIT receipt allocated to the
/// invoice, and the mandate turns from FRST to RCUR. An R-transaction reverses the receipt (cash out of the bank, invoice reopened) and the
/// ledger invariants keep passing; a reject before settlement frees the invoice; without a bank-file store nothing is collected.
/// </summary>
public sealed class DirectDebitRunTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Run = "/dev/bank/direct-debits/run";
    private const string Creditor = "GR8201101250000000012300695";
    private BillingSlice _slice = null!;

    public async ValueTask InitializeAsync() => _slice = await OpenAsync();

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private async Task<BillingSlice> OpenAsync(bool manualBank = false, bool fileStore = true)
    {
        var slice = new BillingSlice(database, services =>
        {
            services.PostConfigure<BillingOptions>(o =>
            {
                o.DirectDebitCreditorName = "Test Insurance SA";
                o.DirectDebitCreditorId = "GR12ZZZ099999999999";
                o.DirectDebitCreditorIban = Creditor;
                o.DirectDebitLeadDays = 30;
            });
            if (manualBank)
            {
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Billing:StubBank:Mode"] = "Manual" }).Build();
                services.RemoveAll<IBankChannel>();
                services.AddSingleton<IBankChannel>(new StubBankChannel(configuration));
            }

            if (!fileStore)
            {
                services.RemoveAll<IBankFileStore>();
            }
        });
        await slice.SeedAsync();
        return slice;
    }

    private static string NewIban()
    {
        var bban = string.Concat(Guid.NewGuid().ToByteArray().Select(b => (b % 10).ToString(CultureInfo.InvariantCulture))).PadRight(23, '7')[..23];
        var remainder = 0;
        foreach (var c in bban + "GR00")
        {
            var value = char.IsAsciiDigit(c) ? c - '0' : c - 'A' + 10;
            remainder = value >= 10 ? ((remainder * 100) + value) % 97 : ((remainder * 10) + value) % 97;
        }

        return "GR" + (98 - remainder).ToString("00", CultureInfo.InvariantCulture) + bban;
    }

    /// <summary>A bound policy on DIRECT_DEBIT with an active signed mandate; returns the invoice and its collected-IBAN.</summary>
    private static async Task<(BoundPolicy Policy, string Account, string Invoice, string Mandate, string Iban)> DirectDebitPolicyAsync(BillingSlice slice)
    {
        var policy = await slice.BindAsync();
        await slice.DrainAsync();
        var account = await slice.ScalarAsync<string>($"SELECT billing_account_id::text FROM bil.billing_account WHERE payer_party_id = '{policy.PartyId}'");
        var iban = NewIban();
        var (added, instrument) = await slice.PostAsync("/api/bil/v1/payment-instruments/add", new { partyId = policy.PartyId, iban, holder = "Eleni Papadopoulou" });
        added.StatusCode.ShouldBe(HttpStatusCode.OK, instrument?.ToJsonString());
        var (created, mandate) = await slice.PostAsync("/api/bil/v1/mandates", new { billingAccountId = account, instrumentId = instrument.Text("instrument.instrumentId") });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, mandate?.ToJsonString());
        var mandateId = mandate.Text("mandate.mandateId");
        (await slice.PostAsync("/api/bil/v1/mandates/sign", new { mandateId, signatureEvidenceId = "EVD-DD" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (changed, change) = await slice.PostAsync("/api/bil/v1/payment-plans/change", new { termId = policy.TermId, planCode = "ANNUAL", method = "DIRECT_DEBIT" });
        changed.StatusCode.ShouldBe(HttpStatusCode.OK, change?.ToJsonString());
        var invoice = (await slice.InvoiceOfAsync(policy.PolicyId)).Text("invoice.invoiceId");
        return (policy, account, invoice, mandateId, iban);
    }

    private static async Task<JsonNode?> RunAsync(BillingSlice slice)
    {
        var (response, body) = await slice.PostAsync(Run, new { });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body;
    }

    private static Task<string> ItemAsync(BillingSlice slice, string invoice, string column) =>
        slice.ScalarAsync<string>($"SELECT {column}::text FROM bil.dd_item WHERE invoice_id = '{invoice}' ORDER BY created_at DESC LIMIT 1");

    private static Task<decimal> NetAsync(BillingSlice slice, string account, string ledgerAccount) =>
        slice.ScalarAsync<decimal>($"""
            SELECT coalesce(sum(CASE l.side WHEN 'DEBIT' THEN l.amount ELSE -l.amount END), 0) FROM bil.ledger_line l
             WHERE l.billing_account_id = '{account}' AND l.account_code = '{ledgerAccount}'
            """);

    private static async Task InvariantsHoldAsync(BillingSlice slice, string account)
    {
        var (response, body) = await slice.PostAsync("/api/bil/v1/reconciliation/invariants", new { account });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["results"]!.AsArray().Count.ShouldBe(15);
        body["results"]!.AsArray().ShouldAllBe(x => x!.Text("status") == "PASS", body.ToJsonString());
    }

    [Fact]
    public async Task BO_BIL_06b_a_due_instalment_is_written_to_pain008_collected_once_settled_and_the_mandate_turns_RCUR()
    {
        var (policy, account, invoice, mandate, iban) = await DirectDebitPolicyAsync(_slice);
        var store = _slice.Policy.Factory.Services.GetRequiredService<InMemoryBankFileStore>();

        var run = await RunAsync(_slice);
        run!["submitted"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(1);
        (await ItemAsync(_slice, invoice, "state")).ShouldBe("COLLECTED");
        (await ItemAsync(_slice, invoice, "sequence_type")).ShouldBe("FRST");
        var endToEnd = await ItemAsync(_slice, invoice, "end_to_end_id");

        // The file: pain.008 with the creditor identity, the FRST block, the mandate and the debtor IBAN (only here); the tables hold the reference and hash.
        var file = await _slice.ScalarAsync<string>($"SELECT c.file_reference FROM bil.dd_collection c JOIN bil.dd_item i USING (collection_id) WHERE i.invoice_id = '{invoice}'");
        var xml = Encoding.UTF8.GetString(store.Read(file)!);
        xml.ShouldContain("pain.008.001.02");
        xml.ShouldContain("<SeqTp>FRST</SeqTp>");
        xml.ShouldContain("GR12ZZZ099999999999");
        xml.ShouldContain($"<EndToEndId>{endToEnd}</EndToEndId>");
        xml.ShouldContain(await _slice.ScalarAsync<string>($"SELECT mandate_reference FROM bil.mandate WHERE mandate_id = '{mandate}'"));
        xml.ShouldContain(iban);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.dd_collection t, bil.dd_item i WHERE t::text LIKE '%{iban}%' OR i::text LIKE '%{iban}%'")).ShouldBe(0);

        // Settled into a DIRECT_DEBIT receipt allocated to the invoice; the invoice is Paid.
        (await _slice.ScalarAsync<string>($"SELECT state FROM bil.invoice WHERE invoice_id = '{invoice}'")).ShouldBe("PAID");
        (await _slice.ScalarAsync<string>($"SELECT r.method || '/' || r.state || '/' || r.bank_reference FROM bil.receipt r WHERE r.receipt_id = (SELECT receipt_id FROM bil.dd_item WHERE invoice_id = '{invoice}')"))
            .ShouldBe($"DIRECT_DEBIT/ALLOCATED/{endToEnd}");
        (await NetAsync(_slice, account, "LA-10")).ShouldBe(policy.Total);

        // The mandate was used: first_used_at set and RCUR from now on.
        (await _slice.ScalarAsync<string>($"SELECT sequence_type || '/' || (first_used_at IS NOT NULL)::text FROM bil.mandate WHERE mandate_id = '{mandate}'")).ShouldBe("RCUR/true");

        // Collected once: another run does not touch the paid instalment.
        await RunAsync(_slice);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.dd_item WHERE invoice_id = '{invoice}'")).ShouldBe(1);
        await InvariantsHoldAsync(_slice, account);
    }

    [Fact]
    public async Task BO_BIL_06b_an_R_transaction_reverses_the_receipt_reopens_the_invoice_and_it_is_collected_again_as_RCUR()
    {
        var (policy, account, invoice, _, _) = await DirectDebitPolicyAsync(_slice);
        await RunAsync(_slice);
        var endToEnd = await ItemAsync(_slice, invoice, "end_to_end_id");
        var receipt = await ItemAsync(_slice, invoice, "receipt_id");

        var (response, body) = await _slice.PostAsync($"/dev/bank/direct-debits/{endToEnd}/return?reason=AM04", new { });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("state").ShouldBe("RETURNED");
        body!["returnEntryId"].ShouldNotBeNull();

        // The bank took the money back: receipt reversed, cash out of LA-10 and LA-11, invoice open again, return reason kept.
        (await _slice.ScalarAsync<string>($"SELECT state FROM bil.receipt WHERE receipt_id = '{receipt}'")).ShouldBe("REVERSED");
        (await _slice.ScalarAsync<string>($"SELECT state FROM bil.suspense_item WHERE receipt_id = '{receipt}'")).ShouldBe("RETURNED");
        (await _slice.ScalarAsync<string>($"SELECT state FROM bil.invoice WHERE invoice_id = '{invoice}'")).ShouldBeOneOf("DUE", "OVERDUE");
        (await _slice.ScalarAsync<string>($"SELECT reason_code FROM bil.dd_item WHERE end_to_end_id = '{endToEnd}'")).ShouldBe("AM04");
        (await NetAsync(_slice, account, "LA-10")).ShouldBe(0m);
        (await NetAsync(_slice, account, "LA-11")).ShouldBe(0m);
        (await NetAsync(_slice, account, "LA-02")).ShouldBe(policy.Total);
        await InvariantsHoldAsync(_slice, account);

        // The same status again changes nothing; a status for an unknown debit is a 404.
        (await _slice.PostAsync($"/dev/bank/direct-debits/{endToEnd}/return", new { })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await NetAsync(_slice, account, "LA-10")).ShouldBe(0m);
        (await _slice.PostAsync("/dev/bank/direct-debits/DDUNKNOWN/return", new { })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The invoice is collectable again: the next run submits a new item (RCUR, the mandate was used) and settles it.
        await RunAsync(_slice);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.dd_item WHERE invoice_id = '{invoice}'")).ShouldBe(2);
        (await ItemAsync(_slice, invoice, "sequence_type")).ShouldBe("RCUR");
        (await ItemAsync(_slice, invoice, "state")).ShouldBe("COLLECTED");
        (await _slice.ScalarAsync<string>($"SELECT state FROM bil.invoice WHERE invoice_id = '{invoice}'")).ShouldBe("PAID");
        (await NetAsync(_slice, account, "LA-10")).ShouldBe(policy.Total);
        await InvariantsHoldAsync(_slice, account);
    }

    [Fact]
    public async Task BO_BIL_06b_with_a_manual_bank_a_submitted_debit_waits_and_a_reject_before_settlement_frees_the_invoice()
    {
        await using var manual = await OpenAsync(manualBank: true);
        var (policy, account, invoice, _, _) = await DirectDebitPolicyAsync(manual);

        await RunAsync(manual);
        (await ItemAsync(manual, invoice, "state")).ShouldBe("SUBMITTED");
        (await manual.ScalarAsync<string>($"SELECT state FROM bil.invoice WHERE invoice_id = '{invoice}'")).ShouldBeOneOf("DUE", "OVERDUE");
        (await NetAsync(manual, account, "LA-10")).ShouldBe(0m);

        // In flight, an instalment is not submitted twice.
        await RunAsync(manual);
        (await manual.ScalarAsync<long>($"SELECT count(*) FROM bil.dd_item WHERE invoice_id = '{invoice}'")).ShouldBe(1);

        var endToEnd = await ItemAsync(manual, invoice, "end_to_end_id");
        (await manual.PostAsync($"/dev/bank/direct-debits/{endToEnd}/reject?reason=AC04", new { })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ItemAsync(manual, invoice, "state")).ShouldBe("REJECTED");
        (await manual.ScalarAsync<long>($"SELECT count(*) FROM bil.receipt WHERE bank_reference = '{endToEnd}'")).ShouldBe(0);

        // Collected again, then the bank settles it by hand.
        await RunAsync(manual);
        (await manual.ScalarAsync<long>($"SELECT count(*) FROM bil.dd_item WHERE invoice_id = '{invoice}'")).ShouldBe(2);
        var second = await ItemAsync(manual, invoice, "end_to_end_id");
        (await manual.PostAsync($"/dev/bank/direct-debits/{second}/settle", new { })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await manual.ScalarAsync<string>($"SELECT state FROM bil.invoice WHERE invoice_id = '{invoice}'")).ShouldBe("PAID");
        (await NetAsync(manual, account, "LA-10")).ShouldBe(policy.Total);
        await InvariantsHoldAsync(manual, account);
    }

    [Fact]
    public async Task BO_BIL_06b_without_a_bank_file_store_or_creditor_identity_nothing_is_collected()
    {
        await using var closed = await OpenAsync(fileStore: false);
        var (_, _, invoice, _, _) = await DirectDebitPolicyAsync(closed);
        var (response, body) = await closed.PostAsync(Run, new { });
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, body?.ToJsonString());
        body.Text("code").ShouldBe("BIL-ERR-METHOD-UNAVAILABLE");
        (await closed.ScalarAsync<long>($"SELECT count(*) FROM bil.dd_item WHERE invoice_id = '{invoice}'")).ShouldBe(0);

        // A direct-debit term without a usable mandate (cancelled after the plan was chosen) is skipped, not failed.
        var (policy, _, withdrawn, mandate, _) = await DirectDebitPolicyAsync(_slice);
        (await _slice.PostAsync("/api/bil/v1/mandates/cancel", new { mandateId = mandate })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await RunAsync(_slice);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.dd_item WHERE invoice_id = '{withdrawn}'")).ShouldBe(0);
        (await _slice.ScalarAsync<string>($"SELECT state FROM bil.invoice WHERE invoice_id = '{withdrawn}'")).ShouldBeOneOf("DUE", "OVERDUE");
        policy.Total.ShouldBeGreaterThan(0m);

        // Staff roles only.
        (await _slice.PostAsync(Run, new { }, Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
