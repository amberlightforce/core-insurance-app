using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Party;
using CoreIns.IntegrationTests.Product;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.SharedKernel.Identifiers;
using Npgsql;
using static CoreIns.IntegrationTests.Finance.FinanceSlice;

namespace CoreIns.IntegrationTests.Finance;

/// <summary>
/// The SL-FIN slice on a real PostgreSQL 17 through the real Host: BIL BillingEntryPosted posted into balanced IFRS17
/// journals with data-driven rules (D-SLC-12), POL PolicyBound as context, waiting and release, idempotent intake,
/// intake exceptions, and the journal and posting-rule APIs (W5-FIN-01/02 subset).
/// </summary>
public sealed class FinanceIntakeTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Day = "2026-11-02";
    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;
    private FinanceSlice _slice = null!;
    private NpgsqlDataSource _db = null!;
    private string? _artefactHash;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _client = _factory.CreateClient();
        _slice = new FinanceSlice(_factory);
        _db = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        // The PFC artefact PolicyBound points at (FIN loads its charge types and GL keys from PFC); importing again is a no-op.
        var (response, body) = await ProductApi.ImportAsync(_client, ProductApi.Seed("MOTOR-FIN"));
        response.IsSuccessStatusCode.ShouldBeTrue(body?.ToJsonString());
        _artefactHash = body.Text("artefactHash");
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private Policy NewPolicy() => Policy.New($"POL{Random.Shared.Next(100_000_000, 999_999_999)}", "MOTOR-FIN", _artefactHash!);

    /// <summary>The E2E-01 money path for one annual motor policy: written (per coverage + IPT), billed, IPT due, received, allocated.</summary>
    private async Task<(Policy Policy, Guid Account, Guid Invoice, Guid Receipt)> HappyPathAsync(bool policyFirst = true)
    {
        var policy = NewPolicy();
        var account = Guid.CreateVersion7();
        var invoice = Guid.CreateVersion7();
        var receipt = Guid.CreateVersion7();
        if (policyFirst)
        {
            await _slice.PolicyBoundAsync(policy);
        }

        await _slice.EntryAsync(account, "WRITTEN", Day,
            Line("LA-01", "DEBIT", "240.00", policy, account, "PREM-MTPL", "PREMIUM", "MTPL"),
            Line("LA-04", "CREDIT", "240.00", policy, account, "PREM-MTPL", "PREMIUM", "MTPL"));
        await _slice.EntryAsync(account, "WRITTEN", Day,
            Line("LA-01", "DEBIT", "160.00", policy, account, "PREM-OD", "PREMIUM", "OWN-DAMAGE"),
            Line("LA-04", "CREDIT", "160.00", policy, account, "PREM-OD", "PREMIUM", "OWN-DAMAGE"));
        await _slice.EntryAsync(account, "WRITTEN", Day,
            Line("LA-01", "DEBIT", "60.00", policy, account, "GR-IPT", "TAX"),
            Line("LA-27", "CREDIT", "60.00", policy, account, "GR-IPT", "TAX"));
        if (!policyFirst)
        {
            await _slice.PolicyBoundAsync(policy);
        }

        await _slice.EntryAsync(account, "BILLED", Day,
            Line("LA-02", "DEBIT", "460.00", policy, account, invoiceId: invoice),
            Line("LA-01", "CREDIT", "460.00", policy, account, invoiceId: invoice));
        await _slice.EntryAsync(account, "IPT_DUE", Day,
            Line("LA-27", "DEBIT", "60.00", policy, account, "GR-IPT", "TAX", invoiceId: invoice),
            Line("LA-06", "CREDIT", "60.00", policy, account, "GR-IPT", "TAX", invoiceId: invoice));
        await _slice.EntryAsync(account, "RECEIVED", Day,
            Line("LA-10", "DEBIT", "460.00", billingAccount: account, receiptId: receipt),
            Line("LA-11", "CREDIT", "460.00", billingAccount: account, receiptId: receipt));
        await _slice.EntryAsync(account, "ALLOCATED", Day,
            Line("LA-11", "DEBIT", "460.00", policy, account, invoiceId: invoice, receiptId: receipt),
            Line("LA-02", "CREDIT", "460.00", policy, account, invoiceId: invoice, receiptId: receipt));
        await _slice.DrainAsync();
        return (policy, account, invoice, receipt);
    }

    private Task<decimal> NetAsync(Policy policy, string account) =>
        ScalarAsync<decimal>(_db, $"""
            SELECT coalesce(sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END), 0) FROM fin.journal_line l
             WHERE l.account_code = '{account}' AND (l.policy_number = '{policy.Number}' OR l.receipt_id IN (
                   SELECT receipt_id FROM fin.journal_line WHERE policy_number = '{policy.Number}' AND receipt_id IS NOT NULL))
            """);

    [Fact]
    public async Task REQ_FIN_036_048_050_068_075_the_E2E01_money_path_posts_balanced_IFRS17_journals_from_BIL_entries()
    {
        var (policy, _, invoice, receipt) = await HappyPathAsync();

        // One journal per BIL entry (7), all POSTED, in the IFRS17 book with rule set v1 (REQ-FIN-036, -059).
        (await ScalarAsync<long>(_db, $"""
            SELECT count(DISTINCT j.journal_id) FROM fin.journal_entry j JOIN fin.journal_line l USING (journal_id)
             WHERE l.policy_number = '{policy.Number}' OR l.receipt_id = '{receipt}'
            """)).ShouldBe(7);
        (await ScalarAsync<long>(_db, $"""
            SELECT count(*) FROM fin.journal_entry WHERE book <> 'IFRS17' OR rule_set_version <> 1 OR source_event_type <> 'BillingEntryPosted'
            """)).ShouldBe(0);

        // Written premium per coverage on the LRC account derived from the PFC GL key (REQ-FIN-050, D-SLC-10b).
        (await ScalarAsync<string>(_db, $"""
            SELECT string_agg(coverage_code || ':' || gl_key || ':' || side || ':' || amount::text, ',' ORDER BY coverage_code)
              FROM fin.journal_line WHERE policy_number = '{policy.Number}' AND account_code = 'GL-2110'
            """)).ShouldBe("MTPL:PREM-MOTOR-MTPL:CREDIT:240.0000,OWN-DAMAGE:PREM-MOTOR-OD:CREDIT:160.0000");

        // Account balances after the whole path: premium and IPT stay, receivables and clearing net to zero, cash in bank.
        (await NetAsync(policy, "GL-2110")).ShouldBe(-400.00m);
        (await NetAsync(policy, "GL-2410")).ShouldBe(-60.00m);
        (await ScalarAsync<string>(_db, $"SELECT gl_key FROM fin.journal_line WHERE policy_number = '{policy.Number}' AND account_code = 'GL-2410'")).ShouldBe("TAX-IPT");
        (await NetAsync(policy, "GL-2411")).ShouldBe(0m);
        (await NetAsync(policy, "GL-1215")).ShouldBe(0m);
        (await NetAsync(policy, "GL-1210")).ShouldBe(0m);
        (await NetAsync(policy, "GL-2540")).ShouldBe(0m);
        (await NetAsync(policy, "GL-1110")).ShouldBe(460.00m);

        // Every journal balances (REQ-FIN-068) and the lines carry the dimensions (REQ-FIN-075).
        (await ScalarAsync<long>(_db, """
            SELECT count(*) FROM (SELECT journal_id FROM fin.journal_line GROUP BY journal_id
                                  HAVING sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END) <> 0) t
            """)).ShouldBe(0);
        (await ScalarAsync<long>(_db, $"""
            SELECT count(*) FROM fin.journal_line WHERE policy_number = '{policy.Number}'
               AND (product_code <> 'MOTOR-FIN' OR product_version <> '1.0' OR policy_term_id <> '{policy.TermId}' OR policy_id <> '{policy.PolicyId}')
            """)).ShouldBe(0);
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_line WHERE invoice_id = '{invoice}'")).ShouldBe(6);

        // JournalPosted per journal through the outbox (REQ-FIN-085) and gapless numbers from PLT (REQ-FIN-069).
        (await ScalarAsync<long>(_db, $"""
            SELECT count(*) FROM plt.event_archive e WHERE e.event_type = 'JournalPosted'
               AND (e.business_keys->>'journalId')::uuid IN (SELECT DISTINCT journal_id FROM fin.journal_line WHERE policy_number = '{policy.Number}')
            """)).ShouldBe(6);
        (await ScalarAsync<long>(_db, "SELECT count(*) FROM fin.journal_entry WHERE journal_number !~ '^JNL2026[0-9]{10}$'")).ShouldBe(0);

        // The query API by policy business key and dates; get by number (REQ-FIN-078, -079).
        var (queried, page) = await PartyApi.SendAsync(_client, HttpMethod.Get,
            $"/api/fin/v1/journals/query?policyNumber={policy.Number}&accountingDateFrom={Day}&accountingDateTo={Day}&limit=4", roles: FinanceRole);
        queried.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        page!["items"]!.AsArray().Count.ShouldBe(4);
        var cursor = page["nextCursor"]!.GetValue<string>();
        var (_, rest) = await PartyApi.SendAsync(_client, HttpMethod.Get,
            $"/api/fin/v1/journals/query?policyNumber={policy.Number}&cursor={Uri.EscapeDataString(cursor)}&limit=4", roles: FinanceRole);
        rest!["items"]!.AsArray().Count.ShouldBe(2);
        rest["nextCursor"].ShouldBeNull();

        var first = page["items"]![0]!["journal"]!;
        first.Text("book").ShouldBe("IFRS17");
        first.Text("period").ShouldBe("2026-11");
        first.Text("sourceModule").ShouldBe("BIL");
        first["lines"]!.AsArray().Count.ShouldBe(2);
        first["totals"]![0]!.Text("amount").ShouldBe(first["lines"]![0]!["amount"]!.Text("amount"));
        var premiumLine = page["items"]!.AsArray().SelectMany(i => i!["journal"]!["lines"]!.AsArray()).First(l => l!.Text("account") == "GL-2110")!;
        premiumLine.Text("accountName.el").ShouldBe("ΥΕΚ εκτός συνιστώσας ζημίας");
        premiumLine.Text("accountOrigin").ShouldBe("PRD09_ILLUSTRATIVE");
        premiumLine.Text("dimensions.policyNumber").ShouldBe(policy.Number);
        premiumLine.Text("amount.amount").ShouldBe("240.00");

        var (got, journal) = await PartyApi.SendAsync(_client, HttpMethod.Get, $"/api/fin/v1/journals/{first.Text("journalNumber")}", roles: FinanceRole);
        got.StatusCode.ShouldBe(HttpStatusCode.OK, journal?.ToJsonString());
        journal.Text("journal.journalId").ShouldBe(first.Text("journalId"));
    }

    [Fact]
    public async Task REQ_FIN_040_DARC26_an_entry_waits_for_its_policy_context_and_posts_when_PolicyBound_arrives()
    {
        var policy = NewPolicy();
        var account = Guid.CreateVersion7();
        await _slice.EntryAsync(account, "WRITTEN", Day,
            Line("LA-01", "DEBIT", "240.00", policy, account, "PREM-MTPL", "PREMIUM", "MTPL"),
            Line("LA-04", "CREDIT", "240.00", policy, account, "PREM-MTPL", "PREMIUM", "MTPL"));
        await _slice.DrainAsync();
        (await ScalarAsync<string>(_db, $"SELECT status || '/' || waiting_on FROM fin.business_event WHERE event_type = 'BillingEntryPosted' AND payload::text LIKE '%{policy.TermId}%'"))
            .ShouldBe($"WAITING/term:{policy.TermId}");
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_line WHERE policy_term_id = '{policy.TermId}'")).ShouldBe(0);

        await _slice.PolicyBoundAsync(policy);
        await _slice.DrainAsync();

        (await ScalarAsync<string>(_db, $"SELECT status || '/' || attempts FROM fin.business_event WHERE event_type = 'BillingEntryPosted' AND payload::text LIKE '%{policy.TermId}%'"))
            .ShouldBe("POSTED/1");
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_line WHERE policy_number = '{policy.Number}'")).ShouldBe(2);
        (await ScalarAsync<string>(_db, $"SELECT status FROM fin.business_event WHERE event_type = 'PolicyBound' AND policy_id = '{policy.PolicyId}'")).ShouldBe("NO_POSTING");
    }

    [Fact]
    public async Task REQ_FIN_040_a_released_entry_that_fails_is_suspended_alone_and_the_policy_context_still_commits()
    {
        var policy = NewPolicy();
        var account = Guid.CreateVersion7();
        var failing = await _slice.EntryAsync(account, "WRITTEN", Day,
            Line("LA-01", "DEBIT", "13.13", policy, account, "PREM-MTPL", "PREMIUM", "MTPL"),
            Line("LA-04", "CREDIT", "13.13", policy, account, "PREM-MTPL", "PREMIUM", "MTPL"));
        var healthy = await _slice.EntryAsync(account, "WRITTEN", Day,
            Line("LA-01", "DEBIT", "160.00", policy, account, "PREM-OD", "PREMIUM", "OWN-DAMAGE"),
            Line("LA-04", "CREDIT", "160.00", policy, account, "PREM-OD", "PREMIUM", "OWN-DAMAGE"));
        await _slice.DrainAsync();

        // An injected database failure for one of the two waiting entries (test-only trigger, dropped afterwards).
        await database.ExecuteAsSuperuserAsync("""
            CREATE OR REPLACE FUNCTION fin.test_injected_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.amount = 13.13 THEN RAISE EXCEPTION 'injected failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER trg_test_injected_failure BEFORE INSERT ON fin.journal_line FOR EACH ROW EXECUTE FUNCTION fin.test_injected_failure();
            """, TestContext.Current.CancellationToken);
        try
        {
            await _slice.PolicyBoundAsync(policy);
            await _slice.DrainAsync();
        }
        finally
        {
            await database.ExecuteAsSuperuserAsync(
                "DROP TRIGGER trg_test_injected_failure ON fin.journal_line; DROP FUNCTION fin.test_injected_failure();", TestContext.Current.CancellationToken);
        }

        (await ScalarAsync<string>(_db, $"SELECT status || '/' || exception_reason FROM fin.business_event WHERE source_event_id = '{failing.EventId.Value}'"))
            .ShouldBe("SUSPENDED/POSTING_ERROR");
        (await ScalarAsync<string>(_db, $"SELECT status FROM fin.business_event WHERE source_event_id = '{healthy.EventId.Value}'")).ShouldBe("POSTED");
        (await ScalarAsync<string>(_db, $"SELECT status FROM fin.business_event WHERE event_type = 'PolicyBound' AND policy_id = '{policy.PolicyId}'")).ShouldBe("NO_POSTING");
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.policy_context WHERE policy_term_id = '{policy.TermId}'")).ShouldBe(1);
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_line WHERE policy_number = '{policy.Number}'")).ShouldBe(2);

        // No JournalPosted for the rolled-back attempt: one event per journal that exists.
        (await ScalarAsync<long>(_db, $"""
            SELECT count(*) FROM plt.event_archive e WHERE e.event_type = 'JournalPosted'
               AND NOT EXISTS (SELECT 1 FROM fin.journal_entry j WHERE j.journal_id = (e.business_keys->>'journalId')::uuid)
            """)).ShouldBe(0);
        (await ScalarAsync<long>(_db, $"""
            SELECT count(*) FROM plt.event_archive WHERE event_type = 'BusinessEventSuspended'
               AND business_keys->>'businessEventId' = (SELECT business_event_id::text FROM fin.business_event WHERE source_event_id = '{failing.EventId.Value}')
            """)).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_FIN_031_a_redelivered_event_is_posted_once()
    {
        var (policy, _, _, _) = await HappyPathAsync();
        var before = await ScalarAsync<long>(_db, "SELECT count(*) FROM fin.journal_entry");

        // Replay everything: outbox back to Pending and the platform's processed markers gone, so only FIN's own
        // idempotency (unique source event id) stands between a redelivery and a second journal.
        await database.ExecuteAsSuperuserAsync(
            "UPDATE plt.outbox_message SET status = 'Pending', dispatched_at = NULL; DELETE FROM plt.processed_event WHERE handler LIKE 'FIN.%'",
            TestContext.Current.CancellationToken);
        await _slice.DrainAsync();

        (await ScalarAsync<long>(_db, "SELECT count(*) FROM fin.journal_entry")).ShouldBe(before);
        (await NetAsync(policy, "GL-1110")).ShouldBe(460.00m);
    }

    [Fact]
    public async Task REQ_FIN_080_084_an_entry_without_a_rule_is_an_intake_exception_never_a_suspense_posting()
    {
        var account = Guid.CreateVersion7();
        var entry = await _slice.EntryAsync(account, "COMMISSION", Day,
            Line("LA-15", "DEBIT", "40.00", billingAccount: account),
            Line("LA-14", "CREDIT", "40.00", billingAccount: account));
        var foreign = await _slice.EntryAsync(account, "RECEIVED", Day,
            Line("LA-10", "DEBIT", "10.00", billingAccount: account, currency: "USD"),
            Line("LA-11", "CREDIT", "10.00", billingAccount: account, currency: "USD"));
        var fractional = await _slice.EntryAsync(account, "RECEIVED", Day,
            Line("LA-10", "DEBIT", "10.005", billingAccount: account),
            Line("LA-11", "CREDIT", "10.005", billingAccount: account));
        await _slice.DrainAsync();

        (await ScalarAsync<string>(_db, $"SELECT status || '/' || exception_reason FROM fin.business_event WHERE source_event_id = '{entry.EventId.Value}'")).ShouldBe("SUSPENDED/NO_RULE");
        (await ScalarAsync<string>(_db, $"SELECT exception_reason FROM fin.business_event WHERE source_event_id = '{foreign.EventId.Value}'")).ShouldBe("RATE_MISSING");
        (await ScalarAsync<string>(_db, $"SELECT exception_reason FROM fin.business_event WHERE source_event_id = '{fractional.EventId.Value}'")).ShouldBe("PRECISION");
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_line WHERE billing_account_id = '{account}'")).ShouldBe(0);
        (await ScalarAsync<long>(_db, $"""
            SELECT count(*) FROM plt.event_archive WHERE event_type = 'BusinessEventSuspended'
               AND (business_keys->>'businessEventId')::uuid IN (SELECT business_event_id FROM fin.business_event WHERE billing_account_id = '{account}')
            """)).ShouldBe(3);
    }

    [Fact]
    public async Task REQ_FIN_030_036_POL_and_BIL_context_events_are_recorded_and_never_journalised()
    {
        var policy = NewPolicy();
        var account = Guid.CreateVersion7();
        var delta = Sample("pol", "ChargeDeltaEmitted");
        delta["termId"] = policy.TermId.ToString();
        var transaction = policy.TransactionId;
        await _slice.PublishAsync(ChargeDeltaEmittedV1.Descriptor, "Policy", policy.PolicyId.ToString(), delta,
            BusinessKeys.Empty.With("policyId", policy.PolicyId.ToString()).With("chargeId", Guid.CreateVersion7().ToString())
                .With("policyTermId", policy.TermId.ToString()).With("transactionId", transaction.ToString()),
            new CoreIns.Platform.Events.EventSet(transaction, 1, 1));
        foreach (var (contract, type, keys) in new (CoreIns.Platform.Contracts.Events.EventContract, string, BusinessKeys)[]
                 {
                     (InvoiceIssuedV1.Descriptor, "InvoiceIssued", BusinessKeys.Empty.With("billingAccountId", account.ToString()).With("invoiceId", Guid.CreateVersion7().ToString())),
                     (PaymentReceivedV1.Descriptor, "PaymentReceived", BusinessKeys.Empty.With("billingAccountId", account.ToString()).With("receiptId", Guid.CreateVersion7().ToString())),
                     (CashAllocatedV1.Descriptor, "CashAllocated", BusinessKeys.Empty.With("billingAccountId", account.ToString()).With("receiptId", Guid.CreateVersion7().ToString())),
                 })
        {
            await _slice.PublishAsync(contract, "BillingAccount", account.ToString(), Sample("bil", type), keys);
        }

        await _slice.DrainAsync();

        (await ScalarAsync<string>(_db, $"""
            SELECT string_agg(event_type || ':' || relevance || ':' || status, ',' ORDER BY event_type) FROM fin.business_event
             WHERE billing_account_id = '{account}' OR policy_id = '{policy.PolicyId}'
            """)).ShouldBe("CashAllocated:CONTEXT:NO_POSTING,ChargeDeltaEmitted:CONTEXT:NO_POSTING,InvoiceIssued:CONTEXT:NO_POSTING,PaymentReceived:CONTEXT:NO_POSTING");
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_line WHERE billing_account_id = '{account}' OR policy_id = '{policy.PolicyId}'")).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_FIN_048_049_052_the_posting_rules_in_force_are_listed_with_key_specificity_and_target()
    {
        var (response, body) = await PartyApi.SendAsync(_client, HttpMethod.Get, $"/api/fin/v1/posting-rules?book=IFRS17&validAt={Day}&limit=200", roles: FinanceRole);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var items = body!["items"]!.AsArray();
        items.Count.ShouldBe(16);
        var premium = items.Single(i => i.Text("ruleCode") == "WR-PREMIUM")!;
        premium.Text("entryType").ShouldBe("WRITTEN");
        premium.Text("sourceAccount").ShouldBe("LA-04");
        premium.Text("chargeCategory").ShouldBe("PREMIUM");
        premium.Text("deriveFrom").ShouldBe("GL_KEY");
        premium.Text("specificity").ShouldBe("1");
        premium.Text("ruleSetVersion").ShouldBe("1");
        premium.Text("contentHash").ShouldMatch("^[0-9a-f]{64}$");
        premium.Text("accountOrigin").ShouldBe("null");
        var cash = items.Single(i => i.Text("ruleCode") == "RC-CASH")!;
        cash.Text("account").ShouldBe("GL-1110");
        cash.Text("accountOrigin").ShouldBe("PRD09_ILLUSTRATIVE");
        items.Single(i => i.Text("ruleCode") == "RC-UNALLOCATED")!.Text("accountOrigin").ShouldBe("TECHNICAL_PLACEHOLDER");
        items.Single(i => i.Text("ruleCode") == "ID-PAYABLE")!.Text("deriveFrom").ShouldBe("GL_KEY");

        var (before, empty) = await PartyApi.SendAsync(_client, HttpMethod.Get, "/api/fin/v1/posting-rules?validAt=2025-12-31", roles: FinanceRole);
        before.StatusCode.ShouldBe(HttpStatusCode.OK);
        empty!["items"]!.AsArray().Count.ShouldBe(0);

        var (bad, problem) = await PartyApi.SendAsync(_client, HttpMethod.Get, "/api/fin/v1/posting-rules?validAt=02-11-2026", roles: FinanceRole);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        problem.Text("code").ShouldBe("FIN-ERR-VALIDATION");
    }

    [Fact]
    public async Task REQ_FIN_078_the_journal_APIs_need_a_finance_permission_and_hide_unknown_journals()
    {
        (await PartyApi.SendAsync(_client, HttpMethod.Get, "/api/fin/v1/journals/query", roles: PartyApi.Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PartyApi.SendAsync(_client, HttpMethod.Get, "/api/fin/v1/posting-rules", roles: PartyApi.Billing)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (missing, problem) = await PartyApi.SendAsync(_client, HttpMethod.Get, $"/api/fin/v1/journals/{Guid.CreateVersion7()}", roles: FinanceRole);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        problem.Text("code").ShouldBe("FIN-ERR-NOT-FOUND");
        (await PartyApi.SendAsync(_client, HttpMethod.Get, "/api/fin/v1/journals/query?accountingDateFrom=yesterday", roles: FinanceRole)).Response.StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
    }
}
