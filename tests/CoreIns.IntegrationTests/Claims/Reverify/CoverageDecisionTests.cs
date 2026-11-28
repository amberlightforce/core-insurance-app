using System.Net;
using System.Text.Json.Nodes;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;
using static CoreIns.IntegrationTests.Claims.Reverify.ReverifyHarness;

namespace CoreIns.IntegrationTests.Claims.Reverify;

/// <summary>
/// BO-CLM-03 (REQ-CLM-002, -051, -058): <c>clm.Coverage.decide</c> over HTTP on a real PostgreSQL: a denial blocks new payment sets, a
/// reservation of rights moves the claim to UnderInvestigation, a decision after a re-verification clears the in-question gate, and
/// the authority limit with four eyes applies.
/// </summary>
public sealed class CoverageDecisionTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ReverifyHarness _h = null!;

    public ValueTask InitializeAsync()
    {
        _h = new ReverifyHarness(database);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private Task<(HttpResponseMessage Response, JsonNode? Body)> DecideAsync(
        MoneyClaim claim, string outcome, string reason = "POLICY_TERMS", string roles = Handler, string user = HandlerUser) =>
        _h.Money.SendAsync(
            HttpMethod.Post, "/api/clm/v1/coverage/decide",
            new { claimId = claim.ClaimId, exposureId = claim.ExposureId, outcome, reasonCode = reason, comment = "SECRET-COMMENT" }, roles, user);

    private Task<long> EventsAsync(MoneyClaim claim, string type) => _h.Money.ScalarAsync<long>(
        $"SELECT count(*) FROM plt.outbox_message WHERE aggregate_type = 'Claim' AND aggregate_id = '{claim.ClaimId}' AND event_type = '{type}'");

    [Fact]
    public async Task A_denied_exposure_refuses_new_payment_sets_but_not_reserves_and_the_decision_is_published_and_audited()
    {
        var claim = await _h.OpenClaimAsync(_h.Policy());

        (await DecideAsync(claim, "DENIED", reason: "bad")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var (response, body) = await DecideAsync(claim, "DENIED");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("outcome").ShouldBe("DENIED");
        body.Text("coverageIndication").ShouldBe("NOT_COVERED");
        body.Text("coverageDecision").ShouldBe("NOT_COVERED");

        var after = await _h.ClaimAsync(claim);
        after.Text("exposures.0.coverageDecision").ShouldBe("NOT_COVERED");

        var (payment, paymentBody) = await _h.Money.BuildAsync(claim, [Payment(claim, 100m, Guid.NewGuid().ToString())]);
        payment.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, paymentBody?.ToJsonString());
        paymentBody.Text("code").ShouldBe("CLM-ERR-COVERAGE-DENIED");
        (await _h.Money.BuildAsync(claim, [Reserve(claim, 100m)])).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await EventsAsync(claim, "CoverageDecisionRecorded")).ShouldBe(1);
        var payload = await _h.Money.ScalarAsync<string>(
            $"SELECT payload::text FROM plt.outbox_message WHERE event_type = 'CoverageDecisionRecorded' AND aggregate_id = '{claim.ClaimId}'");
        payload.ShouldContain("\"decision\": \"DENIED\"");
        payload.ShouldContain("POLICY_TERMS");
        payload.ShouldNotContain("SECRET-COMMENT");
        (await _h.Money.ScalarAsync<long>(
            $"SELECT count(*) FROM plt.audit_event WHERE operation = 'clm.Coverage.decide' AND outcome = 'Succeeded' AND object_id = '{claim.ClaimId}' AND changes::text LIKE '%POLICY_TERMS%' AND changes::text NOT LIKE '%SECRET-COMMENT%'"))
            .ShouldBe(1);

        // An unknown exposure is a 404; an unauthorised role a 403.
        (await _h.Money.SendAsync(HttpMethod.Post, "/api/clm/v1/coverage/decide",
            new { claimId = claim.ClaimId, exposureId = Guid.NewGuid(), outcome = "COVERED", reasonCode = "POLICY_TERMS" }, Handler, HandlerUser))
            .Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await DecideAsync(claim, "COVERED", roles: "Staff.Finance")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_reservation_of_rights_moves_the_claim_to_UnderInvestigation_and_keeps_payments_open()
    {
        var claim = await _h.OpenClaimAsync(_h.Policy());

        var (response, body) = await DecideAsync(claim, "RESERVATION_OF_RIGHTS");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("coverageDecision").ShouldBe("COVERED_WITH_RESERVATION");
        body.Text("claimStatus").ShouldBe("OPEN");
        body.Text("claimSubStatus").ShouldBe("UNDER_INVESTIGATION");

        var after = await _h.ClaimAsync(claim);
        after.Text("summary.status").ShouldBe("OPEN");
        after.Text("summary.subStatus").ShouldBe("UNDER_INVESTIGATION");

        // The denial gate does not apply: the payment fails later (unknown payee account), not on cover.
        var (payment, paymentBody) = await _h.Money.BuildAsync(claim, [Payment(claim, 100m, Guid.NewGuid().ToString())]);
        paymentBody.Text("code").ShouldNotBe("CLM-ERR-COVERAGE-DENIED");
        paymentBody.Text("code").ShouldNotBe("CLM-ERR-COVERAGE-IN-QUESTION");
        payment.StatusCode.ShouldNotBe(HttpStatusCode.OK);

        // A closed claim cannot be decided.
        (await _h.Money.CloseAsync(claim, int.Parse(after.Text("summary.recordVersion")))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await DecideAsync(claim, "COVERED")).Body.Text("code").ShouldBe("CLM-ERR-ILLEGAL-TRANSITION");
    }

    [Fact]
    public async Task Reverify_then_decide_a_pending_demand_blocks_the_decision_and_a_COVERED_decision_clears_the_in_question_gate()
    {
        var policy = _h.Policy();
        var claim = await _h.OpenClaimAsync(policy);
        _h.Supersede(policy, ["MTPL"]);
        await _h.PublishChangedAsync(policy, DaysAgo(3));
        await _h.DrainAsync();
        var newRef = (await _h.ClaimAsync(claim)).Text("pendingReverification.newSnapshotRef");

        // The snapshot compare: no decision while the demand is open.
        var (blocked, blockedBody) = await DecideAsync(claim, "COVERED");
        blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict, blockedBody?.ToJsonString());
        blockedBody.Text("code").ShouldBe("CLM-ERR-SNAPSHOT-MISMATCH");

        (await _h.ReverifyAsync(claim, "ADOPT", newRef, reason: "POLICY_CORRECTED")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _h.ClaimAsync(claim)).Text("exposures.0.coverageIndication").ShouldBe("IN_QUESTION");
        (await _h.Money.BuildAsync(claim, [Payment(claim, 100m, Guid.NewGuid().ToString())])).Body.Text("code").ShouldBe("CLM-ERR-COVERAGE-IN-QUESTION");

        var (response, body) = await DecideAsync(claim, "COVERED", reason: "GOODWILL");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("coverageInQuestion").ShouldBe("false");
        body.Text("coverageIndication").ShouldBe("COVERED");

        var after = await _h.ClaimAsync(claim);
        after.Text("summary.coverageInQuestion").ShouldBe("false");
        after.Text("exposures.0.coverageDecision").ShouldBe("COVERED");
        (await _h.Money.BuildAsync(claim, [Payment(claim, 100m, Guid.NewGuid().ToString())])).Body.Text("code").ShouldNotBe("CLM-ERR-COVERAGE-IN-QUESTION");
    }

    [Fact]
    public async Task The_authority_limit_applies_and_four_eyes_are_required_above_fifty_thousand()
    {
        var claim = await _h.OpenClaimAsync(_h.Policy());

        // A reserve of 12 000 (the manager builds it; a second manager approves a referral if there is one).
        await ReserveAsync(claim, 12000m, 12000m);

        // The handler's limit is 10 000: refused. The manager decides.
        var (handler, handlerBody) = await DecideAsync(claim, "COVERED");
        handler.StatusCode.ShouldBe(HttpStatusCode.Forbidden, handlerBody?.ToJsonString());
        handlerBody.Text("code").ShouldBe("CLM-ERR-AUTHORITY");
        (await DecideAsync(claim, "COVERED", roles: Manager, user: ManagerUser)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Above 50 000 even the manager cannot decide alone.
        await ReserveAsync(claim, 45000m, 57000m);
        var (four, fourBody) = await DecideAsync(claim, "DENIED", roles: Manager, user: ManagerUser);
        four.StatusCode.ShouldBe(HttpStatusCode.Forbidden, fourBody?.ToJsonString());
        fourBody.Text("code").ShouldBe("CLM-ERR-AUTHORITY");
        fourBody.Text("detail").ShouldContain("FOUR_EYES_REQUIRED");
        (await _h.ClaimAsync(claim)).Text("exposures.0.coverageDecision").ShouldBe("COVERED");
    }

    private async Task ReserveAsync(MoneyClaim claim, decimal amount, decimal expectedTotal)
    {
        var set = await _h.Money.BuildAndSubmitAsync(claim, [Reserve(claim, amount)], Manager, ManagerUser);
        if (set.Text("status") == "PENDING_APPROVAL")
        {
            var (decided, decidedBody) = await _h.Money.DecideAsync(set.Text("approvalRequestId"), user: "manager-maria");
            decided.StatusCode.ShouldBe(HttpStatusCode.OK, decidedBody?.ToJsonString());
            await _h.DrainAsync();
        }

        Amount((await _h.Money.FinancialsAsync(claim))["totals"]!["openReserve"]).ShouldBe(expectedTotal, set.ToJsonString());
    }
}
