using System.Text.Json.Nodes;
using CoreIns.Platform.Events;
using static CoreIns.IntegrationTests.Bil.BillingSlice;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil.Credits;

/// <summary>
/// SL6-BIL-RESPREAD: an out-of-sequence delta set (<c>correctionOf</c> / <c>oosSetId</c>) is billed as one correction: the paid
/// invoice is untouched, the difference falls on a new invoice or a credit note, the set's ledger entries are published as one
/// D4 set carrying the correction metadata (so FIN posts one balanced journal per set), and a replay changes nothing.
/// Amounts are illustrative test data.
/// </summary>
public sealed class RespreadTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private BillingSlice _slice = null!;
    private readonly FakeTaxCalculator _treatment = new();

    public async ValueTask InitializeAsync()
    {
        _slice = new BillingSlice(database, services => services.WithFakeTreatment(_treatment));
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private static (Guid Corrected, Guid OosSet) Tag(IEnumerable<EventEnvelope> set)
    {
        var corrected = Guid.CreateVersion7();
        var oosSet = Guid.CreateVersion7();
        foreach (var delta in set)
        {
            var payload = (JsonObject)delta.Payload;
            payload["correctionOf"] = corrected.ToString();
            payload["oosSetId"] = oosSet.ToString();
        }

        return (corrected, oosSet);
    }

    [Fact]
    public async Task A_credit_set_on_a_paid_invoice_leaves_it_untouched_credits_the_difference_and_publishes_one_set()
    {
        var s = await CreditScenario.BilledAsync(_slice);
        await s.PayAsync(s.Policy.Total);
        var (set, credit, transaction) = s.ServicingSet("ENDORSEMENT_CREDIT", null, -0.2m);
        var (corrected, oosSet) = Tag(set);
        await s.DeliverAsync(set);

        // The paid invoice is unchanged; the difference is on a credit note; the schedule sums to the new premium.
        var documents = await s.DocumentsAsync();
        documents.Single(d => d.Text("invoice.kind") == "INVOICE").Text("invoice.state").ShouldBe("PAID");
        var note = documents.Single(d => d.Text("invoice.kind") == "CREDIT_NOTE");
        Amount(note["invoice"]!["total"]).ShouldBe(-credit);

        var row = $"FROM bil.respread WHERE set_id = '{transaction}'";
        (await _slice.ScalarAsync<long>("SELECT count(*) " + row)).ShouldBe(1);
        (await _slice.ScalarAsync<string>($"SELECT oos_set_id::text || ':' || correction_of::text {row}")).ShouldBe($"{oosSet}:{corrected}");
        (await _slice.ScalarAsync<decimal>("SELECT credited_amount " + row)).ShouldBe(-credit);
        (await _slice.ScalarAsync<decimal>("SELECT invoiced_amount " + row)).ShouldBe(0m);
        (await _slice.ScalarAsync<long>("SELECT paid_invoices_untouched " + row)).ShouldBe(1);
        (await _slice.ScalarAsync<decimal>("SELECT net_amount " + row)).ShouldBe(credit);

        // Every entry of the set is a member of one D4 set (size = number of entries) and carries the correction keys.
        await _slice.DrainAsync();
        var posted = (await _slice.EnvelopesAsync(s.AccountId, "BillingEntryPosted"))
            .Where(e => e.BusinessKeys.TryGetValue("oosSetId", out _)).ToList();
        posted.Count.ShouldBeGreaterThan(1);
        posted.ShouldAllBe(e => e.Set != null && e.Set.SetId == transaction && e.Set.Size == posted.Count
            && e.BusinessKeys["correctionOf"] == corrected.ToString() && e.BusinessKeys["oosSetId"] == oosSet.ToString());
        posted.Select(e => e.Set!.Index).Order().ShouldBe(Enumerable.Range(1, posted.Count));
        await _slice.AllEntriesBalanceAsync();

        // Replay: no new correction, entry or document.
        var entries = await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE billing_account_id = '{s.AccountId}'");
        await s.DeliverAsync(set, replay: true);
        (await _slice.ScalarAsync<long>("SELECT count(*) " + row)).ShouldBe(1);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE billing_account_id = '{s.AccountId}'")).ShouldBe(entries);
        (await s.DocumentsAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_debit_set_on_an_unpaid_term_bills_the_difference_as_one_new_invoice_and_the_first_stays_open()
    {
        var s = await CreditScenario.BilledAsync(_slice);
        var (set, total, transaction) = s.ServicingSet("ENDORSEMENT_DEBIT", null, 0.2m, taxAmount: 7.50m);
        Tag(set);
        await s.DeliverAsync(set);

        var documents = await s.DocumentsAsync();
        documents.Count.ShouldBe(2);
        documents.Count(d => d.Text("invoice.state") == "DUE").ShouldBe(2);
        var second = documents.Single(d => d.Text("invoice.transactionId") == transaction.ToString());
        var row = $"FROM bil.respread WHERE set_id = '{transaction}'";
        (await _slice.ScalarAsync<decimal>("SELECT invoiced_amount " + row)).ShouldBe(Amount(second["invoice"]!["total"]));
        (await _slice.ScalarAsync<decimal>("SELECT invoiced_amount " + row)).ShouldBe(total + 7.50m * set.Count(e => CreditScenario.CategoryOf(e) == CreditScenario.TaxCategory));
        (await _slice.ScalarAsync<long>("SELECT paid_invoices_untouched " + row)).ShouldBe(0);

        // The entries of the set (written, billed, ...) are one D4 set.
        await _slice.DrainAsync();
        var posted = (await _slice.EnvelopesAsync(s.AccountId, "BillingEntryPosted")).Where(e => e.BusinessKeys.TryGetValue("oosSetId", out _)).ToList();
        posted.ShouldAllBe(e => e.Set != null && e.Set.SetId == transaction && e.Set.Size == posted.Count);
        posted.Select(e => e.Payload["eventType"]!.GetValue<string>()).ShouldContain("WRITTEN");
        posted.Select(e => e.Payload["eventType"]!.GetValue<string>()).ShouldContain("BILLED");
        await _slice.AllEntriesBalanceAsync();
    }
}
