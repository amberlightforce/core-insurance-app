using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Bil.Credits;
using CoreIns.IntegrationTests.Bil.Refunds;
using static CoreIns.IntegrationTests.Bil.BillingSlice;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>
/// BO-BIL-08: the suspense workbench. Unapplied cash is a first-class item (listed, suggested, transferred, refunded to its sender);
/// a transfer posts a ledger move, a refund runs the refund approval path (second person, VoP, screening) and money out is
/// forbidden to non-billing roles.
/// </summary>
public sealed class SuspenseTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private RefundHarness _h = null!;

    private BillingSlice Slice => _h.Slice;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _h = await RefundHarness.StartAsync(database, o => o.PayeeCoolingOffDays = 0);

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private static object Money(decimal amount) => new { amount = amount.ToString(CultureInfo.InvariantCulture), currency = "EUR" };

    /// <summary>A bound and billed policy and its billing account and invoice.</summary>
    private async Task<(BoundPolicy Policy, string AccountId, string InvoiceId, string InvoiceNumber)> BilledAsync()
    {
        var policy = await Slice.BindAsync();
        await Slice.DrainAsync();
        var invoice = await Slice.InvoiceOfAsync(policy.PolicyId);
        return (policy, invoice.Text("invoice.billingAccountId"), invoice.Text("invoice.invoiceId"), invoice.Text("invoice.invoiceNumber"));
    }

    /// <summary>Takes a payment that cannot be matched (below the open amount) and returns its receipt and suspense item.</summary>
    private async Task<(JsonNode Receipt, JsonNode Item)> UnappliedAsync(string accountId, decimal amount)
    {
        var (response, body) = await Slice.PostAsync("/api/bil/v1/payments/take", new { billingAccountId = accountId, amount = Money(amount), method = "BANK_TRANSFER", bankReference = Guid.NewGuid().ToString("N") });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        body.Text("allocationOutcome").ShouldBe("SUSPENSE");
        var receipt = body!["receipt"]!;
        var item = await ItemOfAsync(receipt.Text("receiptId"));
        return (receipt, item);
    }

    private async Task<JsonNode> ItemOfAsync(string receiptId, string state = "OPEN")
    {
        var (response, page) = await Slice.GetAsync($"/api/bil/v1/suspense?state={state}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        return page!["items"]!.AsArray().Single(i => i!.Text("receiptId") == receiptId)!;
    }

    private async Task<decimal> UnappliedBalanceAsync(string accountId)
    {
        var (_, balances) = await Slice.GetAsync($"/api/bil/v1/billing-accounts/{accountId}");
        return Amount(balances!["balancesByState"]!["unapplied"]);
    }

    /// <summary>The lines of the account's SUSPENSE_REFUND_APPROVED entries as "account side amount source source-id".</summary>
    private Task<List<string>> RefundLinesAsync(string accountId) => _h.TextsAsync(
        $"""
        SELECT l.account_code || ' ' || l.side || ' ' || l.amount::text || ' ' || coalesce(l.source_type, '-') || ' ' || coalesce(l.source_id, '-') || ' -'
        FROM bil.ledger_line l JOIN bil.ledger_entry e ON e.entry_id = l.entry_id
        WHERE e.entry_type = 'SUSPENSE_REFUND_APPROVED' AND e.billing_account_id = '{accountId}' ORDER BY e.recorded_at, l.line_no
        """);

    private async Task<string> RegisterPayeeAsync(string partyId)
    {
        var (response, body) = await _h.SendAsync(
            HttpMethod.Post, "/api/bil/v1/payee-accounts", new { partyId, purpose = "REFUND", iban = DisbursementSlice.NewIban(), holderName = "Ελένη Παπαδοπούλου", evidenceRef = "DOC-1" },
            RefundUsers.Clerk, "alice");
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return body.Text("payeeAccountId");
    }

    [Fact]
    public async Task REQ_BIL_135_an_unmatched_payment_becomes_an_open_suspense_item_that_billing_staff_list()
    {
        var (policy, account, _, _) = await BilledAsync();
        var (receipt, item) = await UnappliedAsync(account, policy.Total - 10m);

        item.Text("state").ShouldBe("OPEN");
        item.Text("reason").ShouldBe("AMOUNT_MISMATCH");
        item.Text("billingAccountId").ShouldBe(account);
        item.Text("receiptNumber").ShouldBe(receipt.Text("receiptNumber"));
        Amount(item["amount"]).ShouldBe(policy.Total - 10m);

        var (byAccount, filtered) = await Slice.GetAsync($"/api/bil/v1/suspense?billingAccountId={account}");
        byAccount.StatusCode.ShouldBe(HttpStatusCode.OK);
        filtered!["items"]!.AsArray().Count.ShouldBe(1);
        (await Slice.GetAsync($"/api/bil/v1/suspense?billingAccountId={Guid.CreateVersion7()}")).Body!["items"]!.AsArray().Count.ShouldBe(0);
        (await Slice.GetAsync($"/api/bil/v1/suspense?state=ALLOCATED&billingAccountId={account}")).Body!["items"]!.AsArray().Count.ShouldBe(0);
        (await Slice.GetAsync("/api/bil/v1/suspense?state=NOPE")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Money out and moves are for billing roles only.
        (await Slice.GetAsync("/api/bil/v1/suspense", roles: Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var itemId = item.Text("suspenseItemId");
        (await Slice.PostAsync("/api/bil/v1/suspense/suggest", new { suspenseItemId = itemId }, roles: Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Slice.PostAsync("/api/bil/v1/suspense/transfer", new { suspenseItemId = itemId, targetBillingAccountId = Guid.CreateVersion7() }, roles: Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Slice.PostAsync("/api/bil/v1/suspense/refund-to-sender", new { suspenseItemId = itemId, payeeAccountId = Guid.CreateVersion7(), reasonCode = "MISDIRECTED" }, roles: Underwriter))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task NX_HD_14_suggest_ranks_candidates_from_server_side_facts_only()
    {
        var (policy, account, _, invoiceNumber) = await BilledAsync();
        var (_, item) = await UnappliedAsync(account, policy.Total - 10m);

        var (response, body) = await Slice.PostAsync("/api/bil/v1/suspense/suggest", new { suspenseItemId = item.Text("suspenseItemId") });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("item.suspenseItemId").ShouldBe(item.Text("suspenseItemId"));
        var first = body!["suggestions"]!.AsArray().First(s => s!.Text("invoiceNo") == invoiceNumber)!;
        first["exactAmount"]!.GetValue<bool>().ShouldBeFalse();
        first["score"]!.GetValue<int>().ShouldBeGreaterThan(0);

        (await Slice.PostAsync("/api/bil/v1/suspense/suggest", new { suspenseItemId = Guid.CreateVersion7() })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BO_BIL_08_a_transfer_posts_a_ledger_move_to_the_target_account_and_the_cash_can_then_be_allocated_there()
    {
        var (policyA, accountA, _, _) = await BilledAsync();
        var (policyB, accountB, invoiceB, _) = await BilledAsync();
        var amount = policyA.Total - 10m;
        var (receipt, item) = await UnappliedAsync(accountA, amount);
        var itemId = item.Text("suspenseItemId");
        (await UnappliedBalanceAsync(accountA)).ShouldBe(amount);
        (await UnappliedBalanceAsync(accountB)).ShouldBe(0m);

        // The cash cannot go where it already is, nowhere, or to a billing account that does not exist.
        (await Slice.PostAsync("/api/bil/v1/suspense/transfer", new { suspenseItemId = itemId, targetBillingAccountId = accountA })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Slice.PostAsync("/api/bil/v1/suspense/transfer", new { suspenseItemId = itemId, targetBillingAccountId = Guid.CreateVersion7() })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Slice.PostAsync("/api/bil/v1/suspense/transfer", new { suspenseItemId = Guid.CreateVersion7(), targetBillingAccountId = accountB })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var (response, body) = await Slice.PostAsync("/api/bil/v1/suspense/transfer", new { suspenseItemId = itemId, targetBillingAccountId = accountB, comment = "paid on the wrong policy" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("item.billingAccountId").ShouldBe(accountB);
        body.Text("item.state").ShouldBe("OPEN");
        body.Text("item.reason").ShouldBe("TRANSFERRED");

        // The ledger moved the unapplied cash: LA-11 of A debited, LA-11 of B credited, through the clearing account LA-29.
        var lines = await _h.TextsAsync(
            $"""
            SELECT l.account_code || ' ' || l.side || ' ' || l.amount::text || ' ' || l.billing_account_id::text
            FROM bil.ledger_line l JOIN bil.ledger_entry e ON e.entry_id = l.entry_id
            WHERE e.entry_type = 'SUSPENSE_TRANSFERRED' AND e.entry_id = '{body.Text("ledgerEntryId")}' ORDER BY l.line_no
            """);
        lines.Count.ShouldBe(4);
        lines.ShouldContain($"LA-11 DEBIT {amount.ToString("0.0000", CultureInfo.InvariantCulture)} {accountA}");
        lines.ShouldContain($"LA-11 CREDIT {amount.ToString("0.0000", CultureInfo.InvariantCulture)} {accountB}");
        lines.Count(l => l.StartsWith("LA-29", StringComparison.Ordinal)).ShouldBe(2);
        (await UnappliedBalanceAsync(accountA)).ShouldBe(0m);
        (await UnappliedBalanceAsync(accountB)).ShouldBe(amount);
        (await Slice.ScalarAsync<string>($"SELECT billing_account_id::text FROM bil.receipt WHERE receipt_id = '{receipt.Text("receiptId")}'")).ShouldBe(accountB);
        await Slice.AllEntriesBalanceAsync();

        // The cash is allocated on the target as any unapplied cash (a partial payment of B's invoice), which resolves the item.
        var (ok, allocated) = await Slice.PostAsync("/api/bil/v1/allocations/allocate",
            new { receiptId = receipt.Text("receiptId"), lines = new[] { new { invoiceId = invoiceB, amount = Money(amount) } } });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, allocated?.ToJsonString());
        (await ItemOfAsync(receipt.Text("receiptId"), "ALLOCATED")).Text("state").ShouldBe("ALLOCATED");
        (await Slice.GetAsync($"/api/bil/v1/suspense?billingAccountId={accountB}")).Body!["items"]!.AsArray().Count.ShouldBe(0);

        // A resolved item cannot be moved again.
        (await Slice.PostAsync("/api/bil/v1/suspense/transfer", new { suspenseItemId = itemId, targetBillingAccountId = accountA })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        policyB.Total.ShouldBeGreaterThan(amount);
    }

    [Fact]
    public async Task BO_BIL_08_refund_to_sender_goes_through_a_second_approver_screening_and_the_disbursement()
    {
        var (policy, account, _, _) = await BilledAsync();
        var amount = policy.Total - 10m;
        var (receipt, item) = await UnappliedAsync(account, amount);
        var itemId = item.Text("suspenseItemId");
        var payee = await RegisterPayeeAsync(policy.PartyId);

        var (response, body) = await Slice.PostAsync("/api/bil/v1/suspense/refund-to-sender", new { suspenseItemId = itemId, payeeAccountId = payee, reasonCode = "MISDIRECTED" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var refundId = body.Text("refund.refundId");
        body.Text("item.state").ShouldBe("REFUND_PENDING");
        body.Text("item.refundId").ShouldBe(refundId);
        body.Text("refund.state").ShouldBe("PENDING_APPROVAL");
        body.Text("refund.approvalState").ShouldBe("PENDING");

        // Nothing moved yet; the cash cannot be applied or moved while the refund waits; a second request for the item is refused.
        (await RefundLinesAsync(account)).ShouldBeEmpty();
        (await Slice.PostAsync("/api/bil/v1/suspense/refund-to-sender", new { suspenseItemId = itemId, payeeAccountId = payee, reasonCode = "MISDIRECTED" })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Slice.PostAsync("/api/bil/v1/suspense/transfer", new { suspenseItemId = itemId, targetBillingAccountId = Guid.CreateVersion7() })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var (_, invoice) = await Slice.GetAsync($"/api/bil/v1/invoices?policyId={policy.PolicyId}");
        var invoiceId = invoice!["items"]![0]!["invoice"]!.Text("invoiceId");
        var (blocked, blockedBody) = await Slice.PostAsync("/api/bil/v1/allocations/allocate",
            new { receiptId = receipt.Text("receiptId"), lines = new[] { new { invoiceId, amount = Money(amount) } } });
        blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict, blockedBody?.ToJsonString());
        blockedBody.Text("code").ShouldBe("BIL-ERR-SUSPENSE-STATE");

        // Segregation of duties: the maker cannot decide it, even holding the manager role; the clerk has no decide permission.
        (await _h.DecideAsync(refundId, "APPROVE", "carol", RefundUsers.Clerk)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (self, selfBody) = await _h.DecideAsync(refundId, "APPROVE", "test-user", RefundUsers.Both);
        self.StatusCode.ShouldBe(HttpStatusCode.Forbidden, selfBody?.ToJsonString());
        selfBody.Text("code").ShouldBe("PLT-ERR-SELF-APPROVAL");

        // Sanctions screening that is not clear refuses the payment and rolls the approval back (fail closed).
        _h.Screening.Setup("pty.Screening.screen", new CoreIns.Modules.Party.Contracts.Api.ScreeningScreenResponse
        {
            Result = CoreIns.Modules.Party.Contracts.Api.ScreeningScreenResponse.ResultValue.Blocked,
            ListVersions = ["EU-TEST-2026-10"],
            PaymentBlock = true,
        });
        var (hit, hitBody) = await _h.DecideAsync(refundId, "APPROVE", "bob");
        hit.StatusCode.ShouldBe(HttpStatusCode.Conflict, hitBody?.ToJsonString());
        hitBody.Text("code").ShouldBe("BIL-ERR-PAYEE-BLOCKED");
        (await RefundLinesAsync(account)).ShouldBeEmpty();
        (await Slice.ScalarAsync<string>($"SELECT state FROM bil.receipt WHERE receipt_id = '{receipt.Text("receiptId")}'")).ShouldBe("SUSPENSE");
        (await Slice.ScalarAsync<long>($"SELECT count(*) FROM bil.disbursement WHERE source_id = '{refundId}'")).ShouldBe(0);

        // Cleared: the second person approves; unapplied cash moves LA-11 -> LA-12, the receipt is Refunded and the payment is issued.
        _h.Screening.Setup("pty.Screening.screen", DisbursementSlice.Clear());
        var (approved, approvedBody) = await _h.DecideAsync(refundId, "APPROVE", "bob");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, approvedBody?.ToJsonString());
        approvedBody!["refund"]!.Text("state").ShouldBe("PAID");
        (await RefundLinesAsync(account)).ShouldBe(
        [
            $"LA-11 DEBIT {amount.ToString("0.0000", CultureInfo.InvariantCulture)} BIL_REFUND {refundId} -",
            $"LA-12 CREDIT {amount.ToString("0.0000", CultureInfo.InvariantCulture)} BIL_REFUND {refundId} -",
        ]);
        (await Slice.ScalarAsync<string>($"SELECT state FROM bil.receipt WHERE receipt_id = '{receipt.Text("receiptId")}'")).ShouldBe("REFUNDED");
        (await ItemOfAsync(receipt.Text("receiptId"), "REFUNDED")).Text("state").ShouldBe("REFUNDED");
        (await _h.TextsAsync($"SELECT source_type || '|' || amount::text || '|' || payee_account_id::text FROM bil.disbursement WHERE source_id = '{refundId}'"))
            .ShouldHaveSingleItem().ShouldBe($"BIL_REFUND|{amount.ToString("0.0000", CultureInfo.InvariantCulture)}|{payee}");
        (await UnappliedBalanceAsync(account)).ShouldBe(0m);
        await Slice.AllEntriesBalanceAsync();
    }

    [Fact]
    public async Task BO_BIL_08_a_rejected_refund_to_sender_returns_the_cash_to_the_workbench()
    {
        var (policy, account, _, _) = await BilledAsync();
        var (receipt, item) = await UnappliedAsync(account, policy.Total - 10m);
        var itemId = item.Text("suspenseItemId");
        var payee = await RegisterPayeeAsync(policy.PartyId);
        var (_, body) = await Slice.PostAsync("/api/bil/v1/suspense/refund-to-sender", new { suspenseItemId = itemId, payeeAccountId = payee, reasonCode = "MISDIRECTED" });
        var refundId = body.Text("refund.refundId");

        var (rejected, rejectedBody) = await _h.DecideAsync(refundId, "REJECT", "bob", comment: "sender unknown");
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, rejectedBody?.ToJsonString());

        (await ItemOfAsync(receipt.Text("receiptId"))).Text("state").ShouldBe("OPEN");
        (await Slice.ScalarAsync<string>($"SELECT state FROM bil.receipt WHERE receipt_id = '{receipt.Text("receiptId")}'")).ShouldBe("SUSPENSE");
        (await RefundLinesAsync(account)).ShouldBeEmpty();
    }
}

/// <summary>BO-BIL-08: VoP that did not answer leaves the payee account unverified; the cash is not refunded to it (REQ-BIL-186).</summary>
public sealed class SuspenseUnverifiedPayeeTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private RefundHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await RefundHarness.StartAsync(database, verifier: false);

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task REQ_BIL_186_refund_to_sender_needs_a_verified_payee_account_and_the_item_stays_open()
    {
        var slice = _h.Slice;
        var policy = await slice.BindAsync();
        await slice.DrainAsync();
        var account = (await slice.InvoiceOfAsync(policy.PolicyId)).Text("invoice.billingAccountId");
        var (taken, receipt) = await slice.PostAsync("/api/bil/v1/payments/take", new { billingAccountId = account, amount = new { amount = "17.00", currency = "EUR" }, method = "BANK_TRANSFER" });
        taken.StatusCode.ShouldBe(HttpStatusCode.Created, receipt?.ToJsonString());
        var (_, page) = await slice.GetAsync("/api/bil/v1/suspense");
        var itemId = page!["items"]![0]!.Text("suspenseItemId");
        var (created, payee) = await _h.SendAsync(
            HttpMethod.Post, "/api/bil/v1/payee-accounts", new { partyId = policy.PartyId, purpose = "REFUND", iban = DisbursementSlice.NewIban(), holderName = "Ελένη Παπαδοπούλου", evidenceRef = "DOC-1" },
            RefundUsers.Clerk, "alice");
        created.StatusCode.ShouldBe(HttpStatusCode.Created, payee?.ToJsonString());

        var (response, body) = await slice.PostAsync("/api/bil/v1/suspense/refund-to-sender", new { suspenseItemId = itemId, payeeAccountId = payee.Text("payeeAccountId"), reasonCode = "MISDIRECTED" });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, body?.ToJsonString());
        body.Text("code").ShouldBe("BIL-ERR-PAYEE-UNVERIFIED");
        (await slice.GetAsync("/api/bil/v1/suspense")).Body!["items"]![0]!.Text("state").ShouldBe("OPEN");
        (await slice.ScalarAsync<long>("SELECT count(*) FROM bil.refund")).ShouldBe(0);
    }
}
