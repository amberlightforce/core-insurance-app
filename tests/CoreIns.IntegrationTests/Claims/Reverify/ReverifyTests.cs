using System.Net;
using CoreIns.Modules.Claims.Events;
using CoreIns.SharedKernel;
using Npgsql;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;
using static CoreIns.IntegrationTests.Claims.Reverify.ReverifyHarness;

namespace CoreIns.IntegrationTests.Claims.Reverify;

/// <summary>
/// SL3-CLM-REVERIFY (REQ-CLM-002, -057, -058; D-SL3-03 d): CLM consumes <c>PolicyChanged</c> / <c>PolicyCancelled</c> from the real
/// outbox, asks POL (the scripted sandbox double with a version history) whether the claim's snapshot is superseded, raises
/// <c>ReverificationRequired</c> once per claim and cause, never changes the claim by itself, and a human keeps or adopts through
/// <c>clm.Coverage.reverify</c>. The real-POL path (supersession computed by POL itself) is in ReverifyRealPolTests.
/// </summary>
public sealed class ReverifyTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ReverifyHarness _h = null!;

    public ValueTask InitializeAsync()
    {
        _h = new ReverifyHarness(database);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task REQ_CLM_057_002_A_superseded_snapshot_raises_ReverificationRequired_once_per_cause_and_the_claim_is_untouched()
    {
        var policy = _h.Policy();
        var claim = await _h.OpenClaimAsync(policy);
        var before = await _h.ClaimAsync(claim);
        before.Text("summary.snapshotStatus").ShouldBe("VERIFIED");
        var oldRef = before.Text("summary.snapshotRef");
        var version = int.Parse(before.Text("summary.recordVersion"));

        _h.Supersede(policy, ["OD", "MTPL", "GLASS"]);
        var cause = await _h.PublishChangedAsync(policy, DaysAgo(3));
        await _h.DrainAsync();

        var raised = await _h.RaisedAsync(claim);
        raised.Count.ShouldBe(1);
        var payload = raised[0];
        payload["oldSnapshotRef"]!.GetValue<string>().ShouldBe(oldRef);
        payload["newSnapshotRef"]!.GetValue<string>().ShouldEndWith("-v2");
        payload["causeEventId"]!.GetValue<string>().ShouldBe(cause.EventId.Value.ToString());
        payload["causeEventType"]!.GetValue<string>().ShouldBe("PolicyChanged");
        payload["claimId"]!.GetValue<string>().ShouldBe(claim.ClaimId);

        // No personal data in the event (PITFALLS 18/31): only refs and ids.
        payload.Select(p => p.Key).Order().ShouldBe(["causeEventId", "causeEventType", "claimId", "newSnapshotRef", "oldSnapshotRef"]);
        var text = payload.ToJsonString();
        text.ShouldNotContain("SECRET-DESC");
        text.ShouldNotContain("Νικολάου");
        text.ShouldNotContain(policy.PolicyNumber);

        // REQ-CLM-002: the status moves; the ref, the cover and the exposures do not.
        var after = await _h.ClaimAsync(claim);
        after.Text("summary.snapshotStatus").ShouldBe("REVERIFICATION_REQUIRED");
        after.Text("summary.snapshotRef").ShouldBe(oldRef);
        after.Text("summary.recordVersion").ShouldBe((version + 1).ToString());
        after.Text("exposures.0.coverageIndication").ShouldBe("COVERED");
        after.Text("pendingReverification.oldSnapshotRef").ShouldBe(oldRef);
        after.Text("pendingReverification.newSnapshotRef").ShouldBe(payload["newSnapshotRef"]!.GetValue<string>());
        after.Text("pendingReverification.causeEventId").ShouldBe(cause.EventId.Value.ToString());

        // A duplicate delivery of the same event (a replay overrides the processed marker): still exactly one.
        (await _h.ReplayAsync(PolicyChangedHandler.Name, cause)).ShouldBe(1);
        await _h.DrainAsync();
        (await _h.RaisedAsync(claim)).Count.ShouldBe(1);
        (await _h.RowsAsync(claim)).ShouldBe(1);
        (await _h.ClaimAsync(claim)).Text("summary.recordVersion").ShouldBe((version + 1).ToString());
    }

    [Fact]
    public async Task REQ_CLM_057_Two_different_causes_raise_two_demands()
    {
        var policy = _h.Policy();
        var claim = await _h.OpenClaimAsync(policy);
        _h.Supersede(policy, ["OD", "MTPL", "GLASS"]);
        var first = await _h.PublishChangedAsync(policy, DaysAgo(3));
        var second = await _h.PublishChangedAsync(policy, DaysAgo(4));
        await _h.DrainAsync();

        var raised = await _h.RaisedAsync(claim);
        raised.Count.ShouldBe(2);
        raised.Select(r => r["causeEventId"]!.GetValue<string>()).Order()
            .ShouldBe([first.EventId.Value.ToString(), second.EventId.Value.ToString()], ignoreOrder: true);
        (await _h.RowsAsync(claim, "OPEN")).ShouldBe(2);
    }

    [Fact]
    public async Task REQ_CLM_057_A_loss_before_the_effective_date_raises_nothing_and_the_effective_date_itself_does()
    {
        var policy = _h.Policy();
        var before = await _h.OpenClaimAsync(policy);
        _h.Supersede(policy, ["OD", "MTPL", "GLASS"]);

        // The loss is two days ago: an effective date of tomorrow is after it.
        await _h.PublishChangedAsync(policy, BusinessDate.Parse(DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
        await _h.DrainAsync();
        (await _h.RaisedAsync(before)).ShouldBeEmpty();
        (await _h.ClaimAsync(before)).Text("summary.snapshotStatus").ShouldBe("VERIFIED");

        // On the loss date itself (at or after, REQ-CLM-057) the claim is looked at.
        var lossDate = BusinessDate.Parse((await _h.ClaimAsync(before)).Text("summary.lossDate"));
        await _h.PublishChangedAsync(policy, lossDate);
        await _h.DrainAsync();
        (await _h.RaisedAsync(before)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task REQ_CLM_057_A_snapshot_that_is_not_superseded_raises_nothing_and_another_policys_claims_are_not_looked_at()
    {
        var policy = _h.Policy();
        var other = _h.Policy();
        var claim = await _h.OpenClaimAsync(policy);
        var otherClaim = await _h.OpenClaimAsync(other);

        // POL's content at the loss date did not change (superseded = false): nothing is recorded.
        await _h.PublishChangedAsync(policy, DaysAgo(3));
        await _h.DrainAsync();
        (await _h.RaisedAsync(claim)).ShouldBeEmpty();
        (await _h.RowsAsync(claim)).ShouldBe(0);

        // The other policy changed; the first policy's claim is untouched.
        _h.Supersede(other, ["OD"]);
        await _h.PublishChangedAsync(other, DaysAgo(3));
        await _h.DrainAsync();
        (await _h.RaisedAsync(otherClaim)).Count.ShouldBe(1);
        (await _h.RaisedAsync(claim)).ShouldBeEmpty();
    }

    [Fact]
    public async Task REQ_CLM_057_A_closed_claim_is_not_re_verified()
    {
        var policy = _h.Policy();
        var claim = await _h.OpenClaimAsync(policy);
        var version = int.Parse((await _h.ClaimAsync(claim)).Text("summary.recordVersion"));
        (await _h.Money.CloseAsync(claim, version)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        _h.Supersede(policy, ["OD", "MTPL", "GLASS"]);
        await _h.PublishChangedAsync(policy, DaysAgo(3));
        await _h.DrainAsync();
        (await _h.RaisedAsync(claim)).ShouldBeEmpty();
    }

    [Fact]
    public async Task REQ_CLM_058_KEEP_returns_the_claim_to_verified_with_the_ref_unchanged_and_a_final_encrypted_decision_record()
    {
        var policy = _h.Policy();
        var claim = await _h.OpenClaimAsync(policy);
        var oldRef = (await _h.ClaimAsync(claim)).Text("summary.snapshotRef");
        _h.Supersede(policy, ["OD", "MTPL", "GLASS"]);
        await _h.PublishChangedAsync(policy, DaysAgo(3));
        await _h.DrainAsync();
        var pending = (await _h.ClaimAsync(claim)).Text("pendingReverification.newSnapshotRef");

        // A KEEP with a ref that is not the pending one is a mismatch, nothing changes.
        var (mismatch, mismatchBody) = await _h.ReverifyAsync(claim, "KEEP", oldRef + "-x");
        mismatch.StatusCode.ShouldBe(HttpStatusCode.Conflict, mismatchBody?.ToJsonString());
        mismatchBody.Text("code").ShouldBe("CLM-ERR-SNAPSHOT-MISMATCH");

        const string secret = "KEEP-COMMENT-SECRET-7731";
        var (response, body) = await _h.ReverifyAsync(claim, "KEEP", pending, comment: secret);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("decision").ShouldBe("KEEP");
        body.Text("snapshotStatus").ShouldBe("VERIFIED");
        body.Text("snapshotRef").ShouldBe(oldRef);
        body.Text("coverageInQuestion").ShouldBe("false");

        var after = await _h.ClaimAsync(claim);
        after.Text("summary.snapshotStatus").ShouldBe("VERIFIED");
        after.Text("summary.snapshotRef").ShouldBe(oldRef);
        after["pendingReverification"].ShouldBeNull();
        (await _h.RowsAsync(claim, "KEPT")).ShouldBe(1);
        (await _h.Money.ScalarAsync<string>($"SELECT reason_code FROM clm.reverification WHERE claim_id = '{claim.ClaimId}'")).ShouldBe("LOSS_BEFORE_CHANGE");

        // The comment is P2: encrypted at rest, in no event, no audit and no idempotency record (PITFALLS 18/31).
        (await _h.Money.ExposuresAsync(secret)).ShouldBe(0);
        (await _h.Money.ScalarAsync<long>($"SELECT count(*) FROM clm.reverification t WHERE t::text LIKE '%{secret}%'")).ShouldBe(0);

        // Nothing is pending any more.
        var (again, againBody) = await _h.ReverifyAsync(claim, "KEEP", null);
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, againBody?.ToJsonString());
        againBody.Text("code").ShouldBe("CLM-ERR-ILLEGAL-TRANSITION");
    }

    [Fact]
    public async Task REQ_CLM_058_ADOPT_that_keeps_the_cover_takes_the_new_ref_and_leaves_the_exposure_covered()
    {
        var policy = _h.Policy();
        var claim = await _h.OpenClaimAsync(policy);
        var oldRef = (await _h.ClaimAsync(claim)).Text("summary.snapshotRef");
        _h.Supersede(policy, ["OD", "MTPL", "GLASS"]);
        await _h.PublishChangedAsync(policy, DaysAgo(3));
        await _h.DrainAsync();
        var newRef = (await _h.ClaimAsync(claim)).Text("pendingReverification.newSnapshotRef");

        var (response, body) = await _h.ReverifyAsync(claim, "ADOPT", newRef);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("decision").ShouldBe("ADOPT");
        body.Text("snapshotStatus").ShouldBe("VERIFIED");
        body.Text("snapshotRef").ShouldBe(newRef);
        body.Text("previousSnapshotRef").ShouldBe(oldRef);
        body.Text("coverageInQuestion").ShouldBe("false");

        var after = await _h.ClaimAsync(claim);
        after.Text("summary.snapshotRef").ShouldBe(newRef);
        after.Text("summary.snapshotStatus").ShouldBe("VERIFIED");
        after.Text("summary.coverageInQuestion").ShouldBe("false");
        after.Text("exposures.0.coverageIndication").ShouldBe("COVERED");
        after["pendingReverification"].ShouldBeNull();
        (await _h.RowsAsync(claim, "ADOPTED")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_CLM_058_ADOPT_needs_the_expected_new_ref_and_a_stale_one_is_a_409()
    {
        var policy = _h.Policy();
        var claim = await _h.OpenClaimAsync(policy);
        var oldRef = (await _h.ClaimAsync(claim)).Text("summary.snapshotRef");
        _h.Supersede(policy, ["OD", "MTPL", "GLASS"]);
        await _h.PublishChangedAsync(policy, DaysAgo(3));
        await _h.DrainAsync();
        var newRef = (await _h.ClaimAsync(claim)).Text("pendingReverification.newSnapshotRef");

        // No expected ref → validation error (the contract requires it for ADOPT).
        var (missing, missingBody) = await _h.ReverifyAsync(claim, "ADOPT", null);
        missingBody.Text("code").ShouldBe("CLM-ERR-VALIDATION");

        // A ref that is not the pending successor is stale.
        var (stale, staleBody) = await _h.ReverifyAsync(claim, "ADOPT", oldRef);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, staleBody?.ToJsonString());
        staleBody.Text("code").ShouldBe("CLM-ERR-SNAPSHOT-MISMATCH");

        // POL superseded again after the demand was raised (its event has not arrived yet): the pending ref is no longer current.
        _h.Supersede(policy, ["MTPL"]);
        var (again, againBody) = await _h.ReverifyAsync(claim, "ADOPT", newRef);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict, againBody?.ToJsonString());
        againBody.Text("code").ShouldBe("CLM-ERR-SNAPSHOT-MISMATCH");

        // Nothing changed.
        var after = await _h.ClaimAsync(claim);
        after.Text("summary.snapshotRef").ShouldBe(oldRef);
        after.Text("summary.snapshotStatus").ShouldBe("REVERIFICATION_REQUIRED");
        (await _h.RowsAsync(claim, "OPEN")).ShouldBe(1);

        // POL not answering is a 503, not a 500, and again nothing changes.
        _h.PolDown = true;
        var (down, downBody) = await _h.ReverifyAsync(claim, "ADOPT", newRef);
        down.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, downBody?.ToJsonString());
        downBody.Text("code").ShouldBe("CLM-ERR-DEPENDENCY-UNAVAILABLE");
        _h.PolDown = false;
        (await _h.RowsAsync(claim, "OPEN")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_CLM_058_ADOPT_that_removes_the_cover_puts_the_exposure_in_question_and_refuses_new_payments_but_not_reserves()
    {
        var policy = _h.Policy();
        var claim = await _h.OpenClaimAsync(policy);
        _h.Supersede(policy, ["MTPL"]);
        await _h.PublishChangedAsync(policy, DaysAgo(3));
        await _h.DrainAsync();
        var newRef = (await _h.ClaimAsync(claim)).Text("pendingReverification.newSnapshotRef");

        // Before the decision nothing changed: the exposure is still covered.
        (await _h.ClaimAsync(claim)).Text("exposures.0.coverageIndication").ShouldBe("COVERED");

        var (response, body) = await _h.ReverifyAsync(claim, "ADOPT", newRef, reason: "POLICY_CORRECTED");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("coverageInQuestion").ShouldBe("true");

        var after = await _h.ClaimAsync(claim);
        after.Text("summary.coverageInQuestion").ShouldBe("true");
        after.Text("exposures.0.coverageIndication").ShouldBe("IN_QUESTION");
        after.Text("exposures.0.coverageDecision").ShouldBe("PENDING");

        // A new payment on the exposure is refused (CLM-ERR-COVERAGE-IN-QUESTION); a reserve is still allowed.
        var (payment, paymentBody) = await _h.Money.BuildAsync(claim, [Payment(claim, 100m, Guid.NewGuid().ToString())]);
        payment.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, paymentBody?.ToJsonString());
        paymentBody.Text("code").ShouldBe("CLM-ERR-COVERAGE-IN-QUESTION");
        (await _h.Money.BuildAsync(claim, [Reserve(claim, 100m)])).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task REQ_CLM_057_058_A_cancellation_before_the_loss_raises_a_demand_and_adopting_it_puts_the_policy_out_of_force()
    {
        var policy = _h.Policy();
        var claim = await _h.OpenClaimAsync(policy);

        // POL ends the term before the loss (loss two days ago, cancelled effective three days ago).
        _h.Supersede(policy, ["OD", "MTPL"], end: Instant.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-3)));
        var cause = await _h.PublishCancelledAsync(policy, DaysAgo(3));
        await _h.DrainAsync();

        var raised = await _h.RaisedAsync(claim);
        raised.Count.ShouldBe(1);
        raised[0]["causeEventType"]!.GetValue<string>().ShouldBe("PolicyCancelled");
        raised[0]["causeEventId"]!.GetValue<string>().ShouldBe(cause.EventId.Value.ToString());
        (await _h.ClaimAsync(claim)).Text("summary.policyInForceAtLoss").ShouldBe("true");

        var (response, body) = await _h.ReverifyAsync(claim, "ADOPT", raised[0]["newSnapshotRef"]!.GetValue<string>(), reason: "CHANGE_APPLIES");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("coverageInQuestion").ShouldBe("true");
        var after = await _h.ClaimAsync(claim);
        after.Text("summary.policyInForceAtLoss").ShouldBe("false");
        after.Text("summary.coverageInQuestion").ShouldBe("true");
        after.Text("exposures.0.coverageIndication").ShouldBe("IN_QUESTION");
    }

    [Fact]
    public async Task REQ_CLM_058_Several_open_demands_are_closed_by_one_decision_against_the_latest_ref()
    {
        var policy = _h.Policy();
        var claim = await _h.OpenClaimAsync(policy);
        _h.Supersede(policy, ["OD", "MTPL", "GLASS"]);
        await _h.PublishChangedAsync(policy, DaysAgo(3));
        await _h.PublishChangedAsync(policy, DaysAgo(4));
        await _h.DrainAsync();
        (await _h.RowsAsync(claim, "OPEN")).ShouldBe(2);

        var newRef = (await _h.ClaimAsync(claim)).Text("pendingReverification.newSnapshotRef");
        (await _h.ReverifyAsync(claim, "ADOPT", newRef)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _h.RowsAsync(claim, "ADOPTED")).ShouldBe(2);
        (await _h.RowsAsync(claim, "OPEN")).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_CLM_058_Racing_decisions_and_payments_never_return_a_500()
    {
        var policy = _h.Policy();
        var claim = await _h.OpenClaimAsync(policy);
        _h.Supersede(policy, ["MTPL"]);
        await _h.PublishChangedAsync(policy, DaysAgo(3));
        await _h.DrainAsync();
        var newRef = (await _h.ClaimAsync(claim)).Text("pendingReverification.newSnapshotRef");

        var calls = new List<Task<(HttpResponseMessage Response, System.Text.Json.Nodes.JsonNode? Body)>>
        {
            _h.ReverifyAsync(claim, "ADOPT", newRef),
            _h.ReverifyAsync(claim, "ADOPT", newRef),
            _h.ReverifyAsync(claim, "KEEP", newRef),
            _h.Money.BuildAsync(claim, [Payment(claim, 50m, Guid.NewGuid().ToString())]),
            _h.Money.BuildAsync(claim, [Payment(claim, 60m, Guid.NewGuid().ToString())]),
        };
        var results = await Task.WhenAll(calls);
        results.ShouldAllBe(r => (int)r.Response.StatusCode < 500, string.Join(" | ", results.Select(r => r.Body?.ToJsonString())));

        // Exactly one decision won; the payment path ends refused either way.
        results.Take(3).Count(r => r.Response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        (await _h.RowsAsync(claim, "OPEN")).ShouldBe(0);
        (await _h.ClaimAsync(claim)).Text("summary.snapshotStatus").ShouldBe("VERIFIED");
    }

    [Fact]
    public async Task REQ_CLM_058_The_decision_needs_its_permission_and_an_idempotency_key()
    {
        var policy = _h.Policy();
        var claim = await _h.OpenClaimAsync(policy);
        _h.Supersede(policy, ["OD", "MTPL", "GLASS"]);
        await _h.PublishChangedAsync(policy, DaysAgo(3));
        await _h.DrainAsync();
        var newRef = (await _h.ClaimAsync(claim)).Text("pendingReverification.newSnapshotRef");

        (await _h.ReverifyAsync(claim, "ADOPT", newRef, roles: ClaimsSlice.Handler + "x")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (noKey, noKeyBody) = await _h.Slice.SendAsync(
            HttpMethod.Post, "/api/clm/v1/coverage/reverify",
            new { claimId = claim.ClaimId, decision = "ADOPT", reasonCode = "X", expectedNewSnapshotRef = newRef }, withKey: false);
        ((int)noKey.StatusCode).ShouldBeInRange(400, 428, noKeyBody?.ToJsonString());
        (await _h.RowsAsync(claim, "OPEN")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_CLM_058_m1_KEEP_of_a_snapshot_that_is_not_in_force_at_the_loss_needs_the_claims_manager()
    {
        var policy = _h.Policy();
        var claim = await _h.OpenClaimAsync(policy);
        _h.Supersede(policy, ["OD", "MTPL"], end: Instant.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-3)));
        await _h.PublishCancelledAsync(policy, DaysAgo(3));
        await _h.DrainAsync();
        var newRef = (await _h.ClaimAsync(claim)).Text("pendingReverification.newSnapshotRef");

        var (denied, deniedBody) = await _h.ReverifyAsync(claim, "KEEP", newRef);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden, deniedBody?.ToJsonString());
        deniedBody.Text("code").ShouldBe("CLM-ERR-AUTHORITY");
        (await _h.RowsAsync(claim, "OPEN")).ShouldBe(1);

        var (allowed, allowedBody) = await _h.ReverifyAsync(claim, "KEEP", newRef, roles: ClaimsSlice.Manager);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK, allowedBody?.ToJsonString());
        (await _h.RowsAsync(claim, "KEPT")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_CLM_058_m2_A_reason_code_outside_the_configured_list_for_the_decision_is_a_validation_error()
    {
        var policy = _h.Policy();
        var claim = await _h.OpenClaimAsync(policy);
        _h.Supersede(policy, ["OD", "MTPL", "GLASS"]);
        await _h.PublishChangedAsync(policy, DaysAgo(3));
        await _h.DrainAsync();
        var newRef = (await _h.ClaimAsync(claim)).Text("pendingReverification.newSnapshotRef");

        foreach (var (decision, reason) in new[] { ("ADOPT", "MADE_UP"), ("ADOPT", "HANDLER_JUDGEMENT"), ("KEEP", "POLICY_CORRECTED") })
        {
            var (response, body) = await _h.ReverifyAsync(claim, decision, newRef, reason: reason);
            body.Text("code").ShouldBe("CLM-ERR-VALIDATION", body?.ToJsonString());
            ((int)response.StatusCode).ShouldBeInRange(400, 422);
        }

        (await _h.RowsAsync(claim, "OPEN")).ShouldBe(1);
    }

    [Fact]
    public async Task P1_A_payment_set_built_before_an_adoption_that_removes_the_cover_cannot_be_approved()
    {
        var policy = _h.Policy();
        var claim = await _h.OpenClaimAsync(policy);
        (await _h.Money.BuildAndSubmitAsync(claim, [Reserve(claim, 1200m)])).Text("status").ShouldBe("APPROVED");
        var (account, _) = await _h.Money.CaptureAsync(claim);
        var (built, build) = await _h.Money.BuildAsync(claim, [Payment(claim, 500m, account, paymentType: "PARTIAL")]);
        built.StatusCode.ShouldBe(HttpStatusCode.OK, build?.ToJsonString());
        var setId = build.Text("setId");

        _h.Supersede(policy, ["MTPL"]);
        await _h.PublishChangedAsync(policy, DaysAgo(3));
        await _h.DrainAsync();
        var newRef = (await _h.ClaimAsync(claim)).Text("pendingReverification.newSnapshotRef");
        (await _h.ReverifyAsync(claim, "ADOPT", newRef)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (submitted, submit) = await _h.Money.SubmitAsync(setId);
        submitted.StatusCode.ShouldBe(HttpStatusCode.Conflict, submit?.ToJsonString());
        submit.Text("code").ShouldBe("CLM-ERR-SET-STALE");
        (await _h.Money.ScalarAsync<string>($"SELECT status FROM clm.transaction_set WHERE set_id = '{setId}'")).ShouldBe("DRAFT");
        (await _h.Money.ScalarAsync<long>(
            $"SELECT count(*) FROM clm.claim_payment WHERE claim_id = '{claim.ClaimId}' AND status IN ('APPROVED', 'SUBMITTED', 'ISSUED', 'CLEARED')")).ShouldBe(0);
    }

    [Fact]
    public async Task P2_POL_down_during_consumption_records_nothing_and_a_retry_succeeds_later()
    {
        var policy = _h.Policy();
        var claim = await _h.OpenClaimAsync(policy);
        _h.Supersede(policy, ["OD", "MTPL", "GLASS"]);

        _h.PolDown = true;
        await _h.PublishChangedAsync(policy, DaysAgo(3));
        await _h.DrainAsync();
        (await _h.RowsAsync(claim)).ShouldBe(0);
        (await _h.RaisedAsync(claim)).ShouldBeEmpty();
        (await _h.ClaimAsync(claim)).Text("summary.snapshotStatus").ShouldBe("VERIFIED");

        _h.PolDown = false;
        for (var attempt = 0; attempt < 40 && (await _h.RaisedAsync(claim)).Count == 0; attempt++)
        {
            await Task.Delay(1500, ClaimsSlice.Ct);
            await _h.DrainAsync();
        }

        (await _h.RaisedAsync(claim)).Count.ShouldBe(1);
        (await _h.RowsAsync(claim, "OPEN")).ShouldBe(1);
    }

    [Fact]
    public async Task D_ARC_34_The_demand_record_is_frozen_and_its_decision_final_even_for_the_owner()
    {
        var policy = _h.Policy();
        var claim = await _h.OpenClaimAsync(policy);
        _h.Supersede(policy, ["OD", "MTPL", "GLASS"]);
        await _h.PublishChangedAsync(policy, DaysAgo(3));
        await _h.DrainAsync();

        async Task<PostgresException> Refused(string sql)
        {
            await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
            await using var command = dataSource.CreateCommand(sql);
            return await Should.ThrowAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync(ClaimsSlice.Ct));
        }

        (await Refused($"UPDATE clm.reverification SET new_snapshot_ref = 'forged' WHERE claim_id = '{claim.ClaimId}'")).SqlState.ShouldBe("23001");
        (await Refused($"DELETE FROM clm.reverification WHERE claim_id = '{claim.ClaimId}'")).SqlState.ShouldBe("23001");

        var newRef = (await _h.ClaimAsync(claim)).Text("pendingReverification.newSnapshotRef");
        (await _h.ReverifyAsync(claim, "KEEP", newRef)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Refused($"UPDATE clm.reverification SET status = 'ADOPTED' WHERE claim_id = '{claim.ClaimId}'")).SqlState.ShouldBe("23001");
        (await Refused($"UPDATE clm.reverification SET comment_encrypted = '\\x00' WHERE claim_id = '{claim.ClaimId}'")).SqlState.ShouldBe("23001");
        (await Refused($"UPDATE clm.reverification SET jurisdiction = 'CY' WHERE claim_id = '{claim.ClaimId}'")).SqlState.ShouldBe("23001");
        (await Refused($"UPDATE clm.reverification SET reason_code = NULL WHERE claim_id = '{claim.ClaimId}'")).SqlState.ShouldBeOneOf("23001", "23514");

        // The app role has no DELETE on the table at all.
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var privilege = app.CreateCommand("SELECT has_table_privilege('app', 'clm.reverification', 'DELETE')");
        ((bool)(await privilege.ExecuteScalarAsync(ClaimsSlice.Ct))!).ShouldBeFalse();
    }
}
