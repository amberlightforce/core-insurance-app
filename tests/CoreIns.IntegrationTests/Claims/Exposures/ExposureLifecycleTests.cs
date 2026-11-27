using System.Net;
using System.Text.Json.Nodes;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;

namespace CoreIns.IntegrationTests.Claims.Exposures;

/// <summary>
/// BO-CLM-01: exposure update, close (D-CON-04 guard: open reserve, pending payment) and reopen (coded reason) over HTTP on a real PostgreSQL,
/// with the claim close guard still reading the exposure state.
/// </summary>
public sealed class ExposureLifecycleTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ClaimsSlice _slice = null!;
    private ClaimsMoney _money = null!;

    public ValueTask InitializeAsync()
    {
        _slice = new ClaimsSlice(database.AppConnectionString);
        _money = new ClaimsMoney(_slice.Factory, _slice.Client, database.SuperuserConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private Task<MoneyClaim> ClaimAsync() => _money.OpenClaimAsync(ClaimsSlice.Fnol(_slice.Policy()));

    private async Task<int> VersionAsync(MoneyClaim claim) =>
        int.Parse((await _money.ClaimAsync(claim)).Text("summary.recordVersion"), System.Globalization.CultureInfo.InvariantCulture);

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> CloseExposureAsync(MoneyClaim claim, Guid? key = null, string roles = Handler) =>
        await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/exposures/close",
            new { exposureId = claim.ExposureId, expectedRecordVersion = await VersionAsync(claim), outcome = "COMPLETED" }, roles, key: key);

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> ReopenExposureAsync(MoneyClaim claim, string? reason = "NEW_INFORMATION") =>
        await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/exposures/reopen",
            new { exposureId = claim.ExposureId, expectedRecordVersion = await VersionAsync(claim), reasonCode = reason });

    private Task<long> EventCountAsync(MoneyClaim claim, string type) => _money.ScalarAsync<long>(
        $"SELECT count(*) FROM plt.outbox_message WHERE aggregate_type = 'Claim' AND aggregate_id = '{claim.ClaimId}' AND event_type = '{type}'");

    [Fact]
    public async Task Close_is_blocked_by_an_open_reserve_then_succeeds_after_release_and_publishes_ExposureClosed()
    {
        var claim = await ClaimAsync();
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 300m)]);

        var (blocked, blockedBody) = await CloseExposureAsync(claim);
        blocked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, blockedBody?.ToJsonString());
        blockedBody.Text("code").ShouldBe("CLM-ERR-CLOSE-GUARD");
        blockedBody.Text("errors.0.code").ShouldBe("OPEN_RESERVE");
        blockedBody.Text("errors.0.exposureIds.0").ShouldBe(claim.ExposureId);
        (await EventCountAsync(claim, "ExposureClosed")).ShouldBe(0);

        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, -300m, reason: "RELEASE")]);
        var (closed, body) = await CloseExposureAsync(claim);
        closed.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("exposure.status").ShouldBe("CLOSED");
        body.Text("exposure.outcome").ShouldBe("COMPLETED");
        (await EventCountAsync(claim, "ExposureClosed")).ShouldBe(1);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = 'clm.Exposure.close' AND outcome = 'Succeeded' AND object_id = '{claim.ExposureId}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Reopen_needs_a_reason_then_the_exposure_can_close_again()
    {
        var claim = await ClaimAsync();
        (await CloseExposureAsync(claim)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ReopenExposureAsync(claim, reason: null)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (reopened, body) = await ReopenExposureAsync(claim);
        reopened.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("exposure.status").ShouldBe("OPEN");
        (await _money.ScalarAsync<string>($"SELECT payload->>'reasonCode' FROM plt.outbox_message WHERE event_type = 'ExposureReopened' AND aggregate_id = '{claim.ClaimId}'"))
            .ShouldBe("NEW_INFORMATION");

        // An open exposure cannot be reopened; a reopened one closes again.
        (await ReopenExposureAsync(claim)).Body.Text("code").ShouldBe("CLM-ERR-ILLEGAL-TRANSITION");
        (await CloseExposureAsync(claim)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await EventCountAsync(claim, "ExposureClosed")).ShouldBe(2);
    }

    [Fact]
    public async Task The_claim_close_guard_sees_the_exposure_state_and_a_closed_claim_refuses_exposure_reopen()
    {
        var claim = await ClaimAsync();
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 150m)]);
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, -150m, reason: "RELEASE")]);
        (await CloseExposureAsync(claim)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReopenExposureAsync(claim)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 50m)]);

        // The reopened exposure holds a reserve again: closing the claim is refused through the same guard.
        var (blocked, blockedBody) = await _money.CloseAsync(claim, await VersionAsync(claim));
        blocked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, blockedBody?.ToJsonString());
        blockedBody.Text("code").ShouldBe("CLM-ERR-CLOSE-GUARD");

        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, -50m, reason: "RELEASE")]);
        (await _money.CloseAsync(claim, await VersionAsync(claim))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The claim closed with the exposure: nothing left to reopen on a closed claim.
        (await ReopenExposureAsync(claim)).Body.Text("code").ShouldBe("CLM-ERR-ILLEGAL-TRANSITION");
    }

    [Fact]
    public async Task An_idempotent_retry_replays_the_first_response_and_a_second_close_is_an_illegal_transition()
    {
        var claim = await ClaimAsync();
        var key = Guid.NewGuid();
        var request = new { exposureId = claim.ExposureId, expectedRecordVersion = await VersionAsync(claim), outcome = "COMPLETED" };

        var (first, firstBody) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/exposures/close", request, key: key);
        var (retry, retryBody) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/exposures/close", request, key: key);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, firstBody?.ToJsonString());
        retry.StatusCode.ShouldBe(HttpStatusCode.OK, retryBody?.ToJsonString());
        retryBody.Text("exposure.exposureId").ShouldBe(firstBody.Text("exposure.exposureId"));
        (await EventCountAsync(claim, "ExposureClosed")).ShouldBe(1);

        var (again, againBody) = await CloseExposureAsync(claim);
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, againBody?.ToJsonString());
        againBody.Text("code").ShouldBe("CLM-ERR-ILLEGAL-TRANSITION");
    }

    [Fact]
    public async Task Stale_unknown_and_forbidden_requests_are_refused()
    {
        var claim = await ClaimAsync();
        var (stale, staleBody) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/exposures/close",
            new { exposureId = claim.ExposureId, expectedRecordVersion = 99, outcome = "COMPLETED" });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, staleBody?.ToJsonString());
        staleBody.Text("code").ShouldBe("CLM-ERR-STALE");

        var (missing, missingBody) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/exposures/close",
            new { exposureId = Guid.NewGuid(), expectedRecordVersion = 1, outcome = "COMPLETED" });
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound, missingBody?.ToJsonString());

        (await CloseExposureAsync(claim, roles: "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Update_refuses_an_incident_of_another_claim_and_a_closed_exposure()
    {
        var claim = await ClaimAsync();
        var (response, body) = await _money.SendAsync(HttpMethod.Patch, $"/api/clm/v1/exposures/{claim.ExposureId}",
            new { expectedRecordVersion = await VersionAsync(claim), incidentId = Guid.NewGuid() });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body?.ToJsonString());

        var (ok, okBody) = await _money.SendAsync(HttpMethod.Patch, $"/api/clm/v1/exposures/{claim.ExposureId}",
            new { expectedRecordVersion = await VersionAsync(claim), incidentId = (Guid?)null });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, okBody?.ToJsonString());

        (await CloseExposureAsync(claim)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (closed, closedBody) = await _money.SendAsync(HttpMethod.Patch, $"/api/clm/v1/exposures/{claim.ExposureId}",
            new { expectedRecordVersion = await VersionAsync(claim), incidentId = (Guid?)null });
        closed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, closedBody?.ToJsonString());
        closedBody.Text("code").ShouldBe("CLM-ERR-ILLEGAL-TRANSITION");
    }
}
