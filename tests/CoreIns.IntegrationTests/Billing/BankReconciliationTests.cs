using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CoreIns.Modules.Billing.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static CoreIns.IntegrationTests.Bil.BillingSlice;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>
/// BO-BIL-11: bank reconciliation. A camt.053 or CSV statement is imported once (a repeat returns the first import), credits are matched by
/// reference and amount (matched, partial), what cannot be identified goes to suspense, a debit without a disbursement is a break, the
/// statement is refused when it is malformed or names another account, nothing is imported without a bank-file store (fail closed), and the
/// sub-ledger invariants INV-01..15 pass on a clean ledger and name the broken rows otherwise.
/// </summary>
public sealed class BankReconciliationTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Bank = "/api/bil/v1/reconciliation/bank";
    private const string Invariants = "/api/bil/v1/reconciliation/invariants";
    private readonly string _iban = DisbursementSlice.NewIban();
    private BillingSlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        _slice = new BillingSlice(database);
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private static object Money(decimal amount) => new { amount = amount.ToString(CultureInfo.InvariantCulture), currency = "EUR" };

    private static string Num(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private async Task<(BoundPolicy Policy, string AccountId, string InvoiceNumber)> BilledAsync()
    {
        var policy = await _slice.BindAsync();
        await _slice.DrainAsync();
        var invoice = await _slice.InvoiceOfAsync(policy.PolicyId);
        return (policy, invoice.Text("invoice.billingAccountId"), invoice.Text("invoice.invoiceNumber"));
    }

    private sealed record Entry(string Direction, decimal Amount, string? EndToEnd, string? Remittance);

    private static Entry Credit(decimal amount, string? endToEnd = null, string? remittance = null) => new("CRDT", amount, endToEnd, remittance);

    private static Entry Debit(decimal amount, string? endToEnd = null, string? remittance = null) => new("DBIT", amount, endToEnd, remittance);

    private string Camt(string statementId, IEnumerable<Entry> entries, decimal? opening = null, decimal? closing = null, string? iban = null)
    {
        var xml = new StringBuilder();
        xml.Append("""<?xml version="1.0" encoding="UTF-8"?><Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02"><BkToCstmrStmt><Stmt>""");
        xml.Append(CultureInfo.InvariantCulture, $"<Id>{statementId}</Id><Acct><Id><IBAN>{iban ?? _iban}</IBAN></Id><Ccy>EUR</Ccy></Acct>");
        if (opening is { } o)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<Bal><Tp><CdOrPrtry><Cd>OPBD</Cd></CdOrPrtry></Tp><Amt Ccy=\"EUR\">{Num(Math.Abs(o))}</Amt><CdtDbtInd>{(o < 0 ? "DBIT" : "CRDT")}</CdtDbtInd><Dt><Dt>{Today}</Dt></Dt></Bal>");
        }

        if (closing is { } c)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<Bal><Tp><CdOrPrtry><Cd>CLBD</Cd></CdOrPrtry></Tp><Amt Ccy=\"EUR\">{Num(Math.Abs(c))}</Amt><CdtDbtInd>{(c < 0 ? "DBIT" : "CRDT")}</CdtDbtInd><Dt><Dt>{Today}</Dt></Dt></Bal>");
        }

        foreach (var e in entries)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<Ntry><Amt Ccy=\"EUR\">{Num(e.Amount)}</Amt><CdtDbtInd>{e.Direction}</CdtDbtInd><Sts>BOOK</Sts><BookgDt><Dt>{Today}</Dt></BookgDt><ValDt><Dt>{Today}</Dt></ValDt><NtryDtls><TxDtls>");
            if (e.EndToEnd is not null)
            {
                xml.Append(CultureInfo.InvariantCulture, $"<Refs><EndToEndId>{e.EndToEnd}</EndToEndId></Refs>");
            }

            if (e.Remittance is not null)
            {
                xml.Append(CultureInfo.InvariantCulture, $"<RmtInf><Ustrd>{e.Remittance}</Ustrd></RmtInf>");
            }

            xml.Append("</TxDtls></NtryDtls></Ntry>");
        }

        xml.Append("</Stmt></BkToCstmrStmt></Document>");
        return xml.ToString();
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> ImportAsync(string content, string format = "CAMT053", string? iban = null, string roles = Billing) =>
        _slice.PostAsync(Bank, new { bankAccount = iban ?? _iban, format, content }, roles);

    private static JsonNode Line(JsonNode? body, int lineNo) => body!["lines"]!.AsArray().Single(l => l!["lineNo"]!.GetValue<int>() == lineNo)!;

    [Fact]
    public async Task BO_BIL_11_credits_are_matched_by_reference_and_amount_and_the_rest_goes_to_suspense()
    {
        var (policyA, accountA, numberA) = await BilledAsync();
        var (policyB, _, numberB) = await BilledAsync();

        // A receipt staff recorded earlier, with its bank reference and the full open amount; and one below the open amount, left unapplied.
        var (_, exact) = await _slice.PostAsync("/api/bil/v1/payments/take", new { billingAccountId = accountA, amount = Money(policyA.Total), method = "BANK_TRANSFER", bankReference = "REF-EXACT" });
        var (_, short1) = await _slice.PostAsync("/api/bil/v1/payments/take", new { billingAccountId = accountA, amount = Money(50m), method = "BANK_TRANSFER", bankReference = "REF-SHORT" });
        var shortReceipt = short1.Text("receipt.receiptId");
        exact.Text("allocationOutcome").ShouldBe("ALLOCATED");

        var entries = new[]
        {
            Credit(policyA.Total, "REF-EXACT"),                                             // 1: the receipt exists, same amount
            Credit(60m, "REF-SHORT"),                                                       // 2: the receipt exists, other amount
            Credit(policyB.Total, remittance: $"Premium {numberB} thank you"),             // 3: names an invoice, equals its open amount
            Credit(25m, remittance: "Unknown payer, no reference"),                         // 4: nothing identifiable
            Debit(12m, "NOT-A-DISBURSEMENT"),                                               // 5: no disbursement carries it
        };
        var (response, body) = await ImportAsync(Camt("STMT-1", entries));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("status").ShouldBe("BREAKS");
        body!["duplicate"]!.GetValue<bool>().ShouldBeFalse();

        Line(body, 1).Text("status").ShouldBe("MATCHED");
        Line(body, 1).Text("receiptId").ShouldBe(exact.Text("receipt.receiptId"));
        Line(body, 2).Text("status").ShouldBe("PARTIAL");
        Line(body, 2).Text("receiptId").ShouldBe(shortReceipt);
        Amount(Line(body, 2)["matchedAmount"]).ShouldBe(50m);
        Line(body, 3).Text("status").ShouldBe("MATCHED");
        Line(body, 4).Text("status").ShouldBe("SUSPENSE");
        Line(body, 4).Text("suspenseItemId").ShouldNotBeNullOrEmpty();
        Line(body, 5).Text("status").ShouldBe("UNMATCHED");
        body.Text("statement.lineCount").ShouldBe("5");
        body.Text("statement.matchedCount").ShouldBe("2");
        body.Text("statement.partialCount").ShouldBe("1");
        body.Text("statement.suspenseCount").ShouldBe("1");
        body.Text("statement.unmatchedCount").ShouldBe("1");
        body.Text("statement.maskedBankAccount").ShouldBe("****" + _iban[^4..]);
        body["breaks"]!.AsArray().Select(b => b!.Text("kind")).ShouldBe(["PARTIAL_MATCH", "IN_SUSPENSE", "UNMATCHED_DEBIT"]);
        Amount(body["breaks"]![0]!["difference"]).ShouldBe(10m);

        // Line 3 recorded the money on the named invoice's account and paid it in full; line 4 is unapplied cash on the suspense account.
        (await _slice.InvoiceOfAsync(policyB.PolicyId)).Text("invoice.state").ShouldBe("PAID");
        var (_, suspense) = await _slice.GetAsync("/api/bil/v1/suspense");
        suspense!["items"]!.AsArray().Any(i => i!.Text("suspenseItemId") == Line(body, 4).Text("suspenseItemId")).ShouldBeTrue();
        (await _slice.ScalarAsync<long>("SELECT count(*) FROM bil.billing_account WHERE account_type = 'SUSPENSE'")).ShouldBe(1);
        numberA.ShouldNotBeNullOrEmpty();

        // The ledger is still sound after the import.
        var (invResponse, invariants) = await _slice.PostAsync(Invariants, new { });
        invResponse.StatusCode.ShouldBe(HttpStatusCode.OK, invariants?.ToJsonString());
        // The database is shared by the tests of this class: another test may have left a deliberately wrong statement balance (INV-14).
        invariants!["results"]!.AsArray().Count.ShouldBe(15);
        invariants["results"]!.AsArray().Where(x => x!.Text("invariantId") != "INV-14").ShouldAllBe(x => x!.Text("status") == "PASS", invariants.ToJsonString());
    }

    [Fact]
    public async Task BO_BIL_11_importing_the_same_statement_again_returns_the_first_import_and_posts_nothing()
    {
        var content = Camt("STMT-IDEMP", [Credit(31.50m, remittance: "no reference at all")]);
        var (first, one) = await ImportAsync(content);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, one?.ToJsonString());
        one!["duplicate"]!.GetValue<bool>().ShouldBeFalse();
        var receipts = await _slice.ScalarAsync<long>("SELECT count(*) FROM bil.receipt");

        var (second, two) = await ImportAsync(content);
        second.StatusCode.ShouldBe(HttpStatusCode.OK, two?.ToJsonString());
        two!["duplicate"]!.GetValue<bool>().ShouldBeTrue();
        two.Text("statement.statementId").ShouldBe(one.Text("statement.statementId"));
        two.Text("status").ShouldBe(one.Text("status"));
        (await _slice.ScalarAsync<long>("SELECT count(*) FROM bil.receipt")).ShouldBe(receipts);
        (await _slice.ScalarAsync<long>("SELECT count(*) FROM bil.bank_statement WHERE statement_ref = 'STMT-IDEMP'")).ShouldBe(1);

        // The same statement reference with other content is a conflict, not a second statement.
        var (conflict, refused) = await ImportAsync(Camt("STMT-IDEMP", [Credit(99m, remittance: "changed")]));
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict, refused?.ToJsonString());
        refused.Text("code").ShouldBe("BIL-ERR-STATEMENT-CONFLICT");
    }

    [Fact]
    public async Task BO_BIL_11_a_csv_statement_is_imported_and_a_malformed_file_is_refused_with_the_line_named()
    {
        var csv = $"bookingDate,valueDate,amount,currency,reference,endToEndId\n{Today},{Today},40.00,EUR,\"Transfer, unknown\",\n{Today},,-5.00,EUR,bank fee,\n";
        var (response, body) = await ImportAsync(csv, "CSV");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("statement.format").ShouldBe("CSV");
        Line(body, 1).Text("direction").ShouldBe("CREDIT");
        Line(body, 1).Text("status").ShouldBe("SUSPENSE");
        Line(body, 2).Text("direction").ShouldBe("DEBIT");
        Line(body, 2).Text("status").ShouldBe("UNMATCHED");
        body.Text("statement.statementRef").ShouldStartWith("CSV-");

        var (bad, refused) = await ImportAsync($"bookingDate,amount,currency,reference\n{Today},12.345,EUR,x\n", "CSV");
        bad.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, refused?.ToJsonString());
        refused.Text("code").ShouldBe("BIL-ERR-STATEMENT-INVALID");
        refused.Text("detail").ShouldContain("Line 2");

        var (noXml, refusedXml) = await ImportAsync("<not-closed>", "CAMT053");
        noXml.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, refusedXml?.ToJsonString());
        var (other, refusedAccount) = await ImportAsync(Camt("STMT-OTHER", [Credit(1m)], iban: DisbursementSlice.NewIban()));
        other.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, refusedAccount?.ToJsonString());
        (await _slice.ScalarAsync<long>("SELECT count(*) FROM bil.bank_statement WHERE statement_ref = 'STMT-OTHER'")).ShouldBe(0);
    }

    [Fact]
    public async Task BO_BIL_11_a_balance_that_does_not_add_up_is_a_break_and_invariant_14_names_the_statement()
    {
        var (response, body) = await ImportAsync(Camt("STMT-BAL", [Credit(10m, remittance: "x"), Debit(4m, "ZZ")], opening: 100m, closing: 120m));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["breaks"]!.AsArray().Any(b => b!.Text("kind") == "BALANCE_MISMATCH").ShouldBeTrue();
        Amount(body["breaks"]!.AsArray().Single(b => b!.Text("kind") == "BALANCE_MISMATCH")!["difference"]).ShouldBe(14m);

        var (_, invariants) = await _slice.PostAsync(Invariants, new { });
        invariants.Text("status").ShouldBe("FAIL");
        var failed = invariants!["results"]!.AsArray().Where(r => r!.Text("status") == "FAIL").ToList();
        failed.Count.ShouldBe(1);
        failed[0]!.Text("invariantId").ShouldBe("INV-14");
        failed[0]!["samples"]!.AsArray().Select(x => x!.GetValue<string>()).ShouldContain(body.Text("statement.statementId"));
    }

    [Fact]
    public async Task BO_BIL_11_only_billing_staff_reconcile_and_a_missing_bank_file_store_fails_closed()
    {
        (await ImportAsync(Camt("STMT-ROLE", [Credit(1m)]), roles: Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _slice.PostAsync(Invariants, new { }, Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await using var closed = new BillingSlice(database, services => services.RemoveAll<IBankFileStore>());
        await closed.SeedAsync();
        var (response, body) = await closed.PostAsync(Bank, new { bankAccount = _iban, format = "CAMT053", content = Camt("STMT-CLOSED", [Credit(1m)]) });
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, body?.ToJsonString());
        body.Text("code").ShouldBe("BIL-ERR-METHOD-UNAVAILABLE");
        (await closed.ScalarAsync<long>("SELECT count(*) FROM bil.bank_statement")).ShouldBe(0);
    }
}
