using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Underwriting.Contracts;
using CoreIns.Platform.Contracts.Common;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Renewal;

/// <summary>
/// SL3-POL-RENEW on PostgreSQL with scripted RAT/UW doubles and a shiftable clock: E2E-04 steps 1-7 (create inside the window,
/// RENEWAL rating and PRE_BIND UW, offer, explicit acceptance binding term n+1), the refusals, and the temporal invariants
/// (contiguity, one stamp per command, term 1 untouched). Requirement ids are in the test names (REQ → test traceability).
/// </summary>
public sealed class RenewalTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private RenewalHarness _renewal = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var slice = new PolicySlice(
            database.AppConnectionString, settings: new Dictionary<string, string?> { ["Platform:Time:Mode"] = "Shiftable" });
        await slice.SeedAsync();
        _renewal = new RenewalHarness(slice, database.SuperuserConnectionString);
        await _renewal.BindPolicyAsync();
    }

    public async ValueTask DisposeAsync() => await _renewal.Slice.DisposeAsync();

    [Fact]
    public async Task E2E_04_renew_now_offer_accept_binds_term_two_contiguous_with_term_one_on_the_version_resolved_at_its_start()
    {
        // MOTOR-GR 1.1 is imported after term 1 was bound on 1.0 (D-SL3-15): the renewal term resolves to it, term 1 keeps 1.0.
        var (imported, importBody) = await _renewal.ImportVersion11Async();
        imported.StatusCode.ShouldBeOneOf([HttpStatusCode.OK, HttpStatusCode.Created], importBody?.ToJsonString());
        var term1Snapshot = await SnapshotContentAsync(DateTimeOffset.UtcNow.AddDays(30));

        await _renewal.GoToAsync(30);

        // Step 1 (REQ-POL-245, -246, -258, -263): "Renew now" 30 days before expiry; artefact pinned at the new term's start.
        var (created, create) = await _renewal.CreateAsync();
        created.StatusCode.ShouldBe(HttpStatusCode.Created, create?.ToJsonString());
        create.Text("state").ShouldBe("DRAFT");
        create.Text("renewalProductVersion").ShouldBe("1.1");
        create.Text("expiringTermId").ShouldBe(_renewal.TermId);
        created.Headers.Location!.ToString().ShouldBe($"/api/pol/v1/jobs/{create.Text("jobId")}");
        (await _renewal.ScalarAsync<string>($"SELECT job_type FROM pol.job WHERE job_id = '{_renewal.JobId}'")).ShouldBe("RENEWAL");
        var expiry = await _renewal.ScalarAsync<DateTime>($"SELECT valid_to FROM pol.policy_term WHERE term_id = '{_renewal.TermId}' AND recorded_to IS NULL");
        (await _renewal.ScalarAsync<DateTime>($"SELECT effective_at FROM pol.job WHERE job_id = '{_renewal.JobId}'")).ShouldBe(expiry);
        var head = await _renewal.ScalarAsync<Guid>($"SELECT head_transaction_id FROM pol.policy_term WHERE term_id = '{_renewal.TermId}' AND recorded_to IS NULL");
        (await _renewal.ScalarAsync<Guid>($"SELECT base_transaction_id FROM pol.job WHERE job_id = '{_renewal.JobId}'")).ShouldBe(head);
        (await _renewal.CountEventsAsync("RenewalCreated")).ShouldBe(1);

        // The risk tree was copied with its static locators (REQ-POL-246).
        var jobId = _renewal.JobId;
        var copied = await _renewal.ScalarAsync<string>($"SELECT risk_tree->'vehicles'->0->>'locator' FROM pol.quote_version WHERE job_id = '{jobId}' AND version_no = 1");
        var original = await _renewal.ScalarAsync<string>(
            $"SELECT snapshot->'vehicles'->0->>'locator' FROM pol.segment WHERE term_id = '{_renewal.TermId}' AND recorded_to IS NULL");
        copied.ShouldBe(original);

        // Steps 3-5 (REQ-POL-249, -250, -201): RENEWAL rating, UW at PRE_BIND, Quoted.Offered with RenewalOffered.
        var (offered, offer) = await _renewal.OfferAsync();
        offered.StatusCode.ShouldBe(HttpStatusCode.OK, offer?.ToJsonString());
        offer.Text("state").ShouldBe("QUOTED");
        (await _renewal.SubStateAsync()).ShouldBe("OFFERED");
        (await _renewal.ReferredAsync()).ShouldBeFalse();
        offer.Text("acceptanceMode").ShouldBe("EXPLICIT");
        var premium = decimal.Parse(offer.Text("premiumSummary.premium.amount"), CultureInfo.InvariantCulture);
        var total = decimal.Parse(offer.Text("premiumSummary.total.amount"), CultureInfo.InvariantCulture);
        premium.ShouldBe(432.36m);
        total.ShouldBe(479.21m);
        (await _renewal.CountEventsAsync("RenewalOffered")).ShouldBe(1);
        var offeredEvent = await _renewal.ScalarAsync<string>(
            $"SELECT payload::text FROM plt.outbox_message WHERE event_type = 'RenewalOffered' AND aggregate_id = '{_renewal.PolicyId}'");
        var offeredPayload = JsonNode.Parse(offeredEvent)!;
        offeredPayload.Text("acceptanceMode").ShouldBe("EXPLICIT");
        offeredPayload.Text("offerVersion").ShouldBe("1");
        offeredPayload.Text("premiumSummary.total.amount").ShouldBe("479.21");

        // Steps 6-7 (REQ-POL-253, -257, -005, -033, -263): explicit acceptance (channel STAFF), bind of term 2 in one command.
        var (accepted, accept) = await _renewal.AcceptAsync(user: "uw-anna");
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, accept?.ToJsonString());
        accept.Text("state").ShouldBe("BOUND");
        accept.Text("newTermNumber").ShouldBe("2");
        accept.Text("termState").ShouldBe("SCHEDULED");
        accept.Text("predecessorTermId").ShouldBe(_renewal.TermId);
        var term2 = accept.Text("newTermId");
        var transaction = accept.Text("transactionId");
        var recordedAt = await _renewal.ScalarAsync<DateTime>($"SELECT recorded_at FROM pol.policy_transaction WHERE transaction_id = '{transaction}'");

        // Term 2 is contiguous with term 1 (no gap, no overlap) and follows it (predecessor_term_id); the pins are the resolved 1.1.
        var contiguous = await _renewal.ScalarAsync<bool>(
            $"SELECT (SELECT valid_to FROM pol.policy_term WHERE term_id = '{_renewal.TermId}' AND recorded_to IS NULL) = (SELECT valid_from FROM pol.policy_term WHERE term_id = '{term2}')");
        contiguous.ShouldBeTrue();
        (await _renewal.ScalarAsync<string>($"SELECT predecessor_term_id::text FROM pol.policy_term WHERE term_id = '{term2}'")).ShouldBe(_renewal.TermId);
        (await _renewal.ScalarAsync<string>($"SELECT product_version FROM pol.policy_term WHERE term_id = '{term2}'")).ShouldBe("1.1");
        (await _renewal.ScalarAsync<string>($"SELECT product_version FROM pol.policy_term WHERE term_id = '{_renewal.TermId}'")).ShouldBe("1.0");
        (await _renewal.ScalarAsync<string>($"SELECT state FROM pol.policy_term WHERE term_id = '{term2}'")).ShouldBe("SCHEDULED");
        (await _renewal.ScalarAsync<bool>($"SELECT valid_to = valid_from + interval '1 year' FROM pol.policy_term WHERE term_id = '{term2}'")).ShouldBeTrue();
        (await _renewal.ScalarAsync<string>($"SELECT kind FROM pol.policy_transaction WHERE transaction_id = '{transaction}'")).ShouldBe("RENEWAL");
        (await _renewal.ScalarAsync<long>($"SELECT count(DISTINCT policy_number) FROM pol.policy WHERE policy_id = '{_renewal.PolicyId}'")).ShouldBe(1);
        (await _renewal.TermVersionsAsync()).ShouldBe(2);

        // Σ term-2 deltas = term-2 written = the offer; every row and event carries the one record time of the command.
        (await _renewal.ScalarAsync<decimal>($"SELECT sum(amount) FROM pol.charge_line WHERE term_id = '{term2}'")).ShouldBe(total);
        (await _renewal.ScalarAsync<decimal>($"SELECT total FROM pol.policy_transaction WHERE transaction_id = '{transaction}'")).ShouldBe(total);
        (await _renewal.ScalarAsync<long>($"SELECT count(*) FROM pol.charge_line WHERE term_id = '{term2}' AND transaction_kind <> 'NEW_BUSINESS'")).ShouldBe(0);
        (await _renewal.ScalarAsync<long>(
                $"SELECT count(*) FROM pol.charge_line c JOIN pol.policy_term t ON t.term_id = c.term_id WHERE c.term_id = '{term2}' AND (c.recorded_at <> t.recorded_from OR c.recorded_at <> '{recordedAt:O}')"))
            .ShouldBe(0);
        (await _renewal.ScalarAsync<decimal>(
                $"SELECT sum((payload->'netAmount'->>'amount')::numeric) FROM plt.outbox_message WHERE event_type = 'ChargeDeltaEmitted' AND set_id = '{transaction}'"))
            .ShouldBe(total);
        (await _renewal.CountEventsAsync("RenewalBound")).ShouldBe(1);
        var bound = JsonNode.Parse(await _renewal.ScalarAsync<string>(
            $"SELECT payload::text FROM plt.outbox_message WHERE event_type = 'RenewalBound' AND aggregate_id = '{_renewal.PolicyId}'"))!;
        bound.Text("newTermId").ShouldBe(term2);
        bound.Text("newTermNumber").ShouldBe("2");
        bound.Text("productVersion").ShouldBe("1.1");
        bound.Text("producerOfRecord").ShouldBe("DIRECT");
        (await _renewal.ScalarAsync<string>($"SELECT accepted_by FROM pol.job WHERE job_id = '{jobId}'")).ShouldBe("USER:uw-anna");
        // PITFALLS 5 / D-UW-01: the creator, the offerer and the acceptor are all participants UW refuses as deciders.
        (await _renewal.ScalarAsync<long>($"SELECT cardinality(participants) FROM pol.job WHERE job_id = '{jobId}'")).ShouldBe(2);
        (await _renewal.ScalarAsync<bool>($"SELECT 'USER:uw-anna' = ANY(participants) FROM pol.job WHERE job_id = '{jobId}'")).ShouldBeTrue();
        (await _renewal.ScalarAsync<string>($"SELECT acceptance_channel FROM pol.job WHERE job_id = '{jobId}'")).ShouldBe("STAFF");
        (await _renewal.ScalarAsync<bool>($"SELECT accepted_at IS NOT NULL FROM pol.job WHERE job_id = '{jobId}'")).ShouldBeTrue();

        // The exclusion constraint holds: an overlapping term cannot be written.
        var overlap = await Should.ThrowAsync<PostgresException>(() => _renewal.ExecuteAsync($"""
            UPDATE pol.policy SET last_recorded_at = last_recorded_at + interval '1 second' WHERE policy_id = '{_renewal.PolicyId}';
            CREATE TEMP TABLE clash AS SELECT * FROM pol.policy_term WHERE term_id = '{term2}';
            UPDATE clash SET term_version_id = gen_random_uuid(), term_id = gen_random_uuid(), term_number = 3, valid_from = valid_from + interval '1 day',
                   recorded_from = (SELECT last_recorded_at FROM pol.policy WHERE policy_id = '{_renewal.PolicyId}');
            INSERT INTO pol.policy_term SELECT * FROM clash;
            """));
        overlap.SqlState.ShouldBe(PostgresErrorCodes.ExclusionViolation);

        // pol.Policy.get shows both terms by validAt; term 1's snapshot is unaffected by the renewal.
        var (_, now) = await _renewal.GetAsync($"/api/pol/v1/policies/{_renewal.PolicyId}");
        now.Text("term.termNumber").ShouldBe("1");
        var inTerm2 = DateTime.SpecifyKind(expiry, DateTimeKind.Utc).AddDays(10).ToString("O", CultureInfo.InvariantCulture);
        var (_, later) = await _renewal.GetAsync($"/api/pol/v1/policies/{_renewal.PolicyId}?validAt={Uri.EscapeDataString(inTerm2)}");
        later.Text("term.termNumber").ShouldBe("2");
        later.Text("term.productVersion").ShouldBe("1.1");
        later.Text("policy.policyNumber").ShouldBe(_renewal.PolicyNumber);
        (await SnapshotContentAsync(DateTimeOffset.UtcNow.AddDays(30))).ShouldBe(term1Snapshot);
    }

    [Fact]
    public async Task REQ_POL_245_outside_the_renewal_window_or_after_expiry_the_renewal_is_refused()
    {
        // A fresh policy has a year to run; 45 days is the window (illustrative lead).
        var (early, earlyBody) = await _renewal.CreateAsync();
        early.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, earlyBody?.ToJsonString());
        earlyBody.Text("code").ShouldBe("POL-ERR-VALIDATION");
        earlyBody.Text("errors.0.code").ShouldBe("OUTSIDE_RENEWAL_WINDOW");

        await _renewal.GoToAsync(46);
        (await _renewal.CreateAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // Inside the window, the 45th day counts; after the term ended there is nothing to renew.
        await _renewal.GoToAsync(45);
        (await _renewal.CreateAsync(dryRun: true)).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        await _renewal.GoToAsync(-1);
        var (late, lateBody) = await _renewal.CreateAsync();
        late.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, lateBody?.ToJsonString());
        lateBody.Text("errors.0.code").ShouldBe("OUTSIDE_RENEWAL_WINDOW");
        (await _renewal.ScalarAsync<long>($"SELECT count(*) FROM pol.job WHERE job_type = 'RENEWAL' AND policy_id = '{_renewal.PolicyId}'")).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_POL_245_one_open_renewal_per_term_and_a_renewed_term_cannot_be_renewed_again()
    {
        await _renewal.GoToAsync(30);
        (await _renewal.CreateAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var (second, secondBody) = await _renewal.CreateAsync();
        RenewalHarness.ShouldFailWith(second, secondBody, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");

        (await _renewal.OfferAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _renewal.AcceptAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (third, thirdBody) = await _renewal.CreateAsync();
        RenewalHarness.ShouldFailWith(third, thirdBody, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");
        (await _renewal.TermVersionsAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task REQ_POL_253_accepting_twice_is_a_replay_with_the_same_key_and_a_conflict_otherwise_never_two_terms()
    {
        await _renewal.GoToAsync(30);
        (await _renewal.CreateAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _renewal.OfferAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var key = Guid.NewGuid();
        var at = _renewal.Clock.Now.ToDateTimeOffset().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var (first, firstBody) = await _renewal.AcceptAsync(key: key, acceptedAt: at);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, firstBody?.ToJsonString());
        var (replay, replayBody) = await _renewal.AcceptAsync(key: key, acceptedAt: at);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK, replayBody?.ToJsonString());
        replayBody.Text("newTermId").ShouldBe(firstBody.Text("newTermId"));

        var (again, againBody) = await _renewal.AcceptAsync();
        RenewalHarness.ShouldFailWith(again, againBody, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");
        (await _renewal.TermVersionsAsync()).ShouldBe(2);
        (await _renewal.CountEventsAsync("RenewalBound")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_POL_201_a_referred_renewal_is_not_offered_or_accepted_until_the_referral_is_decided()
    {
        await _renewal.GoToAsync(30);
        (await _renewal.CreateAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.Created);

        // An open issue blocking PRE_BIND: Quoted and Referred, but not offered; no RenewalOffered goes out.
        _renewal.Slice.Raise(BlockingPoint.PreBind);
        var (referred, referral) = await _renewal.OfferAsync();
        referred.StatusCode.ShouldBe(HttpStatusCode.OK, referral?.ToJsonString());
        referral.Text("state").ShouldBe("QUOTED");
        (await _renewal.ReferredAsync()).ShouldBeTrue();
        (await _renewal.SubStateAsync()).ShouldBe("NONE");
        (await _renewal.CountEventsAsync("RenewalOffered")).ShouldBe(0);

        // Accepting is refused by the bind gate, as for new business: nothing is bound.
        var (gated, gate) = await _renewal.AcceptAsync();
        RenewalHarness.ShouldFailWith(gated, gate, HttpStatusCode.UnprocessableEntity, "POL-ERR-GATE-FAILED");
        (await _renewal.TermVersionsAsync()).ShouldBe(1);

        // The decision flows back: UW no longer blocks; offering again offers; accepting binds.
        _renewal.Slice.AcceptAll();
        var (reoffered, reoffer) = await _renewal.OfferAsync();
        reoffered.StatusCode.ShouldBe(HttpStatusCode.OK, reoffer?.ToJsonString());
        (await _renewal.SubStateAsync()).ShouldBe("OFFERED");
        (await _renewal.ReferredAsync()).ShouldBeFalse();
        (await _renewal.CountEventsAsync("RenewalOffered")).ShouldBe(1);

        // A referral raised after the offer fails the gate at acceptance too.
        _renewal.Slice.Raise(BlockingPoint.PreBind);
        var (lateGate, lateBody) = await _renewal.AcceptAsync();
        RenewalHarness.ShouldFailWith(lateGate, lateBody, HttpStatusCode.UnprocessableEntity, "POL-ERR-GATE-FAILED");
        _renewal.Slice.AcceptAll();
        (await _renewal.AcceptAsync()).Body.Text("state").ShouldBe("BOUND");
    }

    [Fact]
    public async Task Accepting_an_unoffered_renewal_is_refused()
    {
        await _renewal.GoToAsync(30);
        (await _renewal.CreateAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var (draft, draftBody) = await _renewal.AcceptAsync();
        RenewalHarness.ShouldFailWith(draft, draftBody, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");

        // Offering twice without an edit is refused too: the prior offer stands.
        (await _renewal.OfferAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (again, againBody) = await _renewal.OfferAsync();
        RenewalHarness.ShouldFailWith(again, againBody, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");
    }

    [Fact]
    public async Task Editing_an_offered_renewal_keeps_the_prior_offer_and_offering_again_offers_the_new_version()
    {
        await _renewal.GoToAsync(30);
        await _renewal.CreateAsync();
        var jobId = _renewal.JobId;
        (await _renewal.OfferAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // pol.Job.updateDraft takes the offered (Quoted) job back to Draft on a new version; the first version is kept as Superseded.
        var (edited, edit) = await _renewal.PostAsync(
            "/api/pol/v1/jobs/update-draft",
            new { jobId, versionNo = 1, expectedDraftVersion = 0, instructions = new object[] { new { op = "SET_ANSWERS", questionSet = new { questionSetCode = "MOTOR-RISK", questionSetVersion = "1", answers = new Dictionary<string, string> { ["Q-USAGE"] = "PRIVATE", ["Q-HIRE-REWARD"] = "NO" } } } } });
        edited.StatusCode.ShouldBe(HttpStatusCode.OK, edit?.ToJsonString());
        edit.Text("versionNo").ShouldBe("2");

        var (reoffered, reoffer) = await _renewal.OfferAsync();
        reoffered.StatusCode.ShouldBe(HttpStatusCode.OK, reoffer?.ToJsonString());
        (await _renewal.SubStateAsync()).ShouldBe("OFFERED");
        reoffer.Text("offerVersion").ShouldBe("2");
        (await _renewal.ScalarAsync<string>($"SELECT string_agg(state, ',' ORDER BY version_no) FROM pol.quote_version WHERE job_id = '{jobId}'")).ShouldBe("SUPERSEDED,QUOTED");
        (await _renewal.CountEventsAsync("RenewalOffered")).ShouldBe(2);
    }

    [Fact]
    public async Task REQ_POL_246_a_change_bound_on_term_one_after_the_offer_makes_the_acceptance_require_a_rebase()
    {
        await _renewal.GoToAsync(30);
        (await _renewal.CreateAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _renewal.OfferAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await _renewal.SimulateLaterHeadAsync("CHANGE");

        var (response, body) = await _renewal.AcceptAsync();
        RenewalHarness.ShouldFailWith(response, body, HttpStatusCode.Conflict, "POL-ERR-REBASE-REQUIRED");
        (await _renewal.TermVersionsAsync()).ShouldBe(1);
        (await _renewal.CountEventsAsync("RenewalBound")).ShouldBe(0);
    }

    [Fact]
    public async Task A_cancelled_term_cannot_be_renewed()
    {
        await _renewal.GoToAsync(30);
        await _renewal.SimulateLaterHeadAsync("CANCELLATION", "CANCELLED");

        var (response, body) = await _renewal.CreateAsync();

        RenewalHarness.ShouldFailWith(response, body, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");
        (await _renewal.ScalarAsync<long>($"SELECT count(*) FROM pol.job WHERE job_type = 'RENEWAL' AND policy_id = '{_renewal.PolicyId}'")).ShouldBe(0);
    }

    [Fact]
    public async Task A_term_cancelled_after_the_offer_cannot_be_accepted()
    {
        await _renewal.GoToAsync(30);
        (await _renewal.CreateAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _renewal.OfferAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _renewal.SimulateLaterHeadAsync("CANCELLATION", "CANCELLED");

        var (response, body) = await _renewal.AcceptAsync();

        RenewalHarness.ShouldFailWith(response, body, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");
        (await _renewal.TermVersionsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Only_staff_acceptance_is_supported_and_an_unknown_term_or_role_is_refused()
    {
        await _renewal.GoToAsync(30);
        (await _renewal.CreateAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _renewal.OfferAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (payment, paymentBody) = await _renewal.AcceptAsync(channel: "PAYMENT");
        payment.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, paymentBody?.ToJsonString());
        paymentBody.Text("errors.0.code").ShouldBe("CHANNEL_NOT_SUPPORTED");

        (await _renewal.CreateAsync(termId: Guid.CreateVersion7().ToString())).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _renewal.CreateAsync(roles: Billing)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _renewal.OfferAsync(roles: Billing)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _renewal.AcceptAsync(roles: Billing)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _renewal.TermVersionsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Dry_run_computes_the_renewal_and_leaves_nothing_behind()
    {
        await _renewal.GoToAsync(30);
        var (dry, dryBody) = await _renewal.CreateAsync(dryRun: true);
        dry.StatusCode.ShouldBe(HttpStatusCode.Created, dryBody?.ToJsonString());
        (await _renewal.ScalarAsync<long>($"SELECT count(*) FROM pol.job WHERE job_type = 'RENEWAL' AND policy_id = '{_renewal.PolicyId}'")).ShouldBe(0);
        (await _renewal.CountEventsAsync("RenewalCreated")).ShouldBe(0);

        (await _renewal.CreateAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _renewal.OfferAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (acceptDry, acceptBody) = await _renewal.AcceptAsync(dryRun: true);
        acceptDry.StatusCode.ShouldBe(HttpStatusCode.OK, acceptBody?.ToJsonString());
        acceptBody.Text("state").ShouldBe("BOUND");
        (await _renewal.TermVersionsAsync()).ShouldBe(1);
        (await _renewal.CountEventsAsync("RenewalBound")).ShouldBe(0);
        (await _renewal.JobStateAsync()).ShouldBe("QUOTED");
    }

    private async Task<string> SnapshotContentAsync(DateTimeOffset validAt)
    {
        var instant = Uri.EscapeDataString(validAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        var (response, body) = await _renewal.GetAsync($"/api/pol/v1/snapshots/get?policyId={_renewal.PolicyId}&validAt={instant}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["content"]!.ToJsonString();
    }
}
