using System.Globalization;
using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel.Identifiers;
using Npgsql;
using static CoreIns.IntegrationTests.Bil.BillingSlice;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil.Credits;

/// <summary>
/// SL3-BIL-CREDIT: credit notes and the credit balance (REQ-BIL-073, -074, -079, -091, -096, -097, -319), mid-term debits
/// and the renewal term (REQ-BIL-002) with every module real and the MKT treatment scripted (the MKT work package is
/// separate). The servicing events are what POL will send, built by hand from the bind's own deltas. Amounts are
/// illustrative test data (D-SLC-04, D-SL3-08): the tests assert consistency, not tariff values.
/// </summary>
public sealed class CreditNoteTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private BillingSlice _slice = null!;
    private readonly FakeTaxCalculator _treatment = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _slice = new BillingSlice(database, services => services.WithFakeTreatment(_treatment));
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private static decimal Money(JsonNode? node) => Amount(node);

    [Fact]
    public async Task E2E_03_money_a_paid_invoice_and_a_cancellation_give_a_credit_note_and_account_credit_with_IPT_untouched()
    {
        var s = await CreditScenario.BilledAsync(_slice);
        await s.PayAsync(s.Policy.Total);
        var iptPayableBefore = await _slice.ScalarAsync<decimal>(IptPayableSql(s.AccountId));

        var (set, credit, transaction) = s.ServicingSet("CANCELLATION", "Policyholder", factor: -0.6m);
        credit.ShouldBeLessThan(0m);
        (await _slice.InvokeAsync("BIL.PolicyCancelled.StopBilling", s.Cancelled(transaction))).ShouldBeTrue();
        await s.DeliverAsync(set);

        // REQ-BIL-091/074: one credit note referencing invoice 1, its own gapless CN series, total = the credited premium.
        var documents = await s.DocumentsAsync();
        documents.Count.ShouldBe(2);
        var note = documents.Single(d => d.Text("invoice.kind") == "CREDIT_NOTE");
        note.Text("invoice.originalInvoiceId").ShouldBe(s.InvoiceId);
        note.Text("invoice.invoiceNumber").ShouldStartWith("CN");
        note.Text("invoice.transactionId").ShouldBe(transaction.ToString());
        Money(note["invoice"]!["total"]).ShouldBe(-credit);
        Money(note["invoice"]!["open"]).ShouldBe(-credit); // the whole credit is left on the account: invoice 1 was paid
        note["invoiceItems"]!.AsArray().ShouldAllBe(i => i!["transactionKind"]!.GetValue<string>() == "CANCELLATION"
            && i["cancellationSource"]!.GetValue<string>() == "Policyholder" && i["state"]!.GetValue<string>() == "OPEN");

        // The paid invoice is untouched; the account holds the credit; no cash moved.
        var invoice = documents.Single(d => d.Text("invoice.kind") == "INVOICE");
        invoice.Text("invoice.state").ShouldBe("PAID");
        Money(invoice["invoice"]!["open"]).ShouldBe(0m);
        var balances = await s.BalancesAsync();
        Money(balances["credit"]).ShouldBe(-credit);
        Money(balances["billed"]).ShouldBe(0m);
        Money(balances["writtenUnbilled"]).ShouldBe(0m);

        // REQ-BIL-319, INV-05: IPT payable unchanged and no cancellation-sourced line touched LA-06 or LA-27.
        (await _slice.ScalarAsync<decimal>(IptPayableSql(s.AccountId))).ShouldBe(iptPayableBefore);
        (await _slice.ScalarAsync<long>(
            $"SELECT count(*) FROM bil.ledger_line WHERE billing_account_id = '{s.AccountId}' AND account_code IN ('LA-06', 'LA-27') AND cancellation_source IS NOT NULL"))
            .ShouldBe(0);

        // PRD-06 §4.13 steps 16 and 17: CREDIT_WRITTEN Dr LA-04 / Cr LA-01 per credit charge, CREDIT_BILLED Dr LA-01 / Cr LA-02.
        var creditLines = set.Count(e => CreditScenario.CategoryOf(e) != CreditScenario.TaxCategory);
        var entries = $"SELECT count(*) FROM bil.ledger_entry WHERE billing_account_id = '{s.AccountId}'";
        (await _slice.ScalarAsync<long>(entries + " AND entry_type = 'CREDIT_WRITTEN'")).ShouldBe(creditLines);
        (await _slice.ScalarAsync<long>(entries + " AND entry_type = 'CREDIT_BILLED'")).ShouldBe(1);
        (await _slice.ScalarAsync<decimal>(Net("CREDIT_BILLED", "LA-02", s.AccountId))).ShouldBe(credit);
        (await _slice.ScalarAsync<decimal>(Net("CREDIT_BILLED", "LA-01", s.AccountId))).ShouldBe(-credit);
        (await _slice.ScalarAsync<decimal>(Net("CREDIT_WRITTEN", "LA-04", s.AccountId))).ShouldBe(-credit);
        await _slice.AllEntriesBalanceAsync();

        // Every servicing line carries the dimensions (PITFALLS 12), the tax-free credit entries carry no rule id.
        await _slice.DrainAsync();
        var posted = (await _slice.EnvelopesAsync(s.AccountId, "BillingEntryPosted"))
            .Where(e => e.Payload["eventType"]!.GetValue<string>() is "CREDIT_WRITTEN" or "CREDIT_BILLED").ToList();
        posted.Count.ShouldBe(creditLines + 1);
        foreach (var line in posted.SelectMany(e => e.Payload["lines"]!.AsArray()))
        {
            line!["dimensions"]!["transactionKind"]!.GetValue<string>().ShouldBe("CANCELLATION");
            line["dimensions"]!["cancellationSource"]!.GetValue<string>().ShouldBe("Policyholder");
        }

        // REQ-BIL-096/097, D-SL3-07: exactly one fiscal CREDIT request, correlated to invoice 1's document.
        var originalDocument = await _slice.ScalarAsync<Guid>($"SELECT fiscal_document_id FROM bil.invoice WHERE invoice_id = '{s.InvoiceId}'");
        note.Text("fiscalStatus.fiscalDocumentId").ShouldNotBe("null");
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM cmp.fiscal_document WHERE source_type = 'CREDIT' AND role = 'CREDIT' AND source_id = '{note.Text("invoice.invoiceId")}'")).ShouldBe(1);
        var (got, fiscal) = await _slice.GetAsync($"/api/cmp/v1/fiscal-documents/{note.Text("fiscalStatus.fiscalDocumentId")}");
        got.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, fiscal?.ToJsonString());
        originalDocument.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task REQ_BIL_073_an_unpaid_invoice_and_a_cancellation_leave_the_earned_premium_and_IPT_open()
    {
        var s = await CreditScenario.BilledAsync(_slice);
        var (set, credit, transaction) = s.ServicingSet("CANCELLATION", "Policyholder", factor: -0.6m);
        (await _slice.InvokeAsync("BIL.PolicyCancelled.StopBilling", s.Cancelled(transaction))).ShouldBeTrue();
        await s.DeliverAsync(set);

        var documents = await s.DocumentsAsync();
        var invoice = documents.Single(d => d.Text("invoice.kind") == "INVOICE");
        var note = documents.Single(d => d.Text("invoice.kind") == "CREDIT_NOTE");

        // The credit note offsets the original's open balance; nothing is left on the account.
        Money(note["invoice"]!["open"]).ShouldBe(0m);
        Money(invoice["invoice"]!["open"]).ShouldBe(s.Policy.Total + credit);
        invoice.Text("invoice.state").ShouldBe("DUE");
        Money((await s.BalancesAsync())["credit"]).ShouldBe(0m);
        Money((await s.BalancesAsync())["billed"]).ShouldBe(s.Policy.Total + credit);
        invoice["invoiceItems"]!.AsArray().Where(i => i!["chargeCategory"]!.GetValue<string>() == "TAX")
            .ShouldAllBe(i => Money(i!["open"]) == Money(i["amount"])); // IPT stays fully open (KEEP_NOT_REDUCED)

        // The reduced open amount is what a payment must match; paying it settles the invoice.
        await s.PayAsync(s.Policy.Total + credit);
        var after = (await s.DocumentsAsync()).Single(d => d.Text("invoice.kind") == "INVOICE");
        after.Text("invoice.state").ShouldBe("PAID");
        Money(after["invoice"]!["open"]).ShouldBe(0m);
        await _slice.AllEntriesBalanceAsync();
    }

    [Fact]
    public async Task REQ_BIL_074_a_credit_that_offsets_the_whole_invoice_reverses_it()
    {
        var s = await CreditScenario.BilledAsync(_slice);
        var (set, _, transaction) = s.ServicingSet("CANCELLATION", "Policyholder", factor: -1m);
        await _slice.InvokeAsync("BIL.PolicyCancelled.StopBilling", s.Cancelled(transaction));
        await s.DeliverAsync(set);

        // The tax stays billed (KEEP_NOT_REDUCED): the invoice is not fully offset, premium items are settled by credit.
        var documents = await s.DocumentsAsync();
        var invoice = documents.Single(d => d.Text("invoice.kind") == "INVOICE");
        invoice["invoiceItems"]!.AsArray().Where(i => i!["chargeCategory"]!.GetValue<string>() != "TAX")
            .ShouldAllBe(i => Money(i!["open"]) == 0m && i["state"]!.GetValue<string>() == "SETTLED");
        invoice["invoiceItems"]!.AsArray().Where(i => i!["chargeCategory"]!.GetValue<string>() == "TAX")
            .ShouldAllBe(i => i!["state"]!.GetValue<string>() == "OPEN");
        invoice.Text("invoice.state").ShouldBe("DUE");
    }

    [Fact]
    public async Task REQ_BIL_320_duplicate_and_replayed_delivery_gives_one_credit_note()
    {
        var s = await CreditScenario.BilledAsync(_slice);
        var (set, _, transaction) = s.ServicingSet("CANCELLATION", "Policyholder", factor: -0.5m);
        var cancelled = s.Cancelled(transaction);
        await _slice.InvokeAsync("BIL.PolicyCancelled.StopBilling", cancelled);
        await s.DeliverAsync(set);
        var before = await CountsAsync();

        await _slice.InvokeAsync("BIL.PolicyCancelled.StopBilling", cancelled, replay: true);
        await s.DeliverAsync(set, replay: true);
        await s.DeliverAsync(set.AsEnumerable().Reverse(), replay: true);
        (await CountsAsync()).ShouldBe(before);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.invoice WHERE kind = 'CREDIT_NOTE' AND policy_id = '{s.Policy.PolicyId}'")).ShouldBe(1);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM cmp.fiscal_document WHERE source_type = 'CREDIT' AND source_id IN (SELECT invoice_id::text FROM bil.invoice WHERE policy_id = '{s.Policy.PolicyId}')")).ShouldBe(1);
    }

    [Fact]
    public async Task PITFALLS_9_the_credit_note_business_reference_is_unique_even_for_a_fresh_credit_note_id()
    {
        var s = await CreditScenario.BilledAsync(_slice);
        var (set, _, _) = s.ServicingSet("CANCELLATION", "Policyholder", factor: -0.5m);
        await s.DeliverAsync(set);

        // A second credit note for the same source transaction and original invoice, with a new id and number: refused.
        var duplicate = await Should.ThrowAsync<PostgresException>(async () => await _slice.ScalarAsync<long>(
            $"""
            WITH ins AS (
              INSERT INTO bil.invoice (invoice_id, legal_entity_id, jurisdiction, billing_account_id, invoice_number, kind, state, policy_id, term_id, transaction_id,
                  issue_date, due_date, method, total, currency, fiscal_status, fiscal_rejection_codes, created_at, created_by, record_version, original_invoice_id)
              SELECT gen_random_uuid(), legal_entity_id, jurisdiction, billing_account_id, 'CN-DUPLICATE', kind, state, policy_id, term_id, transaction_id,
                  issue_date, due_date, method, total, currency, fiscal_status, fiscal_rejection_codes, created_at, created_by, record_version, original_invoice_id
              FROM bil.invoice WHERE kind = 'CREDIT_NOTE' AND policy_id = '{s.Policy.PolicyId}' RETURNING 1)
            SELECT count(*) FROM ins
            """));
        duplicate.SqlState.ShouldBe("23505");
        duplicate.ConstraintName.ShouldBe("ux_credit_note_reference");
    }

    [Fact]
    public async Task REQ_BIL_079_a_tax_delta_that_disagrees_with_the_treatment_is_quarantined_and_nothing_is_billed()
    {
        var s = await CreditScenario.BilledAsync(_slice);

        // A negative IPT delta where the treatment is KEEP_NOT_REDUCED (customer credit NONE).
        var (set, _, transaction) = s.ServicingSet("CANCELLATION", "Policyholder", factor: -0.6m, taxAmount: -5m);
        await _slice.InvokeAsync("BIL.PolicyCancelled.StopBilling", s.Cancelled(transaction));
        await s.DeliverAsync(set);

        (await _slice.ScalarAsync<string>($"SELECT quarantine_reason FROM bil.charge WHERE transaction_id = '{transaction}' AND charge_category = 'TAX'"))
            .ShouldBe("TREATMENT-MISMATCH");
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.invoice WHERE transaction_id = '{transaction}'")).ShouldBe(0);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE entry_type LIKE 'CREDIT%' AND billing_account_id = '{s.AccountId}'")).ShouldBe(0);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.intake_exception WHERE kind = 'BIL-DELTA-SET-BLOCKED' AND subject = '{transaction}'")).ShouldBe(1);
    }

    [Theory]
    [InlineData("rule", "TREATMENT-RULE-MISMATCH")]
    [InlineData("provisional", "PROVISIONAL-FLAG-MISMATCH")]
    [InlineData("missing", "TREATMENT-RULE-MISSING")]
    [InlineData("reduce", "TAX-CREDIT-NOT-SUPPORTED")]
    public async Task REQ_BIL_079_every_other_disagreement_fails_closed(string mutation, string reason)
    {
        var s = await CreditScenario.BilledAsync(_slice);
        var amount = 0m;
        string? rule = null;
        var provisional = true;
        var factor = -0.6m;
        switch (mutation)
        {
            case "rule":
                rule = "GR-TRT-IPT-SOMETHING-ELSE";
                break;
            case "provisional":
                provisional = false;
                break;
            case "missing":
                _treatment.Override = _ => throw new SpiException(new SpiError(SpiErrorCategory.RuleMissing, "RULE_MISSING"), "none");
                break;
            default:
                _treatment.Override = _ => TreatmentAction.ReduceProRata; // the treatment would reduce IPT: the slice has no mechanics, so it fails closed
                amount = -3m;
                rule = "GR-TRT-IPT-OTHER";
                break;
        }

        var (set, _, transaction) = s.ServicingSet("CANCELLATION", "Policyholder", factor, amount, rule, provisional);
        await s.DeliverAsync(set);
        (await _slice.ScalarAsync<string>($"SELECT quarantine_reason FROM bil.charge WHERE transaction_id = '{transaction}' AND charge_category = 'TAX'")).ShouldBe(reason);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.invoice WHERE transaction_id = '{transaction}'")).ShouldBe(0);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry e JOIN bil.ledger_line l ON l.entry_id = e.entry_id WHERE l.transaction_id = '{transaction}' AND e.entry_type LIKE 'CREDIT%'")).ShouldBe(0);
    }

    [Fact]
    public async Task PITFALLS_10_a_credit_without_a_kind_a_source_or_above_what_was_billed_is_quarantined()
    {
        var s = await CreditScenario.BilledAsync(_slice);

        var (noKind, _, noKindTx) = s.ServicingSet("CANCELLATION", "Policyholder", -0.2m);
        foreach (var e in noKind)
        {
            ((JsonObject)e.Payload).Remove("transactionKind");
        }

        await s.DeliverAsync(noKind);
        (await _slice.ScalarAsync<string>($"SELECT quarantine_reason FROM bil.charge WHERE transaction_id = '{noKindTx}' AND charge_category <> 'TAX' LIMIT 1")).ShouldBe("TRANSACTION-KIND-MISSING");

        var (noSource, _, noSourceTx) = s.ServicingSet("CANCELLATION", null, -0.2m);
        await s.DeliverAsync(noSource);
        (await _slice.ScalarAsync<string>($"SELECT quarantine_reason FROM bil.charge WHERE transaction_id = '{noSourceTx}' AND charge_category <> 'TAX' LIMIT 1")).ShouldBe("CANCELLATION-SOURCE-MISSING");

        var (wrongKind, _, wrongKindTx) = s.ServicingSet("ENDORSEMENT_DEBIT", null, -0.2m);
        await s.DeliverAsync(wrongKind);
        (await _slice.ScalarAsync<string>($"SELECT quarantine_reason FROM bil.charge WHERE transaction_id = '{wrongKindTx}' AND charge_category <> 'TAX' LIMIT 1")).ShouldBe("KIND-AMOUNT-MISMATCH");

        var (tooMuch, _, tooMuchTx) = s.ServicingSet("CANCELLATION", "Policyholder", -1.5m);
        await s.DeliverAsync(tooMuch);
        (await _slice.ScalarAsync<string>($"SELECT quarantine_reason FROM bil.charge WHERE transaction_id = '{tooMuchTx}' AND quarantine_reason = 'CREDIT-EXCEEDS-BILLED' LIMIT 1")).ShouldBe("CREDIT-EXCEEDS-BILLED");
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.invoice WHERE kind = 'CREDIT_NOTE' AND policy_id = '{s.Policy.PolicyId}'")).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_BIL_074_two_credits_never_exceed_what_was_billed_and_split_over_the_invoices_newest_first()
    {
        var s = await CreditScenario.BilledAsync(_slice);
        var premium = s.Deltas.Where(d => CreditScenario.CategoryOf(d) != CreditScenario.TaxCategory).ToList();
        var first = premium[0];

        // A mid-term debit on the first premium line, then a credit larger than that debit: it takes the debit first, then the bind.
        var elementOnly = (JsonObject p) => p["chargeId"]!.GetValue<string>() == first.Payload["chargeId"]!.GetValue<string>();
        var (debit, debitTotal, debitTx) = s.ServicingSet("ENDORSEMENT_DEBIT", null, 0.1m, taxAmount: 0m, choose: elementOnly);
        await s.DeliverAsync(debit);
        var documents = await s.DocumentsAsync();
        documents.Count(d => d.Text("invoice.kind") == "INVOICE").ShouldBe(2);

        var (credit, creditTotal, _) = s.ServicingSet("ENDORSEMENT_CREDIT", null, -(0.1m + 0.5m), choose: elementOnly);
        await s.DeliverAsync(credit);
        var notes = (await s.DocumentsAsync()).Where(d => d.Text("invoice.kind") == "CREDIT_NOTE").ToList();
        notes.Count.ShouldBe(2); // one per original invoice (PITFALLS 9: reference = source transaction + original invoice)
        notes.Select(n => n.Text("invoice.originalInvoiceId")).Distinct().Count().ShouldBe(2);
        notes.Sum(n => Money(n["invoice"]!["total"])).ShouldBe(-creditTotal);
        Money(notes.Single(n => n.Text("invoice.originalInvoiceId") != s.InvoiceId)["invoice"]!["total"]).ShouldBe(debitTotal); // the debit is credited back in full first

        await _slice.AllEntriesBalanceAsync();

    }

    [Fact]
    public async Task REQ_BIL_073_a_mid_term_debit_gets_its_own_invoice_with_APPLY_IPT_and_the_servicing_dimensions()
    {
        var s = await CreditScenario.BilledAsync(_slice);
        var (debit, total, transaction) = s.ServicingSet("ENDORSEMENT_DEBIT", null, 0.2m, taxAmount: 7.50m);
        await s.DeliverAsync(debit);

        var documents = await s.DocumentsAsync();
        var invoices = documents.Where(d => d.Text("invoice.kind") == "INVOICE").ToList();
        invoices.Count.ShouldBe(2);
        var second = invoices.Single(d => d.Text("invoice.transactionId") == transaction.ToString());
        Money(second["invoice"]!["total"]).ShouldBe(total + 7.50m * debit.Count(e => CreditScenario.CategoryOf(e) == CreditScenario.TaxCategory));
        second["invoiceItems"]!.AsArray().ShouldAllBe(i => i!["transactionKind"]!.GetValue<string>() == "ENDORSEMENT_DEBIT");
        second["invoiceItems"]!.AsArray().Where(i => i!["chargeCategory"]!.GetValue<string>() == "TAX")
            .ShouldAllBe(i => i!["treatmentRuleId"]!.GetValue<string>() == "GR-TRT-IPT-ENDORSEMENT-DEBIT");
        invoices.Single(d => d.Text("invoice.transactionId") != transaction.ToString()).Text("invoice.invoiceId").ShouldBe(s.InvoiceId);

        // The IPT of the debit follows the same ledger path as new business, with the servicing dimensions on every line.
        await _slice.DrainAsync();
        var posted = (await _slice.EnvelopesAsync(s.AccountId, "BillingEntryPosted"))
            .Where(e => e.BusinessKeys.TryGetValue("transactionId", out var t) && t == transaction.ToString()).ToList();
        posted.Select(e => e.Payload["eventType"]!.GetValue<string>()).Distinct().Order().ShouldBe(["BILLED", "IPT_DUE", "WRITTEN"]);
        foreach (var line in posted.SelectMany(e => e.Payload["lines"]!.AsArray()))
        {
            line!["dimensions"]!["transactionKind"]!.GetValue<string>().ShouldBe("ENDORSEMENT_DEBIT");
        }

        await _slice.AllEntriesBalanceAsync();
    }

    [Fact]
    public async Task REQ_BIL_002_RenewalBound_attaches_term_2_to_the_same_account_and_invoices_it_like_a_bind()
    {
        var s = await CreditScenario.BilledAsync(_slice);
        var term1 = await _slice.ScalarAsync<string>($"SELECT to_char(term_to, 'YYYY-MM-DD') FROM bil.plan_instance WHERE term_id = '{s.Policy.TermId}'");
        var start = DateOnly.ParseExact(term1, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var term2 = Guid.CreateVersion7();
        var transaction = Guid.CreateVersion7();

        var renewal = s.Bound with
        {
            EventId = EventId.New(),
            EventType = EventTypeName.Parse("RenewalBound"),
            Payload = new JsonObject
            {
                ["newTermId"] = term2.ToString(),
                ["newTermNumber"] = 2,
                ["transactionId"] = transaction.ToString(),
                ["productCode"] = s.Bound.Payload["productCode"]!.GetValue<string>(),
                ["productVersion"] = s.Bound.Payload["productVersion"]!.DeepClone(),
                ["artefactHash"] = s.Bound.Payload["artefactHash"]!.GetValue<string>(),
                ["producerOfRecord"] = "DIRECT",
                ["predecessorTermId"] = s.Policy.TermId,
            },
            BusinessKeys = s.Bound.BusinessKeys.With("transactionId", transaction.ToString()).With("newTermId", term2.ToString()),
        };
        (await _slice.InvokeAsync("BIL.RenewalBound.AttachTerm", renewal)).ShouldBeTrue();
        (await _slice.ScalarAsync<string>($"SELECT billing_account_id::text FROM bil.plan_instance WHERE term_id = '{term2}'")).ShouldBe(s.AccountId);
        (await _slice.ScalarAsync<string>($"SELECT predecessor_term_id::text FROM bil.plan_instance WHERE term_id = '{term2}'")).ShouldBe(s.Policy.TermId);

        // Term 2's charges: a renewal term is NEW_BUSINESS; same lines, shifted one year.
        var (set, _, _) = s.ServicingSet("NEW_BUSINESS", null, 1m, taxAmount: 0m, term: term2);
        var taxIndex = 0;
        foreach (var e in set)
        {
            var payload = (JsonObject)e.Payload;
            payload["validPeriod"] = new JsonObject { ["from"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["to"] = start.AddYears(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            if (CreditScenario.CategoryOf(e) == CreditScenario.TaxCategory)
            {
                payload["netAmount"] = new JsonObject { ["amount"] = (10m + taxIndex++).ToString("0.00", CultureInfo.InvariantCulture), ["currency"] = "EUR" };
            }
        }

        await s.DeliverAsync(set);
        var invoice2 = await _slice.ScalarAsync<string>($"SELECT invoice_id::text FROM bil.invoice WHERE term_id = '{term2}' AND kind = 'INVOICE'");
        invoice2.ShouldNotBeNullOrEmpty();
        (await _slice.ScalarAsync<string>($"SELECT billing_account_id::text FROM bil.invoice WHERE invoice_id = '{invoice2}'")).ShouldBe(s.AccountId);
        (await _slice.ScalarAsync<string>($"SELECT invoice_number FROM bil.invoice WHERE invoice_id = '{invoice2}'")).ShouldStartWith("INV");
        (await _slice.ScalarAsync<decimal>($"SELECT total FROM bil.invoice WHERE invoice_id = '{invoice2}'")).ShouldBe(set.Sum(e => decimal.Parse(e.Payload["netAmount"]!["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture)));
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.plan_instance WHERE billing_account_id = '{s.AccountId}'")).ShouldBe(2);

        // Redelivery of RenewalBound changes nothing.
        var before = await CountsAsync();
        (await _slice.InvokeAsync("BIL.RenewalBound.AttachTerm", renewal, replay: true)).ShouldBeTrue();
        (await CountsAsync()).ShouldBe(before);
        await _slice.AllEntriesBalanceAsync();
    }

    [Fact]
    public async Task D_ARC_34_as_the_app_role_credit_entries_are_sealed_and_credit_applications_are_append_only_and_bounded()
    {
        var s = await CreditScenario.BilledAsync(_slice);
        var (set, _, _) = s.ServicingSet("CANCELLATION", "Policyholder", factor: -0.5m);
        await s.DeliverAsync(set);
        var entry = await _slice.ScalarAsync<Guid>($"SELECT entry_id FROM bil.ledger_entry WHERE billing_account_id = '{s.AccountId}' AND entry_type = 'CREDIT_BILLED'");
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);

        // A balanced pair appended to the sealed CREDIT_BILLED entry in a later transaction.
        var sealedError = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var connection = await app.OpenConnectionAsync(Ct);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            await using (var insert = new NpgsqlCommand(
                $"""
                INSERT INTO bil.ledger_line (line_id, entry_id, line_no, account_code, side, amount, currency, rule_id, legal_entity_id, billing_account_id)
                VALUES ('{Guid.CreateVersion7()}', '{entry}', 101, 'LA-01', 'DEBIT', 1000, 'EUR', 'BLR-CREDIT-BILLED', '{ApiHostFactory.LegalEntityId}', '{s.AccountId}'),
                       ('{Guid.CreateVersion7()}', '{entry}', 102, 'LA-02', 'CREDIT', 1000, 'EUR', 'BLR-CREDIT-BILLED', '{ApiHostFactory.LegalEntityId}', '{s.AccountId}');
                """, connection, transaction))
            {
                await insert.ExecuteNonQueryAsync(Ct);
            }

            await transaction.CommitAsync(Ct);
        });
        sealedError.SqlState.ShouldBe("BL005");

        // The unpaid original was offset: applications exist, cannot be changed, and cannot exceed their credit item.
        var application = await _slice.ScalarAsync<Guid>("SELECT credit_application_id FROM bil.credit_application LIMIT 1");
        var update = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var command = app.CreateCommand($"UPDATE bil.credit_application SET amount = amount + 1 WHERE credit_application_id = '{application}'");
            await command.ExecuteNonQueryAsync(Ct);
        });
        update.SqlState.ShouldBeOneOf("BL002", "42501"); // the app role has no UPDATE grant; the trigger refuses every other role
        var over = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var command = app.CreateCommand(
                $"""
                INSERT INTO bil.credit_application (credit_application_id, legal_entity_id, billing_account_id, credit_note_id, credit_item_id, target_kind, target_invoice_id,
                    target_invoice_item_id, amount, currency, actor, recorded_at)
                SELECT gen_random_uuid(), legal_entity_id, billing_account_id, credit_note_id, credit_item_id, target_kind, target_invoice_id, target_invoice_item_id,
                    1, currency, actor, recorded_at FROM bil.credit_application WHERE credit_application_id = '{application}'
                """);
            await command.ExecuteNonQueryAsync(Ct);
        });
        over.SqlState.ShouldBe("BL001");

        // An over-credit item (more than the original item) is refused by the database as well.
        var overCredit = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var command = app.CreateCommand(
                """
                INSERT INTO bil.invoice_item (invoice_item_id, invoice_id, legal_entity_id, charge_id, term_id, transaction_id, element_locator, coverage_code, charge_type,
                    charge_category, provisional, valid_from, amount, currency, state, line_no, credits_item_id)
                SELECT gen_random_uuid(), c.invoice_id, c.legal_entity_id, c.charge_id, c.term_id, c.transaction_id, c.element_locator, c.coverage_code, c.charge_type,
                    c.charge_category, false, c.valid_from, 1000000, c.currency, 'OPEN', 99, c.credits_item_id
                FROM bil.invoice_item c WHERE c.credits_item_id IS NOT NULL LIMIT 1
                """);
            await command.ExecuteNonQueryAsync(Ct);
        });
        overCredit.SqlState.ShouldBeOneOf("BL006", "23505");
    }

    [Fact]
    public async Task REQ_BIL_074_PolicyCancelled_marks_the_term_and_a_later_debit_on_it_is_quarantined()
    {
        var s = await CreditScenario.BilledAsync(_slice);
        var (set, _, transaction) = s.ServicingSet("CANCELLATION", "Policyholder", factor: -0.5m);
        await _slice.InvokeAsync("BIL.PolicyCancelled.StopBilling", s.Cancelled(transaction));
        (await _slice.ScalarAsync<string>($"SELECT cancellation_source FROM bil.plan_instance WHERE term_id = '{s.Policy.TermId}'")).ShouldBe("Policyholder");

        var (debit, _, debitTx) = s.ServicingSet("ENDORSEMENT_DEBIT", null, 0.1m);
        await s.DeliverAsync(debit);
        (await _slice.ScalarAsync<string>($"SELECT quarantine_reason FROM bil.charge WHERE transaction_id = '{debitTx}' AND charge_category <> 'TAX' LIMIT 1")).ShouldBe("TERM-CANCELLED");

    }

    private static string IptPayableSql(string account) =>
        $"SELECT coalesce(sum(CASE side WHEN 'CREDIT' THEN amount ELSE -amount END), 0) FROM bil.ledger_line WHERE billing_account_id = '{account}' AND account_code = 'LA-06'";

    private static string Net(string entryType, string accountCode, string account) =>
        $"SELECT coalesce(sum(CASE l.side WHEN 'DEBIT' THEN l.amount ELSE -l.amount END), 0) FROM bil.ledger_line l JOIN bil.ledger_entry e ON e.entry_id = l.entry_id "
        + $"WHERE e.billing_account_id = '{account}' AND e.entry_type = '{entryType}' AND l.account_code = '{accountCode}'";

    private static readonly string[] Tables =
        ["plan_instance", "billing_account", "invoice", "invoice_item", "ledger_entry", "ledger_line", "allocation", "credit_application", "receipt"];

    private async Task<string> CountsAsync() =>
        "charge=" + await _slice.ScalarAsync<long>("SELECT count(*) FROM bil.charge") + "|"
        + string.Join("|", await Task.WhenAll(Tables.Select(async table => $"{table}={await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.{table}")}")).ConfigureAwait(false))
        + $"|fiscal={await _slice.ScalarAsync<long>("SELECT count(*) FROM cmp.fiscal_document")}";
}
