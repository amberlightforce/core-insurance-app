using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Platform.Events;
using static CoreIns.IntegrationTests.Bil.BillingSlice;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>
/// E2E-01 from bind to paid invoice with every module real: POL's ChargeDeltaEmitted and PolicyBound → BIL account,
/// ANNUAL invoice, CMP fiscal document on the myDATA stub, payment, allocation, sub-ledger and BillingEntryPosted.
/// </summary>
public sealed class BillingFlowTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private BillingSlice _slice = null!;

    public async ValueTask InitializeAsync()
    {
        _slice = new BillingSlice(database);
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    [Fact]
    public async Task E2E_01_bind_invoice_fiscal_stub_payment_and_balanced_sub_ledger()
    {
        var policy = await _slice.BindAsync();
        await _slice.DrainAsync();

        // REQ-BIL-030/033/052: the payer's account and the ANNUAL plan instance.
        var invoice = await _slice.InvoiceOfAsync(policy.PolicyId);
        var accountId = invoice.Text("invoice.billingAccountId");
        var (gotAccount, account) = await _slice.GetAsync($"/api/bil/v1/billing-accounts/{accountId}");
        gotAccount.StatusCode.ShouldBe(HttpStatusCode.OK, account?.ToJsonString());
        account.Text("account.payerPartyId").ShouldBe(policy.PartyId);
        account.Text("account.accountNumber").ShouldStartWith("BA");
        account.Text("account.status").ShouldBe("ACTIVE");
        account.Text("account.terms.0.planCode").ShouldBe("ANNUAL");
        account.Text("account.terms.0.policyTermId").ShouldBe(policy.TermId);

        // REQ-BIL-002/067/086/087: one invoice for the whole set, charges frozen as received, gapless number, due on billing.
        invoice.Text("invoice.state").ShouldBe("DUE");
        invoice.Text("invoice.kind").ShouldBe("INVOICE");
        invoice.Text("invoice.invoiceNumber").ShouldStartWith("INV");
        invoice.Text("invoice.transactionId").ShouldBe(policy.TransactionId);
        Amount(invoice["invoice"]!["total"]).ShouldBe(policy.Total);
        Amount(invoice["invoice"]!["open"]).ShouldBe(policy.Total);
        var items = invoice["invoiceItems"]!.AsArray();
        items.Count.ShouldBe(policy.ChargeCount);
        items.Select(i => i!["chargeType"]!.GetValue<string>()).ShouldContain("GR-IPT");
        items.ShouldAllBe(i => i!["fiscalCategoryKey"] != null);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.charge WHERE transaction_id = '{policy.TransactionId}' AND status = 'SCHEDULED'"))
            .ShouldBe(policy.ChargeCount);

        // REQ-BIL-096/098, REQ-CMP-038/046: the fiscal document on the stub channel; identifiers are synthetic and marked.
        invoice.Text("fiscalStatus.status").ShouldBe("REGISTERED");
        invoice.Text("fiscalStatus.triggerPoint").ShouldBe("TRANSACTION");
        invoice.Text("fiscalStatus.mark").ShouldStartWith("STUB-");
        invoice.Text("fiscalStatus.uid").ShouldStartWith("STUB-");
        invoice.Text("fiscalStatus.series").ShouldBe("STUB-A");
        invoice.Text("fiscalStatus.number").ShouldNotBe("null");
        invoice.Text("fiscalStatus.documentType").ShouldBe("UNMAPPED-OQ-012");
        var (gotFiscal, fiscal) = await _slice.GetAsync($"/api/cmp/v1/fiscal-documents/{invoice.Text("fiscalStatus.fiscalDocumentId")}");
        gotFiscal.StatusCode.ShouldBe(HttpStatusCode.OK, fiscal?.ToJsonString());
        fiscal.Text("identifiers.stub").ShouldBe("true");
        fiscal.Text("document.documentTypeIsPlaceholder").ShouldBe("true");
        fiscal.Text("document.sourceType").ShouldBe("TRANSACTION");
        Amount(fiscal!["document"]!["total"]).ShouldBe(policy.Total);

        // REQ-BIL-066/071, PRD-06 §4.13: WRITTEN per delta (IPT to LA-27 under liability point DUE), BILLED, IPT_DUE.
        var entries = $"SELECT count(*) FROM bil.ledger_entry WHERE billing_account_id = '{accountId}'";
        (await _slice.ScalarAsync<long>(entries + " AND entry_type = 'WRITTEN'")).ShouldBe(policy.ChargeCount);
        (await _slice.ScalarAsync<long>(entries + " AND entry_type = 'BILLED'")).ShouldBe(1);
        (await _slice.ScalarAsync<long>(entries + " AND entry_type = 'IPT_DUE'")).ShouldBe(1);
        (await _slice.ScalarAsync<long>(
            $"SELECT count(*) FROM bil.ledger_line WHERE billing_account_id = '{accountId}' AND account_code = 'LA-27' AND side = 'CREDIT' AND charge_type = 'GR-IPT'"))
            .ShouldBeGreaterThanOrEqualTo(1);

        // REQ-BIL-004/126/127/129/130: exact bank transfer, matched by the unique open amount, allocated, invoice Paid.
        var (paid, payment) = await _slice.PostAsync("/api/bil/v1/payments/take", new
        {
            billingAccountId = accountId,
            amount = new { amount = policy.Total.ToString(System.Globalization.CultureInfo.InvariantCulture), currency = "EUR" },
            method = "BANK_TRANSFER",
            bankReference = "TRF-0001",
        });
        paid.StatusCode.ShouldBe(HttpStatusCode.Created, payment?.ToJsonString());
        payment.Text("allocationOutcome").ShouldBe("ALLOCATED");
        payment.Text("receipt.state").ShouldBe("ALLOCATED");
        payment.Text("receipt.receiptNumber").ShouldStartWith("RCP");
        payment!["allocations"]!.AsArray().Count.ShouldBe(policy.ChargeCount);
        payment["allocations"]!.AsArray().ShouldAllBe(a => a!["ruleId"]!.GetValue<string>() == "UNIQUE_OPEN_AMOUNT");

        var after = await _slice.InvoiceOfAsync(policy.PolicyId);
        after.Text("invoice.state").ShouldBe("PAID");
        Amount(after["invoice"]!["open"]).ShouldBe(0m);
        after["invoiceItems"]!.AsArray().ShouldAllBe(i => i!["state"]!.GetValue<string>() == "SETTLED");

        // REQ-BIL-001/285: balances derived from the ledger: nothing unbilled, open or unapplied; collected = total.
        var (_, balances) = await _slice.GetAsync($"/api/bil/v1/billing-accounts/{accountId}");
        Amount(balances!["balancesByState"]!["writtenUnbilled"]).ShouldBe(0m);
        Amount(balances["balancesByState"]!["billed"]).ShouldBe(0m);
        Amount(balances["balancesByState"]!["unapplied"]).ShouldBe(0m);
        Amount(balances["balancesByState"]!["collected"]).ShouldBe(policy.Total);

        // REQ-BIL-280/313: every entry balances, and each entry has exactly one BillingEntryPosted with its lines.
        (await _slice.ScalarAsync<long>(
            "SELECT count(*) FROM (SELECT entry_id FROM bil.ledger_line GROUP BY entry_id, currency"
            + " HAVING sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END) <> 0) unbalanced")).ShouldBe(0);
        var entryCount = await _slice.ScalarAsync<long>(entries);
        entryCount.ShouldBe(policy.ChargeCount + 4L); // WRITTEN×n, BILLED, IPT_DUE, RECEIVED, ALLOCATED
        var posted = await _slice.EnvelopesAsync(accountId, "BillingEntryPosted");
        posted.Count.ShouldBe((int)entryCount);
        posted.Select(e => e.BusinessKeys["entryId"]).Distinct().Count().ShouldBe((int)entryCount);
        posted.Select(e => e.Payload["eventType"]!.GetValue<string>()).Distinct().Order()
            .ShouldBe(["ALLOCATED", "BILLED", "IPT_DUE", "RECEIVED", "WRITTEN"]);
        foreach (var envelope in posted)
        {
            var lines = envelope.Payload["lines"]!.AsArray();
            lines.Sum(l => (l!["side"]!.GetValue<string>() == "DEBIT" ? 1 : -1) * Amount(l["amount"])).ShouldBe(0m);
            lines.ShouldAllBe(l => l!["dimensions"]!["billingAccountId"]!.GetValue<string>() == accountId);
        }

        var written = posted.Where(e => e.Payload["eventType"]!.GetValue<string>() == "WRITTEN").ToList();
        written.ShouldAllBe(e => e.BusinessKeys.ContainsKey("chargeId") && e.BusinessKeys["transactionId"] == policy.TransactionId);
        written.SelectMany(e => e.Payload["lines"]!.AsArray()).ShouldAllBe(l => l!["dimensions"]!["chargeType"] != null
            && l["dimensions"]!["productCode"]!.GetValue<string>() == "MOTOR-GR" && l["dimensions"]!["policyTermId"]!.GetValue<string>() == policy.TermId);

        // The other contract events (context for FIN): BillingAccountChanged, ChargesScheduled, InvoiceIssued, PaymentReceived, CashAllocated.
        foreach (var type in new[] { "BillingAccountChanged", "ChargesScheduled", "InvoiceIssued", "PaymentReceived", "CashAllocated" })
        {
            (await _slice.EnvelopesAsync(accountId, type)).Count.ShouldBe(1, type);
        }

        var cash = (await _slice.EnvelopesAsync(accountId, "CashAllocated")).Single();
        cash.Payload["arrearsCleared"]![0]!["arrearsCleared"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task REQ_BIL_002_redelivered_and_replayed_events_change_nothing()
    {
        var policy = await _slice.BindAsync();
        await _slice.DrainAsync();
        var before = await CountsAsync();

        // Replays bypass the processed-event marker: BIL's own idempotency (charge id, term id) must hold.
        foreach (var envelope in await _slice.EnvelopesAsync(policy.PolicyId))
        {
            if (envelope.EventType.Value == "ChargeDeltaEmitted")
            {
                (await _slice.InvokeAsync("BIL.ChargeDeltaEmitted.Intake", envelope, replay: true)).ShouldBeTrue();
            }
            else if (envelope.EventType.Value == "PolicyBound")
            {
                (await _slice.InvokeAsync("BIL.PolicyBound.AttachTerm", envelope, replay: true)).ShouldBeTrue();
            }
        }

        await _slice.DrainAsync();
        (await CountsAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task D_ARC_26_PolicyBound_before_the_deltas_and_deltas_in_reverse_order_bill_once_when_the_set_completes()
    {
        var policy = await _slice.BindAsync();
        var envelopes = await _slice.EnvelopesAsync(policy.PolicyId);
        var deltas = envelopes.Where(e => e.EventType.Value == "ChargeDeltaEmitted").OrderByDescending(e => e.AggregateSequence).ToList();
        var bound = envelopes.Single(e => e.EventType.Value == "PolicyBound");

        (await _slice.InvokeAsync("BIL.PolicyBound.AttachTerm", bound)).ShouldBeTrue();
        foreach (var delta in deltas.Take(deltas.Count - 1))
        {
            (await _slice.InvokeAsync("BIL.ChargeDeltaEmitted.Intake", delta)).ShouldBeTrue();
        }

        // REQ-BIL-364: written entries post on arrival; nothing is scheduled until the last member arrives.
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.charge WHERE transaction_id = '{policy.TransactionId}' AND status = 'WRITTEN'"))
            .ShouldBe(deltas.Count - 1);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.invoice WHERE transaction_id = '{policy.TransactionId}'")).ShouldBe(0);

        (await _slice.InvokeAsync("BIL.ChargeDeltaEmitted.Intake", deltas[^1])).ShouldBeTrue();
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.invoice WHERE transaction_id = '{policy.TransactionId}'")).ShouldBe(1);

        // The regular dispatch afterwards finds them processed and bills nothing twice.
        await _slice.DrainAsync();
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.invoice WHERE transaction_id = '{policy.TransactionId}'")).ShouldBe(1);
        (await _slice.ScalarAsync<decimal>($"SELECT total FROM bil.invoice WHERE transaction_id = '{policy.TransactionId}'")).ShouldBe(policy.Total);
    }

    [Fact]
    public async Task REQ_BIL_098_a_rejected_fiscal_document_flags_the_invoice_and_a_later_registration_clears_it()
    {
        var policy = await _slice.BindAsync();
        foreach (var envelope in await _slice.EnvelopesAsync(policy.PolicyId))
        {
            var handler = envelope.EventType.Value == "PolicyBound" ? "BIL.PolicyBound.AttachTerm" : "BIL.ChargeDeltaEmitted.Intake";
            if (envelope.EventType.Value is "PolicyBound" or "ChargeDeltaEmitted")
            {
                await _slice.InvokeAsync(handler, envelope);
            }
        }

        var fiscalId = await _slice.ScalarAsync<Guid>($"SELECT fiscal_document_id FROM bil.invoice WHERE transaction_id = '{policy.TransactionId}'");
        (await _slice.ScalarAsync<string>($"SELECT fiscal_status FROM bil.invoice WHERE transaction_id = '{policy.TransactionId}'")).ShouldBe("PENDING");
        var registered = (await _slice.EnvelopesAsync(fiscalId.ToString(), "FiscalDocRegistered")).Single();

        // A synthetic rejection of the same document (the stub never rejects).
        var rejected = registered with
        {
            EventId = CoreIns.SharedKernel.Identifiers.EventId.New(),
            EventType = CoreIns.SharedKernel.Identifiers.EventTypeName.Parse("FiscalDocRejected"),
            Payload = new JsonObject
            {
                ["sourceType"] = "TRANSACTION", ["sourceId"] = policy.TransactionId, ["causeGroup"] = "VALIDATION", ["codes"] = new JsonArray("STUB-TEST-CODE"),
            },
        };
        (await _slice.InvokeAsync("BIL.FiscalDocRejected.FlagInvoice", rejected)).ShouldBeTrue();
        (await _slice.ScalarAsync<string>($"SELECT fiscal_status FROM bil.invoice WHERE transaction_id = '{policy.TransactionId}'")).ShouldBe("REJECTED");
        (await _slice.ScalarAsync<long>("SELECT count(*) FROM bil.intake_exception WHERE kind = 'CMP-FISCAL-REJECTED'")).ShouldBeGreaterThanOrEqualTo(1);
        (await _slice.ScalarAsync<string>($"SELECT state FROM bil.invoice WHERE transaction_id = '{policy.TransactionId}'")).ShouldBe("DUE"); // never held (REQ-BIL-099)

        (await _slice.InvokeAsync("BIL.FiscalDocRegistered.StoreMark", registered)).ShouldBeTrue();
        (await _slice.ScalarAsync<string>($"SELECT fiscal_status FROM bil.invoice WHERE transaction_id = '{policy.TransactionId}'")).ShouldBe("REGISTERED");
        (await _slice.ScalarAsync<string>($"SELECT fiscal_mark FROM bil.invoice WHERE transaction_id = '{policy.TransactionId}'")).ShouldStartWith("STUB-");
    }

    [Fact]
    public async Task REQ_BIL_065_and_D_SLC_10c_an_unknown_charge_type_is_quarantined_and_a_non_ANNUAL_plan_is_not_billed()
    {
        var policy = await _slice.BindAsync();
        var envelopes = await _slice.EnvelopesAsync(policy.PolicyId);
        var bound = envelopes.Single(e => e.EventType.Value == "PolicyBound");
        var delta = envelopes.First(e => e.EventType.Value == "ChargeDeltaEmitted");

        // A synthetic term of one unknown charge type (set of 1) attached with ANNUAL.
        var term = Guid.CreateVersion7().ToString();
        var transaction = Guid.CreateVersion7().ToString();
        var badBound = Retarget(bound, term, transaction);
        var badPayload = (JsonObject)Retarget(delta, term, transaction).Payload.DeepClone();
        badPayload["chargeId"] = Guid.CreateVersion7().ToString();
        badPayload["chargeType"] = "UNKNOWN-CHARGE";
        var badDelta = Retarget(delta, term, transaction) with { Payload = badPayload, Set = new EventSet(Guid.Parse(transaction), 1, 1) };
        (await _slice.InvokeAsync("BIL.PolicyBound.AttachTerm", badBound)).ShouldBeTrue();
        (await _slice.InvokeAsync("BIL.ChargeDeltaEmitted.Intake", badDelta)).ShouldBeTrue();
        (await _slice.ScalarAsync<string>($"SELECT quarantine_reason FROM bil.charge WHERE term_id = '{term}'")).ShouldBe("CHARGE-TYPE-UNKNOWN");
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_line WHERE term_id = '{term}'")).ShouldBe(0); // REQ-BIL-287: nothing posted
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.invoice WHERE term_id = '{term}'")).ShouldBe(0);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.intake_exception WHERE kind = 'BIL-DELTA-SET-BLOCKED' AND subject = '{transaction}'")).ShouldBe(1);

        // An instalment plan (PRD-02 INST-12-DD) is not served by the slice: no plan instance, an exception instead.
        var otherTerm = Guid.CreateVersion7().ToString();
        var instalments = Retarget(bound, otherTerm, Guid.CreateVersion7().ToString());
        var payload = (JsonObject)instalments.Payload.DeepClone();
        payload["paymentPlanRef"] = "INST-12-DD";
        (await _slice.InvokeAsync("BIL.PolicyBound.AttachTerm", instalments with { Payload = payload })).ShouldBeTrue();
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.plan_instance WHERE term_id = '{otherTerm}'")).ShouldBe(0);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.intake_exception WHERE kind = 'BIL-PLAN-UNSUPPORTED' AND subject = '{otherTerm}'")).ShouldBe(1);
    }

    private static EventEnvelope Retarget(EventEnvelope envelope, string term, string transaction)
    {
        var payload = (JsonObject)envelope.Payload.DeepClone();
        payload["termId"] = term;
        payload["transactionId"] = transaction;
        return envelope with
        {
            EventId = CoreIns.SharedKernel.Identifiers.EventId.New(),
            Payload = payload,
            BusinessKeys = envelope.BusinessKeys.With("policyTermId", term).With("transactionId", transaction),
        };
    }

    private static readonly string[] Tables = ["charge", "plan_instance", "billing_account", "invoice", "invoice_item", "ledger_entry", "ledger_line", "allocation", "receipt"];

    private async Task<string> CountsAsync() =>
        string.Join("|", await Task.WhenAll(
            Tables.Select(async table => $"{table}={await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.{table}")}"))
            .ConfigureAwait(false))
        + $"|fiscal={await _slice.ScalarAsync<long>("SELECT count(*) FROM cmp.fiscal_document")}";
}
