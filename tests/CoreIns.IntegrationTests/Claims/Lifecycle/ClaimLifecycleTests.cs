using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;

namespace CoreIns.IntegrationTests.Claims.Lifecycle;

/// <summary>
/// BO-CLM-02: claim reopen (reason, clocks, close guard, audit), merge of a duplicate claim (no money moves, refused with money) and the
/// FNOL draft (no claim number, no events, own drafts only, submit completes it) over HTTP on a real PostgreSQL.
/// </summary>
public sealed class ClaimLifecycleTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
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
        int.Parse((await _money.ClaimAsync(claim)).Text("summary.recordVersion"), CultureInfo.InvariantCulture);

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> ReopenAsync(MoneyClaim claim, string? reason = "NEW_INFORMATION", string roles = Manager, int? version = null) =>
        await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/claims/reopen",
            new { claimId = claim.ClaimId, expectedRecordVersion = version ?? await VersionAsync(claim), reasonCode = reason }, roles, ManagerUser);

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> MergeAsync(MoneyClaim survivor, MoneyClaim duplicate, string roles = Manager) =>
        await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/claims/merge",
            new { survivorClaimId = survivor.ClaimId, duplicateClaimId = duplicate.ClaimId, expectedRecordVersion = await VersionAsync(duplicate), reasonCode = "SAME_EVENT_SECOND_REPORT" },
            roles, ManagerUser);

    private Task<long> EventCountAsync(string claimId, string type) => _money.ScalarAsync<long>(
        $"SELECT count(*) FROM plt.outbox_message WHERE aggregate_type = 'Claim' AND aggregate_id = '{claimId}' AND event_type = '{type}'");

    private Task<string> StatusAsync(MoneyClaim claim) => _money.ScalarAsync<string>(
        $"SELECT status || '/' || coalesce(sub_status, '-') || '/' || coalesce(outcome, '-') FROM clm.claim WHERE claim_id = '{claim.ClaimId}'");

    [Fact]
    public async Task Reopen_restores_the_claim_and_its_exposures_keeps_the_close_guard_and_is_audited_with_the_reason()
    {
        var claim = await ClaimAsync();
        (await _money.CloseAsync(claim, await VersionAsync(claim))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StatusAsync(claim)).ShouldBe("CLOSED/-/COMPLETED");

        (await ReopenAsync(claim, reason: null)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReopenAsync(claim, roles: Handler)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReopenAsync(claim, version: 99)).Body.Text("code").ShouldBe("CLM-ERR-STALE");

        var (reopened, body) = await ReopenAsync(claim);
        reopened.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("claim.status").ShouldBe("OPEN");
        (await StatusAsync(claim)).ShouldBe("OPEN/IN_PROGRESS/-");
        (await _money.ScalarAsync<long>($"SELECT reopen_count FROM clm.claim WHERE claim_id = '{claim.ClaimId}'")).ShouldBe(1);
        (await _money.ScalarAsync<string>($"SELECT status FROM clm.exposure WHERE exposure_id = '{claim.ExposureId}'")).ShouldBe("OPEN");
        (await EventCountAsync(claim.ClaimId, "ClaimReopened")).ShouldBe(1);
        (await EventCountAsync(claim.ClaimId, "ExposureReopened")).ShouldBe(1);
        (await _money.ScalarAsync<string>($"SELECT payload->>'reopenReason' FROM plt.outbox_message WHERE event_type = 'ClaimReopened' AND aggregate_id = '{claim.ClaimId}'"))
            .ShouldBe("NEW_INFORMATION");
        (await _money.ScalarAsync<long>(
            $"SELECT count(*) FROM plt.audit_event WHERE operation = 'clm.Claim.reopen' AND outcome = 'Succeeded' AND object_id = '{claim.ClaimId}' AND changes::text LIKE '%NEW_INFORMATION%'"))
            .ShouldBe(1);

        // An open claim cannot be reopened again.
        (await ReopenAsync(claim)).Body.Text("code").ShouldBe("CLM-ERR-ILLEGAL-TRANSITION");

        // The money path works on the reopened claim, and the close guard still reads the reserve.
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 120m)]);
        var (blocked, blockedBody) = await _money.CloseAsync(claim, await VersionAsync(claim));
        blocked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, blockedBody?.ToJsonString());
        blockedBody.Text("code").ShouldBe("CLM-ERR-CLOSE-GUARD");
    }

    [Fact]
    public async Task Reopening_a_claim_starts_the_MTPL_clocks_that_the_close_cancelled_afresh()
    {
        var claim = await ClaimAsync();
        var (created, createdBody) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/exposures", new
        {
            claimId = claim.ClaimId, expectedRecordVersion = await VersionAsync(claim), kind = "MTPL_BI", coverageCode = "MTPL", claimantPartyId = Guid.CreateVersion7(),
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, createdBody?.ToJsonString());
        var id = createdBody.Text("exposure.exposureId");

        (await _money.CloseAsync(claim, await VersionAsync(claim))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Clocks(claim, id)).ShouldAllBe(c => c!.Text("status") == "CANCELLED");

        (await ReopenAsync(claim)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await Clocks(claim, id);
        after.Count.ShouldBe(2);
        after.ShouldAllBe(c => c!.Text("status") == "RUNNING");
    }

    private async Task<JsonArray> Clocks(MoneyClaim claim, string exposureId) =>
        (await _money.ClaimAsync(claim))["exposures"]!.AsArray().Single(e => e!.Text("exposureId") == exposureId)!["statutoryClocks"]!.AsArray();

    [Fact]
    public async Task Merge_closes_the_duplicate_with_a_pointer_keeps_both_numbers_and_the_survivor_open()
    {
        var policy = _slice.Policy();
        var lossAt = ClaimsSlice.DaysAgo(3);
        var survivor = await _money.OpenClaimAsync(ClaimsSlice.Fnol(policy, lossAt));
        var duplicate = await _money.OpenClaimAsync(ClaimsSlice.Fnol(policy, lossAt, change: b => b["duplicateDecision"] =
            new JsonObject { ["action"] = "OVERRIDE", ["reasonCode"] = "DIFFERENT_INCIDENT" }));
        var survivorVersion = await VersionAsync(survivor);

        (await MergeAsync(survivor, duplicate, roles: Handler)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await MergeAsync(survivor, survivor)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var (merged, body) = await MergeAsync(survivor, duplicate);
        merged.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("duplicate.claimNumber").ShouldBe(duplicate.ClaimNumber);
        body.Text("duplicate.duplicateOfClaimId").ShouldBe(survivor.ClaimId);
        body.Text("survivor.claimNumber").ShouldBe(survivor.ClaimNumber);
        (await StatusAsync(duplicate)).ShouldBe("CLOSED/-/DUPLICATE");
        (await StatusAsync(survivor)).ShouldBe("OPEN/NEW/-");
        (await VersionAsync(survivor)).ShouldBe(survivorVersion);
        (await EventCountAsync(duplicate.ClaimId, "ClaimClosed")).ShouldBe(1);
        (await _money.ScalarAsync<string>($"SELECT payload->>'outcome' FROM plt.outbox_message WHERE event_type = 'ClaimClosed' AND aggregate_id = '{duplicate.ClaimId}'"))
            .ShouldBe("DUPLICATE");
        (await _money.ScalarAsync<long>(
            $"SELECT count(*) FROM plt.audit_event WHERE operation = 'clm.Claim.merge' AND outcome = 'Succeeded' AND object_id = '{duplicate.ClaimId}'")).ShouldBe(1);

        // A merged duplicate is final: it neither reopens nor merges again.
        (await ReopenAsync(duplicate)).Body.Text("code").ShouldBe("CLM-ERR-ILLEGAL-TRANSITION");
        (await MergeAsync(survivor, duplicate)).Body.Text("code").ShouldBe("CLM-ERR-MERGE-NOT-DUPLICATE");
    }

    [Fact]
    public async Task Merge_is_refused_for_other_policies_and_while_the_duplicate_has_money()
    {
        var one = await ClaimAsync();
        var other = await ClaimAsync();
        var (differs, differsBody) = await MergeAsync(one, other);
        differs.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, differsBody?.ToJsonString());
        differsBody.Text("code").ShouldBe("CLM-ERR-MERGE-NOT-DUPLICATE");

        var policy = _slice.Policy();
        var lossAt = ClaimsSlice.DaysAgo(4);
        var survivor = await _money.OpenClaimAsync(ClaimsSlice.Fnol(policy, lossAt));
        var duplicate = await _money.OpenClaimAsync(ClaimsSlice.Fnol(policy, lossAt, change: b => b["duplicateDecision"] =
            new JsonObject { ["action"] = "OVERRIDE", ["reasonCode"] = "DIFFERENT_INCIDENT" }));
        await _money.BuildAndSubmitAsync(duplicate, [Reserve(duplicate, 200m)]);

        var (refused, body) = await MergeAsync(survivor, duplicate);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body?.ToJsonString());
        body.Text("code").ShouldBe("CLM-ERR-MERGE-FINANCIALS");
        body!["errors"]!.AsArray().Select(e => e!.Text("code")).ShouldContain("FINANCIAL_TRANSACTIONS");
        (await StatusAsync(duplicate)).ShouldBe("OPEN/NEW/-");
        (await EventCountAsync(duplicate.ClaimId, "ClaimClosed")).ShouldBe(0);
    }

    [Fact]
    public async Task A_draft_is_saved_and_resumed_without_a_claim_number_events_or_list_entry_and_submit_completes_it()
    {
        var policy = _slice.Policy();
        var claimsBefore = await _money.ScalarAsync<long>("SELECT count(*) FROM clm.claim");
        var eventsBefore = await _money.ScalarAsync<long>("SELECT count(*) FROM plt.outbox_message WHERE event_type LIKE 'Claim%' OR event_type LIKE 'Exposure%'");

        var partial = new JsonObject { ["lineOfBusiness"] = "MOTOR", ["policyId"] = policy.PolicyId.ToString(), ["description"] = "Draft text (synthetic SECRET-DRAFT)" };
        var (saved, savedBody) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/fnol/save-draft", new { fnolPayload = partial });
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, savedBody?.ToJsonString());
        var draftId = savedBody.Text("draftId");
        savedBody.Text("recordVersion").ShouldBe("1");
        savedBody!.ToJsonString().ShouldNotContain("SECRET-DRAFT");

        (await _money.ScalarAsync<long>("SELECT count(*) FROM clm.claim")).ShouldBe(claimsBefore);
        (await _money.ScalarAsync<long>("SELECT count(*) FROM plt.outbox_message WHERE event_type LIKE 'Claim%' OR event_type LIKE 'Exposure%'")).ShouldBe(eventsBefore);
        (await _money.ScalarAsync<bool>($"SELECT position('SECRET-DRAFT'::bytea in payload_encrypted) = 0 FROM clm.fnol_draft WHERE draft_id = '{draftId}'")).ShouldBeTrue();

        // Update with the right version; a stale version loses.
        var full = ClaimsSlice.Fnol(policy);
        var (updated, updatedBody) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/fnol/save-draft", new { draftId, expectedRecordVersion = 1, fnolPayload = full });
        updated.StatusCode.ShouldBe(HttpStatusCode.OK, updatedBody?.ToJsonString());
        updatedBody.Text("recordVersion").ShouldBe("2");
        (await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/fnol/save-draft", new { draftId, expectedRecordVersion = 1, fnolPayload = full }))
            .Body.Text("code").ShouldBe("CLM-ERR-STALE");

        // Only the creator reads it back.
        var (resumed, resumedBody) = await _money.SendAsync(HttpMethod.Get, $"/api/clm/v1/fnol/drafts/{draftId}");
        resumed.StatusCode.ShouldBe(HttpStatusCode.OK, resumedBody?.ToJsonString());
        resumedBody.Text("status").ShouldBe("DRAFT");
        resumedBody.Text("fnolPayload.lossCause").ShouldBe("COLLISION");
        (await _money.SendAsync(HttpMethod.Get, $"/api/clm/v1/fnol/drafts/{draftId}", user: "handler-other")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/fnol/save-draft", new { draftId, expectedRecordVersion = 2, fnolPayload = full }, user: "handler-other"))
            .Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Submitting the draft runs the ordinary FNOL path; the draft becomes SUBMITTED and cannot be submitted twice.
        full["draftId"] = draftId;
        var (submitted, submittedBody) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/fnol/submit", full);
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, submittedBody?.ToJsonString());
        (await _money.ScalarAsync<long>("SELECT count(*) FROM clm.claim")).ShouldBe(claimsBefore + 1);
        (await _money.ScalarAsync<string>($"SELECT status || '/' || claim_id FROM clm.fnol_draft WHERE draft_id = '{draftId}'"))
            .ShouldBe("SUBMITTED/" + submittedBody.Text("claimId"));

        var second = ClaimsSlice.Fnol(_slice.Policy());
        second["draftId"] = draftId;
        var (again, againBody) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/fnol/submit", second);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict, againBody?.ToJsonString());
        againBody.Text("code").ShouldBe("CLM-ERR-STALE");
    }
}
