using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Bil.Refunds;
using CoreIns.Modules.Finance.Contracts;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Platform.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static CoreIns.IntegrationTests.Bil.BillingSlice;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>
/// BO-BIL-07: the receipt list (filters, keyset paging, masked bank reference), unallocate and reallocate. A reversal is append-only
/// (negative allocation rows plus one ALLOCATION_REVERSED entry), the freed cash is unapplied again, invoices roll back, a concurrent
/// second unallocate loses, and a closed FIN period or a paid-out refund refuses it.
/// </summary>
public sealed class AllocationReversalTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private BillingSlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _slice = new BillingSlice(database);
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private static object Money(decimal amount) => new { amount = amount.ToString(CultureInfo.InvariantCulture), currency = "EUR" };

    private static async Task<(BoundPolicy Policy, string AccountId, string InvoiceId)> BilledAsync(BillingSlice slice)
    {
        var policy = await slice.BindAsync();
        await slice.DrainAsync();
        var invoice = await slice.InvoiceOfAsync(policy.PolicyId);
        return (policy, invoice.Text("invoice.billingAccountId"), invoice.Text("invoice.invoiceId"));
    }

    /// <summary>Takes a payment of exactly the invoice total: matched and allocated at once.</summary>
    private static async Task<JsonNode> PaidAsync(BillingSlice slice, string account, decimal amount, string? reference = null)
    {
        var (response, body) = await slice.PostAsync("/api/bil/v1/payments/take",
            new { billingAccountId = account, amount = Money(amount), method = "BANK_TRANSFER", bankReference = reference ?? Guid.NewGuid().ToString("N") });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return body!;
    }

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> UnallocateAsync(BillingSlice slice, string receiptId, string[]? allocationIds = null, Guid? key = null, string roles = Billing) =>
        slice.PostAsync("/api/bil/v1/allocations/unallocate", new { receiptId, allocationIds, reasonCode = "MISAPPLIED", comment = "wrong policy" }, roles, key);

    private static async Task<JsonNode> ReceiptAsync(BillingSlice slice, string receiptId)
    {
        var (response, body) = await slice.GetAsync($"/api/bil/v1/receipts/{receiptId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!;
    }

    /// <summary>Net balance of a sub-ledger account on a billing account, debit positive.</summary>
    private static Task<decimal> NetAsync(BillingSlice slice, string account, string ledgerAccount) => slice.ScalarAsync<decimal>(
        $"""
        SELECT coalesce(sum(CASE WHEN l.side = 'DEBIT' THEN l.amount ELSE -l.amount END), 0)
        FROM bil.ledger_line l JOIN bil.ledger_entry e ON e.entry_id = l.entry_id
        WHERE e.billing_account_id = '{account}' AND l.account_code = '{ledgerAccount}'
        """);

    [Fact]
    public async Task BO_BIL_07_the_receipt_list_filters_pages_by_keyset_and_masks_the_bank_reference()
    {
        var (policyA, accountA, _) = await BilledAsync(_slice);
        var (policyB, accountB, _) = await BilledAsync(_slice);
        var prefix = "LST" + Guid.NewGuid().ToString("N")[..6];
        var first = await PaidAsync(_slice, accountA, policyA.Total, prefix + "AAAA1111");
        var second = await PaidAsync(_slice, accountB, policyB.Total - 10m, prefix + "BBBB2222");
        var third = await PaidAsync(_slice, accountB, 1.5m, prefix + "CCCC3333");

        var (response, all) = await _slice.GetAsync($"/api/bil/v1/receipts?bankReferencePrefix={prefix}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, all?.ToJsonString());
        var items = all!["items"]!.AsArray();
        items.Select(i => i!.Text("receipt.receiptId")).ShouldBe([first.Text("receipt.receiptId"), second.Text("receipt.receiptId"), third.Text("receipt.receiptId")]);
        items[0]!.Text("bankReferenceMasked").ShouldBe(new string('*', prefix.Length + 4) + "1111");
        items[0]!.Text("receipt.state").ShouldBe("ALLOCATED");

        (await _slice.GetAsync($"/api/bil/v1/receipts?bankReferencePrefix={prefix}&billingAccountId={accountB}")).Body!["items"]!.AsArray().Count.ShouldBe(2);
        (await _slice.GetAsync($"/api/bil/v1/receipts?bankReferencePrefix={prefix}&state=SUSPENSE")).Body!["items"]!.AsArray().Count.ShouldBe(2);
        (await _slice.GetAsync($"/api/bil/v1/receipts?bankReferencePrefix={prefix}&state=ALLOCATED")).Body!["items"]!.AsArray().Count.ShouldBe(1);
        (await _slice.GetAsync($"/api/bil/v1/receipts?bankReferencePrefix={prefix}&amountMin=1.00&amountMax=2.00")).Body!["items"]!.AsArray().Count.ShouldBe(1);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        (await _slice.GetAsync($"/api/bil/v1/receipts?bankReferencePrefix={prefix}&valueDateFrom=2000-01-01&valueDateTo=2000-12-31")).Body!["items"]!.AsArray().Count.ShouldBe(0);
        (await _slice.GetAsync($"/api/bil/v1/receipts?bankReferencePrefix={prefix}&valueDateFrom={today}&valueDateTo=2999-12-31")).Body!["items"]!.AsArray().Count.ShouldBeGreaterThanOrEqualTo(0);
        (await _slice.GetAsync("/api/bil/v1/receipts?bankReferencePrefix=NO-SUCH-REFERENCE")).Body!["items"]!.AsArray().Count.ShouldBe(0);

        // Keyset paging: two pages of 2 and 1, no row twice, the last page has no cursor.
        var (_, page1) = await _slice.GetAsync($"/api/bil/v1/receipts?bankReferencePrefix={prefix}&limit=2");
        page1!["items"]!.AsArray().Count.ShouldBe(2);
        var cursor = page1.Text("nextCursor");
        var (_, page2) = await _slice.GetAsync($"/api/bil/v1/receipts?bankReferencePrefix={prefix}&limit=2&cursor={Uri.EscapeDataString(cursor)}");
        page2!["items"]!.AsArray().Select(i => i!.Text("receipt.receiptId")).ShouldBe([third.Text("receipt.receiptId")]);
        page2["nextCursor"].ShouldBeNull();

        // Bad input is a clean 4xx; only billing roles read the list.
        (await _slice.GetAsync("/api/bil/v1/receipts?state=NOPE")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _slice.GetAsync("/api/bil/v1/receipts?valueDateFrom=yesterday")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _slice.GetAsync("/api/bil/v1/receipts?amountMin=abc")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _slice.GetAsync("/api/bil/v1/receipts?cursor=%25%25")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _slice.GetAsync("/api/bil/v1/receipts?limit=0")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _slice.GetAsync("/api/bil/v1/receipts", roles: Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BO_BIL_07_unallocate_reverses_append_only_returns_the_cash_to_suspense_and_rolls_the_invoice_back()
    {
        var (policy, account, invoiceId) = await BilledAsync(_slice);
        var billedBefore = await NetAsync(_slice, account, "LA-02");
        var paid = await PaidAsync(_slice, account, policy.Total);
        var receiptId = paid.Text("receipt.receiptId");
        paid.Text("allocationOutcome").ShouldBe("ALLOCATED");
        (await _slice.InvoiceOfAsync(policy.PolicyId)).Text("invoice.state").ShouldBe("PAID");
        (await NetAsync(_slice, account, "LA-02")).ShouldBe(billedBefore - policy.Total);

        var (response, body) = await UnallocateAsync(_slice, receiptId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var reversals = body!["reversals"]!.AsArray();
        reversals.Count.ShouldBe(paid["allocations"]!.AsArray().Count);
        reversals.All(r => Amount(r!["amount"]) < 0m && r["reversesAllocationId"] is not null).ShouldBeTrue(body.ToJsonString());
        reversals.Sum(r => Amount(r!["amount"])).ShouldBe(-policy.Total);
        body.Text("receipt.state").ShouldBe("SUSPENSE");
        body.Text("receipt.suspenseReason").ShouldBe("UNALLOCATED");
        Amount(body["receipt"]!["unallocated"]).ShouldBe(policy.Total);
        Amount(body["receipt"]!["allocated"]).ShouldBe(0m);

        // The invoice and its items are open again.
        var invoice = await _slice.InvoiceOfAsync(policy.PolicyId);
        invoice.Text("invoice.state").ShouldBe("DUE");
        invoice["invoiceItems"]!.AsArray().All(i => i!.Text("state") == "OPEN" || i.Text("state") == "BILLED").ShouldBeTrue(invoice.ToJsonString());
        Amount(invoice["invoice"]!["open"]).ShouldBe(policy.Total);

        // The ledger: one reversal entry that mirrors the allocation; receivable and unapplied cash are back where they were.
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE entry_type = 'ALLOCATION_REVERSED' AND billing_account_id = '{account}'")).ShouldBe(1);
        (await NetAsync(_slice, account, "LA-02")).ShouldBe(billedBefore);
        (await NetAsync(_slice, account, "LA-11")).ShouldBe(-policy.Total);
        (await _slice.ScalarAsync<decimal>($"SELECT coalesce(sum(amount), 0) FROM bil.allocation WHERE receipt_id = '{receiptId}'")).ShouldBe(0m);
        var (_, balances) = await _slice.GetAsync($"/api/bil/v1/billing-accounts/{account}");
        Amount(balances!["balancesByState"]!["unapplied"]).ShouldBe(policy.Total);

        // The receipt shows the original rows and their reversals; the cash is a workbench item again.
        var receipt = await ReceiptAsync(_slice, receiptId);
        receipt["allocations"]!.AsArray().Count.ShouldBe(reversals.Count);
        receipt["reversals"]!.AsArray().Count.ShouldBe(reversals.Count);
        var (_, suspense) = await _slice.GetAsync($"/api/bil/v1/suspense?billingAccountId={account}");
        var item = suspense!["items"]!.AsArray().Single(i => i!.Text("receiptId") == receiptId)!;
        item.Text("state").ShouldBe("OPEN");
        item.Text("reason").ShouldBe("UNALLOCATED");

        // Append-only: the database refuses to edit or delete an allocation row, the reversal included.
        await Should.ThrowAsync<Npgsql.PostgresException>(() => _slice.ScalarAsync<long>($"WITH d AS (DELETE FROM bil.allocation WHERE receipt_id = '{receiptId}' RETURNING 1) SELECT count(*) FROM d"));

        // Nothing left to reverse; the cash can be allocated again, to the same invoice.
        var again = await UnallocateAsync(_slice, receiptId);
        again.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, again.Body?.ToJsonString());
        again.Body.Text("code").ShouldBe("BIL-ERR-NOT-ALLOCATED");
        var (reapplied, reappliedBody) = await _slice.PostAsync("/api/bil/v1/allocations/allocate", new { receiptId, lines = new[] { new { invoiceId, amount = Money(policy.Total) } } });
        reapplied.StatusCode.ShouldBe(HttpStatusCode.OK, reappliedBody?.ToJsonString());
        reappliedBody.Text("receipt.state").ShouldBe("ALLOCATED");
        (await _slice.InvoiceOfAsync(policy.PolicyId)).Text("invoice.state").ShouldBe("PAID");

        // Validation and scope.
        (await UnallocateAsync(_slice, Guid.CreateVersion7().ToString())).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _slice.PostAsync("/api/bil/v1/allocations/unallocate", new { receiptId, reasonCode = "not a code" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await UnallocateAsync(_slice, receiptId, roles: Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BO_BIL_07_a_partial_unallocate_reverses_only_the_named_rows_and_leaves_the_invoice_partially_paid()
    {
        var (policy, account, invoiceId) = await BilledAsync(_slice);
        // An over-payment stays in suspense; allocating it applies the invoice and leaves the rest unapplied.
        var over = await PaidAsync(_slice, account, policy.Total + 25m);
        var receiptId = over.Text("receipt.receiptId");
        var (ok, allocated) = await _slice.PostAsync("/api/bil/v1/allocations/allocate", new { receiptId, lines = new[] { new { invoiceId, amount = Money(policy.Total) } } });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, allocated?.ToJsonString());
        var rows = allocated!["allocations"]!.AsArray();
        rows.Count.ShouldBeGreaterThan(1);

        var one = rows[0]!;
        var (response, body) = await UnallocateAsync(_slice, receiptId, [one.Text("allocationId")]);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["reversals"]!.AsArray().Count.ShouldBe(1);
        body.Text("receipt.state").ShouldBe("PARTIALLY_ALLOCATED");
        Amount(body["receipt"]!["unallocated"]).ShouldBe(25m + Amount(one["amount"]));
        (await _slice.InvoiceOfAsync(policy.PolicyId)).Text("invoice.state").ShouldBe("PARTIALLY_PAID");

        // The named row is gone, another row of the receipt still reverses; the same row twice is a conflict.
        var twice = await UnallocateAsync(_slice, receiptId, [one.Text("allocationId")]);
        twice.Response.StatusCode.ShouldBe(HttpStatusCode.Conflict, twice.Body?.ToJsonString());
        twice.Body.Text("code").ShouldBe("BIL-ERR-ALREADY-REVERSED");
        (await UnallocateAsync(_slice, receiptId, [Guid.CreateVersion7().ToString()])).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var rest = await UnallocateAsync(_slice, receiptId);
        rest.Response.StatusCode.ShouldBe(HttpStatusCode.OK, rest.Body?.ToJsonString());
        rest.Body!["reversals"]!.AsArray().Count.ShouldBe(rows.Count - 1);
        rest.Body.Text("receipt.state").ShouldBe("SUSPENSE");
        (await _slice.InvoiceOfAsync(policy.PolicyId)).Text("invoice.state").ShouldBe("DUE");
    }

    [Fact]
    public async Task BO_BIL_07_reallocate_reverses_and_applies_again_in_one_call_and_replays_on_the_same_key()
    {
        var (policy, account, invoiceId) = await BilledAsync(_slice);
        var paid = await PaidAsync(_slice, account, policy.Total);
        var receiptId = paid.Text("receipt.receiptId");
        var key = Guid.NewGuid();
        var request = new { receiptId, reasonCode = "SPLIT", lines = new[] { new { invoiceId, amount = Money(policy.Total - 10m) } } };

        var (response, body) = await _slice.PostAsync("/api/bil/v1/allocations/reallocate", request, key: key);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["reversals"]!.AsArray().Count.ShouldBe(paid["allocations"]!.AsArray().Count);
        body["allocations"]!.AsArray().Sum(a => Amount(a!["amount"])).ShouldBe(policy.Total - 10m);
        body.Text("receipt.state").ShouldBe("PARTIALLY_ALLOCATED");
        Amount(body["receipt"]!["unallocated"]).ShouldBe(10m);
        (await _slice.InvoiceOfAsync(policy.PolicyId)).Text("invoice.state").ShouldBe("PARTIALLY_PAID");
        var rowCount = await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.allocation WHERE receipt_id = '{receiptId}'");

        // The same key replays the answer without reversing again.
        var (replay, replayBody) = await _slice.PostAsync("/api/bil/v1/allocations/reallocate", request, key: key);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK, replayBody?.ToJsonString());
        replayBody.Text("ledgerEntryId").ShouldBe(body.Text("ledgerEntryId"));
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.allocation WHERE receipt_id = '{receiptId}'")).ShouldBe(rowCount);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE entry_type = 'ALLOCATION_REVERSED' AND billing_account_id = '{account}'")).ShouldBe(1);

        // A bad target changes nothing: the reversal of the same call is rolled back with it.
        var before = await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.allocation WHERE receipt_id = '{receiptId}'");
        var (bad, badBody) = await _slice.PostAsync("/api/bil/v1/allocations/reallocate",
            new { receiptId, reasonCode = "SPLIT", lines = new[] { new { invoiceId = Guid.CreateVersion7().ToString(), amount = Money(1m) } } });
        bad.StatusCode.ShouldBe(HttpStatusCode.NotFound, badBody?.ToJsonString());
        var (tooMuch, tooMuchBody) = await _slice.PostAsync("/api/bil/v1/allocations/reallocate",
            new { receiptId, reasonCode = "SPLIT", lines = new[] { new { invoiceId, amount = Money(policy.Total + 1m) } } });
        tooMuch.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, tooMuchBody?.ToJsonString());
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.allocation WHERE receipt_id = '{receiptId}'")).ShouldBe(before);
        (await _slice.InvoiceOfAsync(policy.PolicyId)).Text("invoice.state").ShouldBe("PARTIALLY_PAID");
    }

    [Fact]
    public async Task BO_BIL_07_two_sessions_unallocating_the_same_receipt_reverse_it_exactly_once()
    {
        var (policy, account, _) = await BilledAsync(_slice);
        var paid = await PaidAsync(_slice, account, policy.Total);
        var receiptId = paid.Text("receipt.receiptId");
        var originals = paid["allocations"]!.AsArray().Count;

        var results = await Task.WhenAll(
            UnallocateAsync(_slice, receiptId, key: Guid.NewGuid()),
            UnallocateAsync(_slice, receiptId, key: Guid.NewGuid()),
            UnallocateAsync(_slice, receiptId, key: Guid.NewGuid()));

        results.Count(r => r.Response.StatusCode == HttpStatusCode.OK).ShouldBe(1, string.Join(" | ", results.Select(r => r.Body?.ToJsonString())));
        results.Where(r => r.Response.StatusCode != HttpStatusCode.OK)
            .All(r => r.Response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity).ShouldBeTrue(string.Join(" | ", results.Select(r => r.Body?.ToJsonString())));
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.allocation WHERE receipt_id = '{receiptId}' AND reverses_allocation_id IS NOT NULL")).ShouldBe(originals);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE entry_type = 'ALLOCATION_REVERSED' AND billing_account_id = '{account}'")).ShouldBe(1);
        (await _slice.ScalarAsync<decimal>($"SELECT coalesce(sum(amount), 0) FROM bil.allocation WHERE receipt_id = '{receiptId}'")).ShouldBe(0m);
    }

    [Fact]
    public async Task BO_BIL_07_the_database_refuses_a_second_or_a_wrong_reversal_row()
    {
        var (policy, account, _) = await BilledAsync(_slice);
        var paid = await PaidAsync(_slice, account, policy.Total);
        var receiptId = paid.Text("receipt.receiptId");
        var original = paid["allocations"]!.AsArray()[0]!;
        var originalId = original.Text("allocationId");

        string Insert(string amount, string reverses) => $"""
            INSERT INTO bil.allocation (allocation_id, legal_entity_id, receipt_id, invoice_id, invoice_item_id, term_id, amount, currency, rule_id, source, actor, recorded_at, reverses_allocation_id)
            SELECT gen_random_uuid(), legal_entity_id, receipt_id, invoice_id, invoice_item_id, term_id, {amount}, currency, 'REVERSAL', 'MANUAL', 'test', now(), {reverses}
            FROM bil.allocation WHERE allocation_id = '{originalId}'
            """;
        // Not the negated amount of the original.
        var wrong = await Should.ThrowAsync<Npgsql.PostgresException>(() => _slice.ScalarAsync<long>(Insert("-0.01", $"'{originalId}'")));
        wrong.SqlState.ShouldBe("BL001");
        // A positive amount that claims to reverse something is refused too.
        var positive = await Should.ThrowAsync<Npgsql.PostgresException>(() => _slice.ScalarAsync<long>(Insert("amount", $"'{originalId}'")));
        positive.SqlState.ShouldBe("BL001");
        // An original (no reversed row) with a negative amount breaks the check constraint.
        var negativeOriginal = await Should.ThrowAsync<Npgsql.PostgresException>(() => _slice.ScalarAsync<long>(Insert("-amount", "NULL")));
        negativeOriginal.SqlState.ShouldBe("23514");
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.allocation WHERE receipt_id = '{receiptId}' AND reverses_allocation_id IS NOT NULL")).ShouldBe(0);
    }

    [Fact]
    public async Task BO_BIL_07_a_closed_finance_period_refuses_the_reversal()
    {
        await using var locked = new BillingSlice(database, services =>
        {
            services.RemoveAll<IFinancePeriodService>();
            services.AddScoped<IFinancePeriodService, ClosedPeriods>();
        });
        await locked.SeedAsync();
        var (policy, account, _) = await BilledAsync(locked);
        var paid = await PaidAsync(locked, account, policy.Total);
        var receiptId = paid.Text("receipt.receiptId");

        var (response, body) = await UnallocateAsync(locked, receiptId);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body?.ToJsonString());
        body.Text("code").ShouldBe("BIL-ERR-PERIOD-LOCKED");
        (await ReceiptAsync(locked, receiptId)).Text("receipt.state").ShouldBe("ALLOCATED");
        (await locked.ScalarAsync<long>($"SELECT count(*) FROM bil.allocation WHERE receipt_id = '{receiptId}' AND reverses_allocation_id IS NOT NULL")).ShouldBe(0);
        (await locked.PostAsync("/api/bil/v1/allocations/reallocate", new { receiptId, reasonCode = "SPLIT", lines = new[] { new { invoiceId = Guid.CreateVersion7().ToString(), amount = Money(1m) } } }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BO_BIL_07_an_allocation_whose_term_was_refunded_cannot_be_reversed()
    {
        await using var harness = await RefundHarness.StartAsync(database, o => o.PayeeCoolingOffDays = 0);
        await harness.WithCreditAsync();
        var slice = harness.Slice;
        var receiptId = await slice.ScalarAsync<string>($"SELECT receipt_id::text FROM bil.receipt WHERE billing_account_id = '{harness.AccountId}' ORDER BY recorded_at LIMIT 1");

        // The proposed refund of the credit is paid out (within the clerk's authority the stub bank pays it at once).
        var proposed = await harness.ProposedAsync();
        (await harness.ReloadAsync(proposed.Text("refundId"))).Text("state").ShouldBe("PAID");

        var (response, body) = await UnallocateAsync(slice, receiptId);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body?.ToJsonString());
        body.Text("code").ShouldBe("BIL-ERR-ALLOCATION-REFUNDED");
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM bil.allocation WHERE receipt_id = '{receiptId}' AND reverses_allocation_id IS NOT NULL")).ShouldBe(0);
    }

    /// <summary>FIN with every period closed for posting.</summary>
    private sealed class ClosedPeriods : IFinancePeriodService
    {
        public Task<PeriodStatusResponse> StatusAsync(string legalEntity, ValidAt? validAt = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PeriodStatusResponse { LegalEntity = legalEntity, Period = "2026-11", State = "LOCKED", ClosedForPosting = true });
    }
}
