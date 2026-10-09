using System.Net;
using System.Text.Json.Nodes;
using CoreIns.SharedKernel;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Change;

/// <summary>
/// SL3-POL-READS on PostgreSQL 17: <c>pol.Term.timeline</c>, the term list on <c>pol.Policy.get</c> and <c>effectiveKnownAt</c> on
/// <c>pol.Term.get</c>, over a policy bound, changed (through the change commands) and cancelled/renewed (simulated with SQL under
/// the policy watermark: the cancellation and renewal commands belong to other work packages). Amounts are ILLUSTRATIVE TEST DATA.
/// </summary>
public sealed class TermTimelineTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ChangeHarness _h = null!;

    public async ValueTask InitializeAsync()
    {
        _h = new ChangeHarness(database);
        await _h.InitializeAsync();
    }

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private async Task<JsonNode> GetAsync(string path, string roles = Underwriter, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (response, body) = await _h.SendAsync(HttpMethod.Get, path, roles: roles);
        response.StatusCode.ShouldBe(expected, body?.ToJsonString());
        return body!;
    }

    /// <summary>The response instant equals the database time to the microsecond (the wire drops zero fractions).</summary>
    private static void ShouldBeInstant(string actual, DateTime expected) =>
        DateTime.Parse(actual, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal).ShouldBe(DateTime.SpecifyKind(expected, DateTimeKind.Utc));

    private static string Q(DateTime utc) => Uri.EscapeDataString(ChangeHarness.Iso(utc));

    /// <summary>Binds a policy, changes the engine capacity at day 200 (a bound CHANGE, sequence 2) and returns it.</summary>
    private async Task<Issued> IssueAndChangeAsync()
    {
        var policy = await _h.IssueAsync();
        _h.SetDay(policy, 200);
        var jobId = await _h.NewChangeAsync(policy);
        await _h.EditVehicleAsync(jobId, policy, capacity: 1600);
        (await _h.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (bound, bind) = await _h.BindAsync(jobId);
        bound.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        return policy;
    }

    /// <summary>A cancellation transaction (sequence 3) written under a moved-forward policy watermark, as the cancellation command would.</summary>
    private async Task SimulateCancellationAsync(Issued policy, decimal premium)
    {
        await _h.ExecuteAsync(
            $"""
            WITH w AS (UPDATE pol.policy SET last_recorded_at = last_recorded_at + interval '1 second', record_version = record_version + 1
                        WHERE policy_id = '{policy.PolicyId}' RETURNING last_recorded_at)
            INSERT INTO pol.policy_transaction (transaction_id, policy_id, term_id, job_id, legal_entity_id, kind, sequence, effective_at, recorded_at,
                configuration_hash, artefact_hash, resolution_hash, intent, premium, taxes, total, currency, actor, correlation_id, origin)
            SELECT gen_random_uuid(), policy_id, term_id, gen_random_uuid(), legal_entity_id, 'CANCELLATION', 3, effective_at + interval '1 day', w.last_recorded_at,
                configuration_hash, artefact_hash, resolution_hash, intent, {premium}, 0, {premium}, currency, actor, correlation_id, origin
              FROM pol.policy_transaction, w WHERE term_id = '{policy.TermId}' AND sequence = 1;
            """);
    }

    [Fact]
    public async Task REQ_POL_002_085_timeline_orders_bind_change_and_cancel_with_kinds_and_signed_premium()
    {
        var policy = await IssueAndChangeAsync();
        await SimulateCancellationAsync(policy, -42.5m);
        _h.SetDay(policy, 250);

        var body = await GetAsync($"/api/pol/v1/terms/timeline?policyId={policy.PolicyId}&validAt={Q(policy.Start.AddDays(30))}");

        body.Text("policy.policyId").ShouldBe(policy.PolicyId);
        body.Text("term.termId").ShouldBe(policy.TermId);
        var transactions = body["transactions"]!.AsArray();
        transactions.Select(t => t!.Text("kind")).ShouldBe(["ISSUANCE", "CHANGE", "CANCELLATION"]);
        transactions.Select(t => t!["sequence"]!.GetValue<int>()).ShouldBe([1, 2, 3]);
        transactions[0]!.Text("transactionId").ShouldBe(policy.IssuanceTransactionId);
        transactions[2]!.Text("premiumChange.amount").ShouldBe("-42.5");
        transactions[2]!.Text("premiumChange.currency").ShouldBe("EUR");
        transactions.ShouldAllBe(t => t!["reversed"]!.GetValue<bool>() == false);
        // Recorded time rises with the sequence; effective time is the valid time of each transaction.
        var recorded = transactions.Select(t => DateTime.Parse(t!.Text("recordedAt"), null, System.Globalization.DateTimeStyles.AdjustToUniversal)).ToList();
        recorded.ShouldBe([.. recorded.Order()]);
        body["effectiveKnownAt"].ShouldNotBeNull();

        // The same answer for an explicit termId.
        var byTerm = await GetAsync($"/api/pol/v1/terms/timeline?policyId={policy.PolicyId}&termId={policy.TermId}");
        byTerm["transactions"]!.AsArray().Count.ShouldBe(3);
    }

    [Fact]
    public async Task REQ_POL_085_D_SL3_03_known_at_is_clamped_to_the_watermark_and_hides_later_transactions()
    {
        var policy = await _h.IssueAsync();
        var afterIssue = await _h.ScalarAsync<DateTime>($"SELECT last_recorded_at FROM pol.policy WHERE policy_id = '{policy.PolicyId}'");
        _h.SetDay(policy, 200);
        var jobId = await _h.NewChangeAsync(policy);
        await _h.EditVehicleAsync(jobId, policy, capacity: 1600);
        (await _h.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _h.BindAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var watermark = await _h.ScalarAsync<DateTime>($"SELECT last_recorded_at FROM pol.policy WHERE policy_id = '{policy.PolicyId}'");
        _h.SetDay(policy, 260);

        // A knownAt after the watermark (but not in the future) answers at the watermark.
        var ahead = await GetAsync($"/api/pol/v1/terms/timeline?policyId={policy.PolicyId}&knownAt={Q(watermark.AddDays(5))}&validAt={Q(policy.Start.AddDays(10))}");
        ShouldBeInstant(ahead.Text("effectiveKnownAt"), watermark);
        ahead["transactions"]!.AsArray().Count.ShouldBe(2);

        // A knownAt before the change was recorded is kept as given and shows the issuance only.
        var before = await GetAsync($"/api/pol/v1/terms/timeline?policyId={policy.PolicyId}&knownAt={Q(afterIssue)}&validAt={Q(policy.Start.AddDays(10))}");
        ShouldBeInstant(before.Text("effectiveKnownAt"), afterIssue);
        before["transactions"]!.AsArray().Select(t => t!.Text("kind")).ShouldBe(["ISSUANCE"]);

        // Before the policy existed: nothing is known.
        await GetAsync($"/api/pol/v1/terms/timeline?policyId={policy.PolicyId}&knownAt={Q(afterIssue.AddDays(-1))}", expected: HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task REQ_POL_002_term_get_carries_the_effective_known_at()
    {
        var policy = await IssueAndChangeAsync();
        var watermark = await _h.ScalarAsync<DateTime>($"SELECT last_recorded_at FROM pol.policy WHERE policy_id = '{policy.PolicyId}'");
        _h.SetDay(policy, 260);

        var body = await GetAsync($"/api/pol/v1/terms/{policy.TermId}?knownAt={Q(watermark.AddDays(3))}");
        ShouldBeInstant(body.Text("effectiveKnownAt"), watermark);

        var policyBody = await GetAsync($"/api/pol/v1/policies/{policy.PolicyId}");
        ShouldBeInstant(policyBody.Text("effectiveKnownAt"), watermark);
    }

    [Fact]
    public async Task REQ_POL_002_policy_get_lists_every_term_across_a_renewal()
    {
        var policy = await _h.IssueAsync();
        _h.SetDay(policy, 300);
        var beforeRenewal = await _h.ScalarAsync<DateTime>($"SELECT last_recorded_at FROM pol.policy WHERE policy_id = '{policy.PolicyId}'");

        // The renewal command is another work package: a second term (number 2, Scheduled) written under the policy watermark.
        var secondTerm = Guid.NewGuid();
        await _h.ExecuteAsync(
            $"""
            WITH w AS (UPDATE pol.policy SET last_recorded_at = last_recorded_at + interval '1 second', record_version = record_version + 1
                        WHERE policy_id = '{policy.PolicyId}' RETURNING last_recorded_at)
            INSERT INTO pol.policy_term (term_version_id, term_id, policy_id, legal_entity_id, term_number, valid_from, valid_to, recorded_from, recorded_to, state,
                product_version, artefact_hash, rating_artefact_hash, resolution_hash, configuration_hash, currency, producer_code, payment_plan_ref, written_date,
                head_transaction_id, created_by, predecessor_term_id)
            SELECT gen_random_uuid(), '{secondTerm}', policy_id, legal_entity_id, 2, valid_to, valid_to + interval '365 days', w.last_recorded_at, NULL, 'SCHEDULED',
                product_version, artefact_hash, rating_artefact_hash, resolution_hash, configuration_hash, currency, producer_code, payment_plan_ref, written_date,
                head_transaction_id, created_by, term_id
              FROM pol.policy_term, w WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL;
            """);
        _h.SetDay(policy, 301);

        // During the first term: two terms, the first in force, the second scheduled; `term` stays the one valid at validAt.
        var during = await GetAsync($"/api/pol/v1/policies/{policy.PolicyId}?validAt={Q(policy.Start.AddDays(100))}");
        var terms = during["terms"]!.AsArray();
        terms.Select(t => t!["termNumber"]!.GetValue<int>()).ShouldBe([1, 2]);
        terms.Select(t => t!.Text("state")).ShouldBe(["IN_FORCE", "SCHEDULED"]);
        terms[0]!.Text("termId").ShouldBe(policy.TermId);
        terms[1]!.Text("termId").ShouldBe(secondTerm.ToString());
        terms[1]!.Text("period.from").ShouldBe(terms[0]!.Text("period.to"));
        during.Text("term.termId").ShouldBe(policy.TermId);

        // After the first term ended: expired, then in force.
        var after = await GetAsync($"/api/pol/v1/policies/{policy.PolicyId}?validAt={Q(policy.End.AddDays(5))}");
        after["terms"]!.AsArray().Select(t => t!.Text("state")).ShouldBe(["EXPIRED", "IN_FORCE"]);
        after.Text("term.termId").ShouldBe(secondTerm.ToString());

        // Known before the renewal was recorded: one term only.
        var known = await GetAsync($"/api/pol/v1/policies/{policy.PolicyId}?validAt={Q(policy.Start.AddDays(100))}&knownAt={Q(beforeRenewal)}");
        known["terms"]!.AsArray().Count.ShouldBe(1);

        // The timeline of the renewed term, by termId (no transactions yet).
        var timeline = await GetAsync($"/api/pol/v1/terms/timeline?policyId={policy.PolicyId}&termId={secondTerm}");
        timeline.Text("term.termNumber").ShouldBe("2");
        timeline["transactions"]!.AsArray().Count.ShouldBe(0);

        // A term of another policy is not found within this policy.
        var other = await _h.IssueAsync();
        await GetAsync($"/api/pol/v1/terms/timeline?policyId={policy.PolicyId}&termId={other.TermId}", expected: HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task REQ_POL_085_timeline_is_403_without_the_grant_and_open_to_billing_and_claims_handlers()
    {
        var policy = await _h.IssueAsync();
        _h.SetDay(policy, 1);
        var path = $"/api/pol/v1/terms/timeline?policyId={policy.PolicyId}";

        await GetAsync(path, ChangeHarness.Csr, HttpStatusCode.Forbidden);
        await GetAsync(path, ChangeHarness.NoChange, HttpStatusCode.Forbidden);
        foreach (var role in new[] { "Staff.Underwriter", "Staff.Billing", "Staff.ClaimsHandler" })
        {
            await GetAsync(path, role);
        }

        await GetAsync("/api/pol/v1/terms/timeline", expected: HttpStatusCode.UnprocessableEntity);
    }
}
