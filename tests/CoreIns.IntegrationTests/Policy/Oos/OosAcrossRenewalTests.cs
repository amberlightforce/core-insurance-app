using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Policy.Change;
using CoreIns.SharedKernel;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Oos;

/// <summary>
/// NX-EC-05 on PostgreSQL 17 (change harness: RAT scripted as a function of engine capacity and value, manual clock): an out-of-sequence
/// change whose term has already been renewed. The correction stays in the term it belongs to (its charge lines, deltas and segments), term 2
/// is not written at all, the terms never overlap and stay contiguous, the delta set the downstream consumers (BIL respread, RI cession, FIN
/// accrual) read is one complete NET set that sums to the premium difference, and an open renewal offer is made stale (rebase) by it.
/// The BIL / RI / FIN consumers of that set are covered by RespreadTests, CessionDeltaTests and OosGuardTests (TODO(hardening): one
/// host with every consumer real). Every amount is ILLUSTRATIVE TEST DATA.
/// </summary>
public sealed class OosAcrossRenewalTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ChangeHarness _h = null!;

    public async ValueTask InitializeAsync()
    {
        _h = new ChangeHarness(database);
        await _h.InitializeAsync();
    }

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private async Task<string> ChangeAsync(Issued policy, int capacity, string value)
    {
        var job = await _h.NewChangeAsync(policy);
        await _h.EditVehicleAsync(job, policy, capacity, value);
        (await _h.QuoteAsync(job)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (response, bind) = await _h.BindAsync(job);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        return bind.Text("transactionId");
    }

    private async Task<(string JobId, JsonNode? Offer)> RenewAndOfferAsync(Issued policy)
    {
        var (created, create) = await _h.SendAsync(HttpMethod.Post, "/api/pol/v1/renewals", new { termId = policy.TermId });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, create?.ToJsonString());
        var jobId = create.Text("jobId");
        var (offered, offer) = await OfferAsync(policy, jobId);
        offered.StatusCode.ShouldBe(HttpStatusCode.OK, offer?.ToJsonString());
        return (jobId, offer);
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> OfferAsync(Issued policy, string jobId) =>
        _h.SendAsync(HttpMethod.Post, "/api/pol/v1/renewals/offer", new { jobId, termId = policy.TermId });

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> AcceptAsync(Issued policy, string jobId)
    {
        _h.Clock.Set(Instant.FromDateTimeOffset(_h.Clock.Now.ToDateTimeOffset().AddMinutes(5)));
        return await _h.SendAsync(
            HttpMethod.Post, "/api/pol/v1/renewals/accept",
            new { jobId, termId = policy.TermId, channel = "STAFF", acceptedAt = ChangeHarness.Iso(_h.Clock.Now.ToUtcDateTime()) });
    }

    private async Task<string> RenewAsync(Issued policy)
    {
        var (jobId, _) = await RenewAndOfferAsync(policy);
        var (accepted, accept) = await AcceptAsync(policy, jobId);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, accept?.ToJsonString());
        accept.Text("newTermNumber").ShouldBe("2");
        return accept.Text("newTermId");
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> StartOosAsync(Issued policy, DateTime effective) =>
        await _h.SendAsync(
            HttpMethod.Post, "/api/pol/v1/policy-changes/out-of-sequence",
            new { policyId = policy.PolicyId, effectiveAt = ChangeHarness.Iso(DateTime.SpecifyKind(effective, DateTimeKind.Utc)) });

    /// <summary>Starts, edits, quotes and binds an out-of-sequence change to the given vehicle fields.</summary>
    private async Task<string> OosAsync(Issued policy, int day, int capacity, string value)
    {
        var (started, body) = await StartOosAsync(policy, policy.Start.AddDays(day));
        started.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        var job = body.Text("jobId");
        await _h.EditVehicleAsync(job, policy, capacity, value);
        (await _h.QuoteAsync(job)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (bound, bind) = await _h.BindAsync(job);
        bound.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        return bind.Text("transactionId");
    }

    private Task<string> PremiumByChargeTypeAsync(string policyId, string termId) => _h.ScalarAsync<string>(
        $"SELECT string_agg(charge_type || '=' || total::text, ',' ORDER BY charge_type) FROM (SELECT charge_type, sum(amount) AS total FROM pol.charge_line WHERE policy_id = '{policyId}' AND term_id = '{termId}' AND charge_category = 'PREMIUM' GROUP BY charge_type) t");

    private Task<string> RiskByPeriodAsync(string policyId, string termId) => _h.ScalarAsync<string>(
        $"SELECT string_agg((snapshot->'vehicles'->0->>'engineCapacityCc') || '/' || (snapshot->'vehicles'->0->'value'->>'amount'), ' ; ' ORDER BY valid_from) FROM pol.segment WHERE policy_id = '{policyId}' AND term_id = '{termId}' AND recorded_to IS NULL");

    private Task<decimal> TermPremiumAsync(string termId) =>
        _h.ScalarAsync<decimal>($"SELECT sum(amount) FROM pol.charge_line WHERE term_id = '{termId}' AND charge_category = 'PREMIUM'");

    /// <summary>Everything ever written for the term (lines, segments, term versions, transactions) as one digest: equal digests mean nothing was written.</summary>
    private Task<string> DigestAsync(string termId) => _h.ScalarAsync<string>(
        $"""
        SELECT md5(coalesce((SELECT string_agg(c::text, ',' ORDER BY c::text) FROM pol.charge_line c WHERE c.term_id = '{termId}'), '')
                || '|' || coalesce((SELECT string_agg(s::text, ',' ORDER BY s::text) FROM pol.segment s WHERE s.term_id = '{termId}'), '')
                || '|' || coalesce((SELECT string_agg(t::text, ',' ORDER BY t::text) FROM pol.policy_term t WHERE t.term_id = '{termId}'), '')
                || '|' || coalesce((SELECT string_agg(x::text, ',' ORDER BY x::text) FROM pol.policy_transaction x WHERE x.term_id = '{termId}'), ''))
        """);

    private async Task AssertTermsContiguousAsync(string policyId, string term1, string term2)
    {
        (await _h.ScalarAsync<long>(
            $"SELECT count(*) FROM pol.policy_term a JOIN pol.policy_term b ON a.policy_id = b.policy_id AND a.term_id < b.term_id AND a.recorded_to IS NULL AND b.recorded_to IS NULL AND a.valid_from < b.valid_to AND b.valid_from < a.valid_to WHERE a.policy_id = '{policyId}'"))
            .ShouldBe(0, "terms overlap");
        (await _h.ScalarAsync<bool>(
            $"SELECT (SELECT valid_to FROM pol.policy_term WHERE term_id = '{term1}' AND recorded_to IS NULL) = (SELECT valid_from FROM pol.policy_term WHERE term_id = '{term2}' AND recorded_to IS NULL)"))
            .ShouldBeTrue("terms are not contiguous");
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.policy_term WHERE policy_id = '{policyId}' AND recorded_to IS NULL")).ShouldBe(2);
    }

    /// <summary>The OOS delta set as the downstream consumers read it: complete, one set, NET, every line in the corrected term, summing to the premium difference.</summary>
    private async Task AssertDeltaSetAsync(string transaction, string term, string correctionOf, decimal premiumDifference)
    {
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.charge_line WHERE transaction_id = '{transaction}' AND term_id <> '{term}'")).ShouldBe(0);
        (await _h.ScalarAsync<decimal>($"SELECT sum(amount) FROM pol.charge_line WHERE transaction_id = '{transaction}' AND charge_category = 'PREMIUM'")).ShouldBe(premiumDifference);
        var lines = await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.charge_line WHERE transaction_id = '{transaction}'");
        lines.ShouldBeGreaterThan(0);
        var events = $"FROM plt.outbox_message WHERE event_type = 'ChargeDeltaEmitted' AND set_id = '{transaction}'";
        (await _h.ScalarAsync<long>($"SELECT count(*) {events}")).ShouldBe(lines);
        (await _h.ScalarAsync<long>($"SELECT count(*) {events} AND payload->>'correctionOf' = '{correctionOf}' AND payload->>'deltaKind' = 'NET' AND payload->>'oosSetId' IS NOT NULL")).ShouldBe(lines);
        (await _h.ScalarAsync<long>($"SELECT count(DISTINCT payload->>'oosSetId') {events}")).ShouldBe(1);
        (await _h.ScalarAsync<long>($"SELECT count(DISTINCT set_size) {events}")).ShouldBe(1);
        (await _h.ScalarAsync<int>($"SELECT max(set_size) {events}")).ShouldBe((int)lines);
    }

    /// <summary>Term 1 with rate edits at two days, renewed (offer + acceptance) at a third; the clock is left at the renewal day.</summary>
    private async Task<(Issued Policy, string Term2, string First, string Second)> RenewedPolicyAsync(int changeA, int changeB, int renewDay)
    {
        var policy = await _h.IssueAsync();
        _h.SetDay(policy, changeA);
        var first = await ChangeAsync(policy, 1400, "16000.00");
        _h.SetDay(policy, changeB);
        var second = await ChangeAsync(policy, 1400, "17000.00");
        _h.SetDay(policy, renewDay);
        var term2 = await RenewAsync(policy);
        return (policy, term2, first, second);
    }

    [Fact]
    public async Task NX_EC_05_an_oos_change_in_a_renewed_term_corrects_term_one_only_and_equals_the_in_order_result()
    {
        // Reference: the same three changes in order on a policy that was never renewed (built first: the shared manual clock only moves forward).
        var inOrder = await _h.IssueAsync();
        _h.SetDay(inOrder, 312);
        await ChangeAsync(inOrder, 1600, "15000.00");
        _h.SetDay(inOrder, 315);
        await ChangeAsync(inOrder, 1600, "16000.00");
        _h.SetDay(inOrder, 325);
        await ChangeAsync(inOrder, 1600, "17000.00");

        var (policy, term2, first, _) = await RenewedPolicyAsync(315, 325, 330);
        _h.SetDay(policy, 335);
        var renewalBound = await _h.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'RenewalBound' AND aggregate_id = '{policy.PolicyId}'");
        renewalBound.ShouldBe(1);
        var term2Before = await DigestAsync(term2);
        var term2Premium = await PremiumByChargeTypeAsync(policy.PolicyId, term2);
        var term1Before = await TermPremiumAsync(policy.TermId);

        var oos = await OosAsync(policy, 312, 1600, "15000.00");

        // Term 1 now reads like the in-order policy, per charge type and per period.
        (await PremiumByChargeTypeAsync(policy.PolicyId, policy.TermId)).ShouldBe(await PremiumByChargeTypeAsync(inOrder.PolicyId, inOrder.TermId));
        (await RiskByPeriodAsync(policy.PolicyId, policy.TermId)).ShouldBe(await RiskByPeriodAsync(inOrder.PolicyId, inOrder.TermId));
        (await RiskByPeriodAsync(policy.PolicyId, policy.TermId)).ShouldBe("1400/15000.00 ; 1600/15000.00 ; 1600/16000.00 ; 1600/17000.00");

        // Term 2 was not written at all (no line, segment, term version or transaction), keeps its premium, and the renewal is not re-announced.
        (await DigestAsync(term2)).ShouldBe(term2Before);
        (await PremiumByChargeTypeAsync(policy.PolicyId, term2)).ShouldBe(term2Premium);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'RenewalBound' AND aggregate_id = '{policy.PolicyId}'")).ShouldBe(renewalBound);
        await AssertTermsContiguousAsync(policy.PolicyId, policy.TermId, term2);

        // The one NET set (the input of BIL respread, RI cession and FIN accrual) lies in term 1 and sums to the premium correction.
        var term1After = await TermPremiumAsync(policy.TermId);
        term1After.ShouldNotBe(term1Before);
        await AssertDeltaSetAsync(oos, policy.TermId, first, term1After - term1Before);
        (await _h.ScalarAsync<string>($"SELECT mode || '/' || status || '/' || term_id FROM pol.oos_correction WHERE transaction_id = '{oos}'")).ShouldBe($"NET/APPLIED/{policy.TermId}");

        // Term 1's segments tile the term with no gap and no overlap.
        (await _h.ScalarAsync<bool>(
            $"SELECT bool_and(valid_from = prev_to) FROM (SELECT valid_from, lag(valid_to) OVER (ORDER BY valid_from) AS prev_to FROM pol.segment WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL) t WHERE prev_to IS NOT NULL"))
            .ShouldBeTrue();
        (await _h.ScalarAsync<bool>(
            $"SELECT min(valid_from) = (SELECT valid_from FROM pol.policy_term WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL) AND max(valid_to) = (SELECT valid_to FROM pol.policy_term WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL) FROM pol.segment WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task NX_EC_05_an_oos_change_after_term_two_has_started_still_corrects_term_one()
    {
        var (policy, term2, _, second) = await RenewedPolicyAsync(300, 360, 362);
        _h.SetDay(policy, 380);
        var term2Before = await DigestAsync(term2);
        var before = await TermPremiumAsync(policy.TermId);

        // Base at day 352 is 1400/16000: only the engine capacity changes.
        var oos = await OosAsync(policy, 352, 1600, "16000.00");

        await AssertDeltaSetAsync(oos, policy.TermId, second, await TermPremiumAsync(policy.TermId) - before);
        (await DigestAsync(term2)).ShouldBe(term2Before);
        await AssertTermsContiguousAsync(policy.PolicyId, policy.TermId, term2);
        (await RiskByPeriodAsync(policy.PolicyId, policy.TermId)).ShouldBe("1400/15000.00 ; 1400/16000.00 ; 1600/16000.00 ; 1600/17000.00");
    }

    [Fact]
    public async Task NX_EC_05_a_change_effective_at_the_term_boundary_lands_in_term_two_and_leaves_term_one_alone()
    {
        var (policy, term2, _, _) = await RenewedPolicyAsync(315, 325, 330);
        _h.SetDay(policy, 335);
        var term1Before = await DigestAsync(policy.TermId);

        // The instant term 1 ends is term 2's first instant (half-open terms): the job is on term 2.
        var (started, body) = await StartOosAsync(policy, policy.End);
        started.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        var job = body.Text("jobId");
        (await _h.ScalarAsync<string>($"SELECT target_term_id::text FROM pol.job WHERE job_id = '{job}'")).ShouldBe(term2);
        await _h.EditVehicleAsync(job, policy, 1800, "17000.00");
        (await _h.QuoteAsync(job)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (bound, bind) = await _h.BindAsync(job);
        bound.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());

        (await DigestAsync(policy.TermId)).ShouldBe(term1Before);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.charge_line WHERE transaction_id = '{bind.Text("transactionId")}' AND term_id <> '{term2}'")).ShouldBe(0);
        await AssertTermsContiguousAsync(policy.PolicyId, policy.TermId, term2);
    }

    [Fact]
    public async Task NX_EC_05_an_open_renewal_offer_is_made_stale_by_an_oos_correction_and_rebases_to_the_corrected_risk()
    {
        var policy = await _h.IssueAsync();
        _h.SetDay(policy, 315);
        await ChangeAsync(policy, 1400, "16000.00");
        _h.SetDay(policy, 325);
        await ChangeAsync(policy, 1400, "17000.00");
        _h.SetDay(policy, 330);
        var (jobId, offer) = await RenewAndOfferAsync(policy);
        var firstPremium = decimal.Parse(offer.Text("premiumSummary.premium.amount"), CultureInfo.InvariantCulture);

        _h.SetDay(policy, 335);
        await OosAsync(policy, 312, 1600, "15000.00");

        // The offer was rated on the risk before the correction: acceptance is refused and nothing of term 2 is written.
        var (stale, staleBody) = await AcceptAsync(policy, jobId);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, staleBody?.ToJsonString());
        staleBody.Text("code").ShouldBe("POL-ERR-REBASE-REQUIRED");
        (await _h.ScalarAsync<long>($"SELECT count(DISTINCT term_id) FROM pol.policy_term WHERE policy_id = '{policy.PolicyId}'")).ShouldBe(1);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'RenewalBound' AND aggregate_id = '{policy.PolicyId}'")).ShouldBe(0);

        // Offering again rates the corrected risk (capacity 1400 -> 1600 carried to the last segment); the acceptance then binds a contiguous term 2.
        var (reoffered, reoffer) = await OfferAsync(policy, jobId);
        reoffered.StatusCode.ShouldBe(HttpStatusCode.OK, reoffer?.ToJsonString());
        var secondPremium = decimal.Parse(reoffer.Text("premiumSummary.premium.amount"), CultureInfo.InvariantCulture);
        (secondPremium - firstPremium).ShouldBe(
            decimal.Round(ChangeHarness.MtplRate(1600), 2, MidpointRounding.AwayFromZero) - decimal.Round(ChangeHarness.MtplRate(1400), 2, MidpointRounding.AwayFromZero));
        var (accepted, accept) = await AcceptAsync(policy, jobId);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, accept?.ToJsonString());
        await AssertTermsContiguousAsync(policy.PolicyId, policy.TermId, accept.Text("newTermId"));
    }
}
