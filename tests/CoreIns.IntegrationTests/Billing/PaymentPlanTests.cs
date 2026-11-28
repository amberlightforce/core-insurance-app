using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Platform.Events;
using static CoreIns.IntegrationTests.Bil.BillingSlice;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>
/// BO-BIL-03: the plan catalogue, selecting a plan on a term that is not billed yet (the selection is persisted and drives the invoices),
/// refusing a plan change on a billed term, and the billing preview (the NX-EC-11 split, nothing persisted).
/// </summary>
public sealed class PaymentPlanTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private BillingSlice _slice = null!;

    public async ValueTask InitializeAsync()
    {
        _slice = new BillingSlice(database);
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private static decimal Sum(JsonNode? array, string path) =>
        array!.AsArray().Sum(i => Amount(path.Split('.').Aggregate(i, (node, key) => node![key])));

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

    /// <summary>Attaches a copy of a bound policy's term (ANNUAL) without its charges: a plan instance that is not billed yet.</summary>
    private async Task<(string Term, Func<Task> DeliverCharges, decimal Total)> AttachedTermAsync()
    {
        var policy = await _slice.BindAsync();
        var envelopes = await _slice.EnvelopesAsync(policy.PolicyId);
        var bound = envelopes.Single(e => e.EventType.Value == "PolicyBound");
        var deltas = envelopes.Where(e => e.EventType.Value == "ChargeDeltaEmitted").ToList();
        var term = Guid.CreateVersion7().ToString();
        var transaction = Guid.CreateVersion7().ToString();
        (await _slice.InvokeAsync("BIL.PolicyBound.AttachTerm", Retarget(bound, term, transaction))).ShouldBeTrue();

        async Task Deliver()
        {
            for (var i = 0; i < deltas.Count; i++)
            {
                var payload = (JsonObject)Retarget(deltas[i], term, transaction).Payload.DeepClone();
                payload["chargeId"] = Guid.CreateVersion7().ToString();
                var delta = Retarget(deltas[i], term, transaction) with { Payload = payload, Set = new EventSet(Guid.Parse(transaction), deltas.Count, i + 1) };
                (await _slice.InvokeAsync("BIL.ChargeDeltaEmitted.Intake", delta)).ShouldBeTrue();
            }
        }

        return (term, Deliver, policy.Total);
    }

    private static object Select(string term, string plan, int? dueDay = null, string method = "BANK_TRANSFER") => new { termId = term, planCode = plan, method, dueDay };

    [Fact]
    public async Task BO_BIL_03_the_catalogue_lists_the_four_plans_and_is_billing_staff_only()
    {
        var (response, body) = await _slice.GetAsync("/api/bil/v1/payment-plans?product=MOTOR&productVersion=1&channel=STAFF");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["plans"]!.AsArray().Select(p => p!.Text("planCode")).ShouldBe(["ANNUAL", "SEMI_ANNUAL", "QUARTERLY", "MONTHLY"]);
        body["plans"]!.AsArray().Select(p => p!["instalments"]!.GetValue<int>()).ShouldBe([1, 2, 4, 12]);
        body["exclusions"]!.AsArray().Count.ShouldBe(0);

        (await _slice.GetAsync("/api/bil/v1/payment-plans?product=MOTOR")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _slice.GetAsync("/api/bil/v1/payment-plans?product=MOTOR&productVersion=1&channel=STAFF", roles: Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BO_BIL_03_preview_of_a_quote_spreads_the_gross_exactly_and_persists_nothing()
    {
        var before = await _slice.ScalarAsync<long>("SELECT count(*) FROM bil.invoice");
        var request = new
        {
            planCode = "QUARTERLY",
            termFrom = "2027-01-31",
            dueDay = 31,
            charges = new object[]
            {
                new { category = "PREMIUM", amount = new { amount = "1000.01", currency = "EUR" } },
                new { category = "TAX", amount = new { amount = "120.00", currency = "EUR" } },
                new { category = "LEVY", amount = new { amount = "10.03", currency = "EUR" } },
            },
        };
        var (response, body) = await _slice.PostAsync("/api/bil/v1/billing-preview/compute", request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var schedule = body!["fullSchedule"]!.AsArray();
        schedule.Count.ShouldBe(4);
        Sum(schedule, "amount").ShouldBe(1130.04m);
        Amount(body["total"]).ShouldBe(1130.04m);
        Amount(body["firstDue"]!["amount"]).ShouldBe(Amount(schedule[0]!["amount"]));
        schedule.Select(l => l!.Text("dueDate")).ShouldBe(["2027-01-31", "2027-04-30", "2027-07-31", "2027-10-31"]);
        foreach (var category in new[] { "PREMIUM", "TAX", "LEVY" })
        {
            var parts = schedule.Select(l => l!["byCategory"]!.AsArray().Single(c => c!.Text("category") == category)!["amount"]).ToList();
            parts.Sum(Amount).ShouldBe(category switch { "PREMIUM" => 1000.01m, "TAX" => 120.00m, _ => 10.03m });
        }

        (await _slice.ScalarAsync<long>("SELECT count(*) FROM bil.invoice")).ShouldBe(before);

        var bad = await _slice.PostAsync("/api/bil/v1/billing-preview/compute", new { planCode = "WEEKLY", termFrom = "2027-01-01", charges = request.charges });
        bad.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _slice.PostAsync("/api/bil/v1/billing-preview/compute", new { planCode = "MONTHLY" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _slice.PostAsync("/api/bil/v1/billing-preview/compute", request, Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BO_BIL_03_preview_of_a_bound_term_under_another_plan_sums_to_its_gross()
    {
        var policy = await _slice.BindAsync();
        await _slice.DrainAsync();
        var invoice = await _slice.InvoiceOfAsync(policy.PolicyId);
        var invoices = await _slice.ScalarAsync<long>("SELECT count(*) FROM bil.invoice");

        var (response, body) = await _slice.PostAsync("/api/bil/v1/billing-preview/compute", new { termId = policy.TermId, planCode = "MONTHLY" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["fullSchedule"]!.AsArray().Count.ShouldBe(12);
        Sum(body["fullSchedule"], "amount").ShouldBe(policy.Total);
        Amount(body["total"]).ShouldBe(Amount(invoice["invoice"]!["total"]));
        (await _slice.ScalarAsync<long>("SELECT count(*) FROM bil.invoice")).ShouldBe(invoices);

        (await _slice.PostAsync("/api/bil/v1/billing-preview/compute", new { termId = Guid.NewGuid(), planCode = "MONTHLY" })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BO_BIL_03_a_selected_plan_is_persisted_and_the_term_is_billed_in_its_instalments()
    {
        var (term, deliver, total) = await AttachedTermAsync();

        var (response, body) = await _slice.PostAsync("/api/bil/v1/payment-plans/select", Select(term, "QUARTERLY", dueDay: 15));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("planCode").ShouldBe("QUARTERLY");
        body!["schedule"]!.AsArray().Count.ShouldBe(4);
        (await _slice.ScalarAsync<string>($"SELECT plan_code FROM bil.plan_instance WHERE term_id = '{term}'")).ShouldBe("QUARTERLY");
        (await _slice.ScalarAsync<int>($"SELECT due_day FROM bil.plan_instance WHERE term_id = '{term}'")).ShouldBe(15);

        // Selecting again before billing just replaces the choice.
        var (again, _) = await _slice.PostAsync("/api/bil/v1/payment-plans/select", Select(term, "SEMI_ANNUAL"));
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _slice.ScalarAsync<string>($"SELECT plan_code FROM bil.plan_instance WHERE term_id = '{term}'")).ShouldBe("SEMI_ANNUAL");
        (await _slice.PostAsync("/api/bil/v1/payment-plans/select", Select(term, "QUARTERLY", 15))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await deliver();
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.invoice WHERE term_id = '{term}' AND kind = 'INVOICE'")).ShouldBe(4);
        (await _slice.ScalarAsync<decimal>($"SELECT sum(total) FROM bil.invoice WHERE term_id = '{term}'")).ShouldBe(total);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.invoice WHERE term_id = '{term}' AND instalment_no > 1 AND extract(day FROM due_date) = 15")).ShouldBe(3);

        // The schedule the select answered with after the charges arrived is what was billed.
        var (_, preview) = await _slice.PostAsync("/api/bil/v1/billing-preview/compute", new { termId = term, planCode = "QUARTERLY", dueDay = 15 });
        var billed = await _slice.ScalarAsync<string>($"SELECT string_agg(round(total, 2)::text, ',' ORDER BY instalment_no) FROM bil.invoice WHERE term_id = '{term}'");
        string.Join(',', preview!["fullSchedule"]!.AsArray().Select(l => Amount(l!["amount"]).ToString("F2", CultureInfo.InvariantCulture))).ShouldBe(billed);
    }

    [Fact]
    public async Task BO_BIL_03_a_billed_term_refuses_a_new_plan_but_accepts_a_new_due_day_and_bad_input_is_refused()
    {
        var policy = await _slice.BindAsync();
        await _slice.DrainAsync();

        (await _slice.PostAsync("/api/bil/v1/payment-plans/select", Select(policy.TermId, "MONTHLY"))).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _slice.PostAsync("/api/bil/v1/payment-plans/change", Select(policy.TermId, "MONTHLY"))).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _slice.ScalarAsync<string>($"SELECT plan_code FROM bil.plan_instance WHERE term_id = '{policy.TermId}'")).ShouldBe("ANNUAL");

        var (response, body) = await _slice.PostAsync("/api/bil/v1/payment-plans/change", Select(policy.TermId, "ANNUAL", 10, "CASHIER"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        (await _slice.ScalarAsync<string>($"SELECT method FROM bil.plan_instance WHERE term_id = '{policy.TermId}'")).ShouldBe("CASHIER");

        (await _slice.PostAsync("/api/bil/v1/payment-plans/change", Select(policy.TermId, "WEEKLY"))).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _slice.PostAsync("/api/bil/v1/payment-plans/change", Select(policy.TermId, "ANNUAL", method: "CARD"))).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _slice.PostAsync("/api/bil/v1/payment-plans/change", Select(policy.TermId, "ANNUAL", 40))).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _slice.PostAsync("/api/bil/v1/payment-plans/select", Select(Guid.NewGuid().ToString(), "MONTHLY"))).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _slice.PostAsync("/api/bil/v1/payment-plans/select", Select(policy.TermId, "ANNUAL"), Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
