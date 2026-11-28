using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Finance.Contracts;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Platform.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static CoreIns.IntegrationTests.Bil.BillingSlice;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>
/// BO-BIL-13: write-offs (propose, decide by a second person with BIL.WRITE_OFF authority, post; a residual within the tolerance is written
/// off at once) and money transfers between accounts of one payer. Every test ends with the sub-ledger invariants INV-01..15 (BO-BIL-11) and,
/// where FIN journalises the entry, with the BIL and FIN totals equal.
/// </summary>
public sealed class WriteOffTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Manager = "Staff.BillingManager";
    private BillingSlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _slice = new BillingSlice(database);
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private static object Money(decimal amount) => new { amount = amount.ToString(CultureInfo.InvariantCulture), currency = "EUR" };

    private static async Task<(HttpResponseMessage Response, JsonNode? Body)> AsAsync(BillingSlice slice, string path, object body, string roles, string user, Guid? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Test-Roles", roles);
        request.Headers.Add("X-Test-User", user);
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        request.Headers.AcceptLanguage.ParseAdd("en");
        request.Content = JsonContent.Create(body);
        var response = await slice.Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> ProposeAsync(BillingSlice slice, string invoiceId, decimal amount, string user = "alice") =>
        AsAsync(slice, "/api/bil/v1/write-offs/propose", new { invoiceId, amount = Money(amount), reasonCode = "UNCOLLECTIBLE", comment = "debtor untraceable" }, "Staff.Billing", user);

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> DecideAsync(BillingSlice slice, string writeOffId, string decision, string user = "bob", string roles = Manager) =>
        AsAsync(slice, "/api/bil/v1/write-offs/decide", new { writeOffId, decision, comment = "checked" }, roles, user);

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(BillingSlice slice, string writeOffId, string user = "carol") =>
        AsAsync(slice, "/api/bil/v1/write-offs/post", new { writeOffId }, "Staff.Billing", user);

    private static async Task<(BoundPolicy Policy, string AccountId, string InvoiceId)> BilledAsync(BillingSlice slice)
    {
        var policy = await slice.BindAsync();
        await slice.DrainAsync();
        var invoice = await slice.InvoiceOfAsync(policy.PolicyId);
        return (policy, invoice.Text("invoice.billingAccountId"), invoice.Text("invoice.invoiceId"));
    }

    private static Task<long> SetOverdueAsync(BillingSlice slice, string invoiceId) =>
        slice.ScalarAsync<long>($"WITH u AS (UPDATE bil.invoice SET state = 'OVERDUE' WHERE invoice_id = '{invoiceId}' RETURNING 1) SELECT count(*) FROM u");

    /// <summary>Net balance of a sub-ledger account on a billing account, debit positive.</summary>
    private static Task<decimal> NetAsync(BillingSlice slice, string account, string ledgerAccount) => slice.ScalarAsync<decimal>(
        $"""
        SELECT coalesce(sum(CASE WHEN l.side = 'DEBIT' THEN l.amount ELSE -l.amount END), 0)
        FROM bil.ledger_line l JOIN bil.ledger_entry e ON e.entry_id = l.entry_id
        WHERE l.billing_account_id = '{account}' AND l.account_code = '{ledgerAccount}'
        """);

    /// <summary>Net balance of a FIN general-ledger account on a billing account, debit positive.</summary>
    private static Task<decimal> FinNetAsync(BillingSlice slice, string account, string gl) => slice.ScalarAsync<decimal>(
        $"""
        SELECT coalesce(sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END), 0) FROM fin.journal_line
         WHERE billing_account_id = '{account}' AND account_code = '{gl}'
        """);

    /// <summary>The ledger invariants hold (INV-01..15, BO-BIL-11); this class owns its database, so INV-14 passes too.</summary>
    private static async Task InvariantsHoldAsync(BillingSlice slice)
    {
        var (response, body) = await slice.PostAsync("/api/bil/v1/reconciliation/invariants", new { });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["results"]!.AsArray().Count.ShouldBe(15);
        body["results"]!.AsArray().ShouldAllBe(x => x!.Text("status") == "PASS", body.ToJsonString());
        body.Text("status").ShouldBe("PASS");
    }

    /// <summary>Every BIL entry of the account was journalised by FIN (or is a memo FIN books nothing for), nothing suspended.</summary>
    private static async Task FinPostedAsync(BillingSlice slice, string account)
    {
        await slice.DrainAsync();
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM fin.business_event WHERE event_type = 'BillingEntryPosted' AND billing_account_id = '{account}' AND status NOT IN ('POSTED', 'NO_POSTING')")).ShouldBe(0);
    }

    [Fact]
    public async Task BO_BIL_13_a_manual_write_off_needs_a_second_person_and_posts_a_balanced_entry_that_FIN_journalises()
    {
        var (policy, account, invoiceId) = await BilledAsync(_slice);
        await SetOverdueAsync(_slice, invoiceId);
        var billed = await NetAsync(_slice, account, "LA-02");
        billed.ShouldBe(policy.Total);

        // Propose: waits for a decision, previews the entry (write-off clearing against the billed receivable).
        var (response, proposed) = await ProposeAsync(_slice, invoiceId, policy.Total);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, proposed?.ToJsonString());
        var writeOffId = proposed.Text("writeOff.writeOffId");
        proposed.Text("writeOff.state").ShouldBe("PROPOSED");
        proposed.Text("writeOff.kind").ShouldBe("MANUAL");
        proposed!["postingPreview"]!.AsArray().Select(l => $"{l!.Text("account")} {l.Text("side")}").ShouldBe(["LA-16 DEBIT", "LA-02 CREDIT"]);
        (await PostAsync(_slice, writeOffId)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // SoD: the proposer cannot decide; billing staff without the manager role cannot either.
        var self = await DecideAsync(_slice, writeOffId, "APPROVE", user: "alice");
        self.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, self.Body?.ToJsonString());
        self.Body.Text("code").ShouldBe("BIL-ERR-SOD");
        (await DecideAsync(_slice, writeOffId, "APPROVE", user: "dave", roles: "Staff.Billing")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ProposeAsync(_slice, invoiceId, 1m)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity); // pending proposals already cover the balance

        // A second person approves within their authority; only then can it be posted.
        var (approved, decided) = await DecideAsync(_slice, writeOffId, "APPROVE");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, decided?.ToJsonString());
        decided.Text("writeOff.state").ShouldBe("APPROVED");
        decided.Text("writeOff.decidedBy").ShouldContain("bob");
        (await DecideAsync(_slice, writeOffId, "REJECT")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var (posted, body) = await PostAsync(_slice, writeOffId);
        posted.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("writeOff.state").ShouldBe("POSTED");
        var entryId = body.Text("ledgerEntryId");
        (await PostAsync(_slice, writeOffId)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // The invoice is written off, nothing is open, its items follow.
        var invoice = await _slice.InvoiceOfAsync(policy.PolicyId);
        invoice.Text("invoice.state").ShouldBe("WRITTEN_OFF");
        Amount(invoice["invoice"]!["open"]).ShouldBe(0m);
        invoice["invoiceItems"]!.AsArray().ShouldAllBe(i => i!.Text("state") == "WRITTEN_OFF");

        // BIL: LA-02 is cleared into LA-16, one entry that balances; the lines carry the invoice item dimension.
        (await NetAsync(_slice, account, "LA-02")).ShouldBe(0m);
        (await NetAsync(_slice, account, "LA-16")).ShouldBe(policy.Total);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE entry_id = '{entryId}' AND entry_type = 'WRITTEN_OFF'")).ShouldBe(1);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_line WHERE entry_id = '{entryId}' AND invoice_item_id IS NOT NULL")).ShouldBe(policy.ChargeCount * 2);
        (await _slice.ScalarAsync<decimal>($"SELECT coalesce(sum(amount), 0) FROM bil.write_off_line WHERE write_off_id = '{writeOffId}'")).ShouldBe(policy.Total);

        // FIN: the same entry is journalised under rule set v8; the BIL and FIN totals are equal.
        await FinPostedAsync(_slice, account);
        (await FinNetAsync(_slice, account, "GL-1210")).ShouldBe(await NetAsync(_slice, account, "LA-02"));
        (await FinNetAsync(_slice, account, "GL-6210")).ShouldBe(await NetAsync(_slice, account, "LA-16"));
        (await FinNetAsync(_slice, account, "GL-6210")).ShouldBe(policy.Total);
        (await _slice.ScalarAsync<string>($"""
            SELECT string_agg(DISTINCT je.rule_set_version::text, ',') FROM fin.journal_entry je
             WHERE EXISTS (SELECT 1 FROM fin.journal_line jl WHERE jl.journal_id = je.journal_id AND jl.billing_account_id = '{account}' AND jl.account_code = 'GL-6210')
            """)).ShouldBe("8");

        // The inbox lists it; the ledger and the write-off rows stay sound.
        var (_, list) = await _slice.GetAsync($"/api/bil/v1/write-offs?state=POSTED&billingAccountId={account}");
        list!["items"]!.AsArray().Select(i => i!.Text("writeOffId")).ShouldBe([writeOffId]);
        await Should.ThrowAsync<Npgsql.PostgresException>(() => _slice.ScalarAsync<long>($"WITH d AS (DELETE FROM bil.write_off_line WHERE write_off_id = '{writeOffId}' RETURNING 1) SELECT count(*) FROM d"));
        await InvariantsHoldAsync(_slice);
    }

    [Fact]
    public async Task BO_BIL_13_a_rejected_write_off_changes_nothing_and_a_partial_one_leaves_the_rest_open_for_cash()
    {
        var (policy, account, invoiceId) = await BilledAsync(_slice);
        await SetOverdueAsync(_slice, invoiceId);

        // Rejected: nothing is posted, the invoice is as it was.
        var (_, rejectedProposal) = await ProposeAsync(_slice, invoiceId, policy.Total);
        var rejectedId = rejectedProposal.Text("writeOff.writeOffId");
        var (rejected, rejectedBody) = await DecideAsync(_slice, rejectedId, "REJECT");
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, rejectedBody?.ToJsonString());
        rejectedBody.Text("writeOff.state").ShouldBe("REJECTED");
        (await PostAsync(_slice, rejectedId)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _slice.InvoiceOfAsync(policy.PolicyId)).Text("invoice.state").ShouldBe("OVERDUE");
        (await NetAsync(_slice, account, "LA-16")).ShouldBe(0m);

        // A partial write-off of 10: the invoice stays Overdue with the rest open, and more than the rest is refused.
        var (_, partialProposal) = await ProposeAsync(_slice, invoiceId, 10m);
        var partialId = partialProposal.Text("writeOff.writeOffId");
        (await DecideAsync(_slice, partialId, "APPROVE")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(_slice, partialId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var invoice = await _slice.InvoiceOfAsync(policy.PolicyId);
        invoice.Text("invoice.state").ShouldBe("OVERDUE");
        Amount(invoice["invoice"]!["open"]).ShouldBe(policy.Total - 10m);
        var tooMuch = await ProposeAsync(_slice, invoiceId, policy.Total);
        tooMuch.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, tooMuch.Body?.ToJsonString());
        tooMuch.Body.Text("code").ShouldBe("BIL-ERR-AMOUNT-MISMATCH");
        (await NetAsync(_slice, account, "LA-02")).ShouldBe(policy.Total - 10m);

        // The cash of the rest pays the invoice: the open amount the allocator sees is net of the write-off.
        var (taken, payment) = await _slice.PostAsync("/api/bil/v1/payments/take",
            new { billingAccountId = account, amount = Money(policy.Total - 10m), method = "BANK_TRANSFER", bankReference = Guid.NewGuid().ToString("N"), invoiceId });
        taken.StatusCode.ShouldBe(HttpStatusCode.Created, payment?.ToJsonString());
        payment.Text("allocationOutcome").ShouldBe("ALLOCATED");
        (await _slice.InvoiceOfAsync(policy.PolicyId)).Text("invoice.state").ShouldBe("PAID");
        (await NetAsync(_slice, account, "LA-02")).ShouldBe(0m);

        await FinPostedAsync(_slice, account);
        (await FinNetAsync(_slice, account, "GL-1210")).ShouldBe(await NetAsync(_slice, account, "LA-02"));
        (await FinNetAsync(_slice, account, "GL-6210")).ShouldBe(10m);
        await InvariantsHoldAsync(_slice);
    }

    [Fact]
    public async Task BO_BIL_13_a_residual_within_the_tolerance_is_written_off_at_once_without_a_second_person()
    {
        var (policy, account, invoiceId) = await BilledAsync(_slice);

        // A payment 0.50 short leaves a residual of 0.50 on a partly paid invoice.
        var (taken, payment) = await _slice.PostAsync("/api/bil/v1/payments/take",
            new { billingAccountId = account, amount = Money(policy.Total - 0.50m), method = "BANK_TRANSFER", bankReference = Guid.NewGuid().ToString("N") });
        taken.StatusCode.ShouldBe(HttpStatusCode.Created, payment?.ToJsonString());
        var (allocated, allocation) = await _slice.PostAsync("/api/bil/v1/allocations/allocate",
            new { receiptId = payment.Text("receipt.receiptId"), lines = new[] { new { invoiceId, amount = Money(policy.Total - 0.50m) } } });
        allocated.StatusCode.ShouldBe(HttpStatusCode.OK, allocation?.ToJsonString());
        var partlyPaid = await _slice.InvoiceOfAsync(policy.PolicyId);
        partlyPaid.Text("invoice.state").ShouldBe("PARTIALLY_PAID");
        Amount(partlyPaid["invoice"]!["open"]).ShouldBe(0.50m);

        // Above the tolerance (or short of the whole balance) a not-overdue invoice is not written off.
        (await ProposeAsync(_slice, invoiceId, 0.20m)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var (response, body) = await ProposeAsync(_slice, invoiceId, 0.50m);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("writeOff.kind").ShouldBe("TOLERANCE");
        body.Text("writeOff.state").ShouldBe("POSTED");
        body.Text("writeOff.decidedBy").ShouldBe("system:tolerance");
        body!["postingPreview"]!.AsArray().Select(l => $"{l!.Text("account")} {l.Text("side")}").ShouldBe(["LA-19 DEBIT", "LA-02 CREDIT"]);
        (await _slice.InvoiceOfAsync(policy.PolicyId)).Text("invoice.state").ShouldBe("PAID");
        (await NetAsync(_slice, account, "LA-19")).ShouldBe(0.50m);
        (await NetAsync(_slice, account, "LA-02")).ShouldBe(0m);

        await FinPostedAsync(_slice, account);
        (await FinNetAsync(_slice, account, "GL-6220")).ShouldBe(0.50m);
        (await FinNetAsync(_slice, account, "GL-1210")).ShouldBe(await NetAsync(_slice, account, "LA-02"));
        await InvariantsHoldAsync(_slice);
    }

    [Fact]
    public async Task BO_BIL_13_a_write_off_is_refused_for_a_not_overdue_invoice_a_bad_request_and_a_closed_period()
    {
        var (policy, account, invoiceId) = await BilledAsync(_slice);

        // Due, not overdue, above the tolerance.
        var due = await ProposeAsync(_slice, invoiceId, policy.Total);
        due.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, due.Body?.ToJsonString());
        due.Body.Text("code").ShouldBe("BIL-ERR-INVOICE-STATE");

        // Bad input and unknown objects.
        (await AsAsync(_slice, "/api/bil/v1/write-offs/propose", new { invoiceId, amount = Money(policy.Total), reasonCode = "not a code" }, "Staff.Billing", "alice")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AsAsync(_slice, "/api/bil/v1/write-offs/propose", new { invoiceId, amount = Money(-5m), reasonCode = "X" }, "Staff.Billing", "alice")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ProposeAsync(_slice, Guid.CreateVersion7().ToString(), 5m)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await DecideAsync(_slice, Guid.CreateVersion7().ToString(), "APPROVE")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AsAsync(_slice, "/api/bil/v1/write-offs/propose", new { invoiceId, amount = Money(5m), reasonCode = "X" }, Underwriter, "alice")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _slice.GetAsync("/api/bil/v1/write-offs?state=NOPE")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // A closed FIN period refuses the posting; the approved write-off stays approved.
        await using var locked = new BillingSlice(database, services =>
        {
            services.RemoveAll<IFinancePeriodService>();
            services.AddScoped<IFinancePeriodService, ClosedPeriods>();
        });
        await locked.SeedAsync();
        var (lockedPolicy, lockedAccount, lockedInvoice) = await BilledAsync(locked);
        await SetOverdueAsync(locked, lockedInvoice);
        var (_, proposed) = await ProposeAsync(locked, lockedInvoice, lockedPolicy.Total);
        var writeOffId = proposed.Text("writeOff.writeOffId");
        (await DecideAsync(locked, writeOffId, "APPROVE")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (response, body) = await PostAsync(locked, writeOffId);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body?.ToJsonString());
        body.Text("code").ShouldBe("BIL-ERR-PERIOD-LOCKED");
        (await locked.ScalarAsync<string>($"SELECT state FROM bil.write_off WHERE write_off_id = '{writeOffId}'")).ShouldBe("APPROVED");
        (await NetAsync(locked, lockedAccount, "LA-16")).ShouldBe(0m);
        account.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task BO_BIL_13_two_sessions_posting_the_same_write_off_book_it_once()
    {
        var (policy, account, invoiceId) = await BilledAsync(_slice);
        await SetOverdueAsync(_slice, invoiceId);
        var (_, proposed) = await ProposeAsync(_slice, invoiceId, policy.Total);
        var writeOffId = proposed.Text("writeOff.writeOffId");
        (await DecideAsync(_slice, writeOffId, "APPROVE")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var results = await Task.WhenAll(PostAsync(_slice, writeOffId, "c1"), PostAsync(_slice, writeOffId, "c2"), PostAsync(_slice, writeOffId, "c3"));
        results.Count(r => r.Response.StatusCode == HttpStatusCode.OK).ShouldBe(1, string.Join(" | ", results.Select(r => r.Body?.ToJsonString())));
        results.Where(r => r.Response.StatusCode != HttpStatusCode.OK).All(r => r.Response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity).ShouldBeTrue();
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE entry_type = 'WRITTEN_OFF' AND billing_account_id = '{account}'")).ShouldBe(1);
        (await NetAsync(_slice, account, "LA-16")).ShouldBe(policy.Total);
        await InvariantsHoldAsync(_slice);
    }

    /// <summary>A second billing account of the same payer (the busy one is the payer's only PREMIUM account).</summary>
    private async Task<string> SecondAccountAsync(string template)
    {
        var id = Guid.CreateVersion7();
        await _slice.ScalarAsync<long>($"""
            WITH ins AS (
              INSERT INTO bil.billing_account (billing_account_id, legal_entity_id, jurisdiction, account_number, account_type, payer_party_id, currency, status, created_at, created_by, record_version)
              SELECT '{id}', legal_entity_id, jurisdiction, 'BA-T' || substr(replace('{id}', '-', ''), 1, 12), 'SECONDARY', payer_party_id, currency, 'ACTIVE', now(), 'test', 1
              FROM bil.billing_account WHERE billing_account_id = '{template}'
              RETURNING 1)
            SELECT count(*) FROM ins
            """);
        return id.ToString();
    }

    [Fact]
    public async Task BO_BIL_13_money_moves_between_accounts_of_the_same_payer_only_and_the_ledger_pair_balances()
    {
        var (policy, accountA, _) = await BilledAsync(_slice);
        var accountB = await SecondAccountAsync(accountA);
        var (otherPolicy, otherAccount, _) = await BilledAsync(_slice);

        // Unapplied cash on account A (short of the open amount: it waits in suspense).
        var (taken, payment) = await _slice.PostAsync("/api/bil/v1/payments/take",
            new { billingAccountId = accountA, amount = Money(policy.Total - 10m), method = "BANK_TRANSFER", bankReference = Guid.NewGuid().ToString("N") });
        taken.StatusCode.ShouldBe(HttpStatusCode.Created, payment?.ToJsonString());
        payment.Text("allocationOutcome").ShouldBe("SUSPENSE");
        var receiptId = payment.Text("receipt.receiptId");
        var amount = policy.Total - 10m;
        (await NetAsync(_slice, accountA, "LA-11")).ShouldBe(-amount);

        // Another payer, the same account, a missing receipt, an unknown account and a missing role are refused; nothing moves.
        var other = await AsAsync(_slice, "/api/bil/v1/transfers/money", new { sourceBillingAccountId = accountA, targetBillingAccountId = otherAccount, receiptIds = new[] { receiptId } }, "Staff.Billing", "alice");
        other.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, other.Body?.ToJsonString());
        other.Body.Text("code").ShouldBe("BIL-ERR-PAYER-MISMATCH");
        (await AsAsync(_slice, "/api/bil/v1/transfers/money", new { sourceBillingAccountId = accountA, targetBillingAccountId = accountA, receiptIds = new[] { receiptId } }, "Staff.Billing", "alice")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AsAsync(_slice, "/api/bil/v1/transfers/money", new { sourceBillingAccountId = accountA, targetBillingAccountId = accountB, receiptIds = new[] { Guid.CreateVersion7() } }, "Staff.Billing", "alice")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AsAsync(_slice, "/api/bil/v1/transfers/money", new { sourceBillingAccountId = accountA, targetBillingAccountId = Guid.CreateVersion7(), receiptIds = new[] { receiptId } }, "Staff.Billing", "alice")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AsAsync(_slice, "/api/bil/v1/transfers/money", new { sourceBillingAccountId = accountA, targetBillingAccountId = accountB, receiptIds = new[] { receiptId } }, Underwriter, "alice")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await NetAsync(_slice, accountA, "LA-11")).ShouldBe(-amount);

        // The transfer: one entry, the clearing nets to zero, the cash is account B's unapplied cash now.
        var key = Guid.NewGuid();
        var request = new { sourceBillingAccountId = accountA, targetBillingAccountId = accountB, receiptIds = new[] { receiptId }, comment = "moved to the payer's second account" };
        var (response, body) = await AsAsync(_slice, "/api/bil/v1/transfers/money", request, "Staff.Billing", "alice", key);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        Amount(body!["total"]).ShouldBe(amount);
        body["transfers"]!.AsArray().Select(t => t!.Text("receiptId")).ShouldBe([receiptId]);
        var entryId = body.Text("ledgerEntryId");
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_line WHERE entry_id = '{entryId}'")).ShouldBe(4);
        (await NetAsync(_slice, accountA, "LA-11")).ShouldBe(0m);
        (await NetAsync(_slice, accountB, "LA-11")).ShouldBe(-amount);
        (await _slice.ScalarAsync<decimal>($"SELECT coalesce(sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END), 0) FROM bil.ledger_line WHERE entry_id = '{entryId}' AND account_code = 'LA-20'")).ShouldBe(0m);
        (await _slice.ScalarAsync<string>($"SELECT billing_account_id::text FROM bil.receipt WHERE receipt_id = '{receiptId}'")).ShouldBe(accountB);
        var (_, suspense) = await _slice.GetAsync($"/api/bil/v1/suspense?billingAccountId={accountB}");
        suspense!["items"]!.AsArray().Single(i => i!.Text("receiptId") == receiptId)!.Text("reason").ShouldBe("TRANSFERRED");

        // The same key replays; a new key finds the cash gone from the source.
        var (replay, replayBody) = await AsAsync(_slice, "/api/bil/v1/transfers/money", request, "Staff.Billing", "alice", key);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK, replayBody?.ToJsonString());
        replayBody.Text("ledgerEntryId").ShouldBe(entryId);
        var again = await AsAsync(_slice, "/api/bil/v1/transfers/money", request, "Staff.Billing", "alice");
        again.Response.StatusCode.ShouldBe(HttpStatusCode.Conflict, again.Body?.ToJsonString());
        again.Body.Text("code").ShouldBe("BIL-ERR-SUSPENSE-STATE");
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE entry_type = 'MONEY_TRANSFERRED' AND billing_account_id = '{accountA}'")).ShouldBe(1);

        // FIN books nothing for it (memo): the GL accounts of unallocated receipts do not change; every BIL entry is still journalised.
        await FinPostedAsync(_slice, accountA);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM fin.business_event WHERE billing_account_id = '{accountA}' AND payload->>'eventType' = 'MONEY_TRANSFERRED' AND status = 'NO_POSTING'")).ShouldBe(1);
        (await FinNetAsync(_slice, accountA, "GL-2540") + await FinNetAsync(_slice, accountB, "GL-2540"))
            .ShouldBe(await NetAsync(_slice, accountA, "LA-11") + await NetAsync(_slice, accountB, "LA-11"));
        otherPolicy.PolicyId.ShouldNotBeNullOrEmpty();
        await InvariantsHoldAsync(_slice);
    }

    /// <summary>FIN with every period closed for posting.</summary>
    private sealed class ClosedPeriods : IFinancePeriodService
    {
        public Task<PeriodStatusResponse> StatusAsync(string legalEntity, ValidAt? validAt = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PeriodStatusResponse { LegalEntity = legalEntity, Period = "2026-11", State = "LOCKED", ClosedForPosting = true });
    }
}
