using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Claims.Domain;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static CoreIns.IntegrationTests.Claims.ClaimsSlice;

namespace CoreIns.IntegrationTests.Claims;

/// <summary>
/// The CLM reference vertical (SL2-CLM-CORE) over HTTP on a real PostgreSQL 17 with a scripted POL snapshot: FNOL submit,
/// validate and get, claim get/search/close, exposure create; numbering, coverage, state machine, events, P2, races.
/// Requirement ids are in the test names and comments (REQ → test map of the work package report).
/// </summary>
public sealed class ClaimsApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string SecretDescription = "SECRET-DESC";
    private const string SecretLocation = "Συγγρού 120";
    private ClaimsSlice _slice = null!;

    public ValueTask InitializeAsync()
    {
        _slice = new ClaimsSlice(database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    [Fact]
    public async Task REQ_CLM_001_002_043_044_061_Submit_opens_a_claim_with_a_gapless_number_snapshot_reference_events_and_audit()
    {
        var policy = _slice.Policy();
        var (response, body) = await _slice.SubmitAsync(Fnol(policy));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var claimId = body.Text("claimId");
        body.Text("claimNumber").ShouldMatch("^CLM[0-9]{9}$");
        body.Text("claim.status").ShouldBe("OPEN");
        body.Text("claim.subStatus").ShouldBe("NEW");
        body.Text("claim.snapshotStatus").ShouldBe("VERIFIED");
        body.Text("claim.snapshotRef").ShouldStartWith($"SNAP-{policy.PolicyId:N}-");
        body.Text("claim.policyNumber").ShouldBe(policy.PolicyNumber);
        body.Text("claim.insuredPartyId").ShouldBe(policy.InsuredPartyId.ToString());
        body.Text("claim.policyInForceAtLoss").ShouldBe("true");
        body.Text("claim.coverageInQuestion").ShouldBe("false");
        body.Text("claim.productVersion").ShouldBe("1.0");
        body.Text("claim.handlingSegment").ShouldBe("STANDARD");
        body.Text("exposures.0.exposureNumber").ShouldBe(body.Text("claimNumber") + "-001");
        body.Text("exposures.0.coverageIndication").ShouldBe("COVERED");
        body.Text("exposures.0.coverageDecision").ShouldBe("PENDING");
        body!.ToJsonString().ShouldNotContain(SecretDescription);

        // REQ-CLM-002: the snapshot was read at the loss instant (validAt) and known now.
        _slice.Snapshots.CallsTo("pol.Snapshot.get").ShouldNotBeEmpty();

        // REQ-CLM-005: the three events are in the outbox on the claim's aggregate with a gap-free sequence 1..3.
        (await database.ScalarAsync<string>(
                $"SELECT string_agg(event_type || ':' || aggregate_sequence, ',' ORDER BY aggregate_sequence) FROM plt.outbox_message WHERE aggregate_type = 'Claim' AND aggregate_id = '{claimId}'"))
            .ShouldBe("ClaimReported:1,CoverageVerified:2,ExposureCreated:3");
        (await database.ScalarAsync<string>($"SELECT payload->>'snapshotRef' FROM plt.outbox_message WHERE event_type = 'ClaimReported' AND aggregate_id = '{claimId}'"))
            .ShouldBe(body.Text("claim.snapshotRef"));
        (await database.ScalarAsync<string>($"SELECT business_keys->>'policyId' FROM plt.outbox_message WHERE event_type = 'ClaimReported' AND aggregate_id = '{claimId}'"))
            .ShouldBe(policy.PolicyId.ToString());
        (await database.ScalarAsync<string>($"SELECT payload->>'outcome' FROM plt.outbox_message WHERE event_type = 'CoverageVerified' AND aggregate_id = '{claimId}'"))
            .ShouldBe("IN_FORCE");
        (await database.ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = 'clm.Fnol.submit' AND object_id = '{claimId}' AND outcome = 'Succeeded'"))
            .ShouldBe(1);

        // REQ-CLM-044: one immutable FNOL snapshot.
        (await database.ScalarAsync<long>($"SELECT count(*) FROM clm.fnol_snapshot WHERE claim_id = '{claimId}'")).ShouldBe(1);
        var (fnol, fnolBody) = await _slice.SendAsync(HttpMethod.Get, $"/api/clm/v1/fnol/{claimId}");
        fnol.StatusCode.ShouldBe(HttpStatusCode.OK, fnolBody?.ToJsonString());
        fnolBody.Text("payload.description").ShouldContain(SecretDescription);
        fnolBody.Text("claimNumber").ShouldBe(body.Text("claimNumber"));
    }

    [Fact]
    public async Task P2_free_text_is_encrypted_at_rest_and_absent_from_events_audit_idempotency_store_and_lists()
    {
        var policy = _slice.Policy();
        var (response, body) = await _slice.SubmitAsync(Fnol(policy));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var claimId = body.Text("claimId");

        // Ciphertext at rest: neither the claim row nor the FNOL snapshot holds the text in clear.
        (await database.ScalarAsync<long>(
                $"SELECT count(*) FROM clm.claim WHERE claim_id = '{claimId}' AND (position(convert_to('{SecretDescription}', 'UTF8') in description_encrypted) > 0 OR position(convert_to('{SecretLocation}', 'UTF8') in loss_location_encrypted) > 0)"))
            .ShouldBe(0);
        (await database.ScalarAsync<long>(
                $"SELECT count(*) FROM clm.fnol_snapshot WHERE claim_id = '{claimId}' AND position(convert_to('{SecretDescription}', 'UTF8') in payload_encrypted) > 0"))
            .ShouldBe(0);

        // Never in events, audit, the idempotency store.
        (await database.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE payload::text LIKE '%{SecretDescription}%' OR payload::text LIKE '%{SecretLocation}%'")).ShouldBe(0);
        (await database.ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE audit_event::text LIKE '%{SecretDescription}%'")).ShouldBe(0);
        (await database.ScalarAsync<long>($"SELECT count(*) FROM plt.idempotency_record WHERE convert_from(response_body, 'UTF8') LIKE '%{SecretDescription}%'")).ShouldBe(0);

        // Lists (search) never show it; the claim view (clm.Claim.get) does, decrypted.
        var (search, searchBody) = await _slice.SendAsync(HttpMethod.Post, "/api/clm/v1/claims/search", new { policyNumber = policy.PolicyNumber }, withKey: false);
        search.StatusCode.ShouldBe(HttpStatusCode.OK, searchBody?.ToJsonString());
        searchBody!.ToJsonString().ShouldNotContain(SecretDescription);
        searchBody.Text("items.0.claim.claimId").ShouldBe(claimId);
        var (get, view) = await _slice.SendAsync(HttpMethod.Get, $"/api/clm/v1/claims/{claimId}");
        get.StatusCode.ShouldBe(HttpStatusCode.OK, view?.ToJsonString());
        view.Text("claim.description").ShouldContain(SecretDescription);
        view.Text("claim.lossLocation").ShouldContain(SecretLocation);
        view.Text("claim.legalEntity").ShouldBe("GR-TEST");
        view.Text("claim.claimants.0.claimantType").ShouldBe("INSURED");
        view.Text("claim.incidents.0.vehicleRef").ShouldBe("YXA-1234");
    }

    [Fact]
    public async Task REQ_CLM_043_Dry_runs_and_validation_consume_no_claim_number()
    {
        var first = await _slice.SubmitAsync(Fnol(_slice.Policy()));
        first.Response.StatusCode.ShouldBe(HttpStatusCode.OK, first.Body?.ToJsonString());

        for (var i = 0; i < 2; i++)
        {
            var (dry, dryBody) = await _slice.SubmitAsync(Fnol(_slice.Policy()), dryRun: true);
            dry.StatusCode.ShouldBe(HttpStatusCode.OK, dryBody?.ToJsonString());
            (await database.ScalarAsync<long>($"SELECT count(*) FROM clm.claim WHERE claim_id = '{dryBody.Text("claimId")}'")).ShouldBe(0);
            (await database.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{dryBody.Text("claimId")}'")).ShouldBe(0);
        }

        var (validate, validateBody) = await _slice.SendAsync(HttpMethod.Post, "/api/clm/v1/fnol/validate", new JsonObject { ["fnol"] = Fnol(_slice.Policy()) }, withKey: false);
        validate.StatusCode.ShouldBe(HttpStatusCode.OK, validateBody?.ToJsonString());
        validateBody.Text("valid").ShouldBe("true");
        validateBody.Text("coverageIndications.0.indication").ShouldBe("COVERED");

        var second = await _slice.SubmitAsync(Fnol(_slice.Policy()));
        second.Response.StatusCode.ShouldBe(HttpStatusCode.OK, second.Body?.ToJsonString());
        Sequence(second.Body.Text("claimNumber")).ShouldBe(Sequence(first.Body.Text("claimNumber")) + 1);
    }

    [Fact]
    public async Task REQ_CLM_043_D_SL2_07_Concurrent_submits_keep_claim_numbers_gapless_and_unique()
    {
        var tasks = Enumerable.Range(0, 12).Select(_ => _slice.SubmitAsync(Fnol(_slice.Policy()))).ToList();
        var results = await Task.WhenAll(tasks);

        results.ShouldAllBe(r => r.Response.StatusCode == HttpStatusCode.OK);
        var numbers = results.Select(r => Sequence(r.Body.Text("claimNumber"))).Order().ToList();
        numbers.Distinct().Count().ShouldBe(12);
        (numbers[^1] - numbers[0]).ShouldBe(11, "no gaps between concurrent submits");
    }

    [Fact]
    public async Task REQ_CLM_030_A_missing_loss_date_is_FNOL_001_naming_the_field_and_creates_nothing()
    {
        var policy = _slice.Policy();
        var (response, body) = await _slice.SubmitAsync(Fnol(policy, change: b => b.Remove("lossAt")));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body?.ToJsonString());
        body.Text("code").ShouldBe("CLM-ERR-FNOL-001");
        body!.ToJsonString().ShouldContain("lossAt");
        (await database.ScalarAsync<long>($"SELECT count(*) FROM clm.claim WHERE policy_id = '{policy.PolicyId}'")).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_CLM_030_A_future_loss_or_a_loss_after_the_notice_date_is_refused()
    {
        var policy = _slice.Policy();
        var (future, futureBody) = await _slice.SubmitAsync(Fnol(policy, lossAt: DaysAgo(-1)));
        future.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, futureBody?.ToJsonString());
        futureBody.Text("code").ShouldBe("CLM-ERR-LOSS-DATE");

        var (late, lateBody) = await _slice.SubmitAsync(Fnol(policy, lossAt: DaysAgo(5), change: b => b["noticeOn"] = DaysAgo(10).ToBusinessDate(TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens")).ToString()));
        late.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, lateBody?.ToJsonString());
        lateBody.Text("code").ShouldBe("CLM-ERR-LOSS-DATE");
        (await database.ScalarAsync<long>($"SELECT count(*) FROM clm.claim WHERE policy_id = '{policy.PolicyId}'")).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_CLM_049_A_loss_outside_the_term_creates_the_claim_flagged_coverage_in_question()
    {
        var policy = _slice.Policy();
        var (response, body) = await _slice.SubmitAsync(Fnol(policy, lossAt: DaysAgo(150)));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("claim.policyInForceAtLoss").ShouldBe("false");
        body.Text("claim.coverageInQuestion").ShouldBe("true");
        body.Text("claim.policyStatusAtLoss").ShouldBe("NO_TERM_AT_INSTANT");
        body.Text("exposures.0.coverageIndication").ShouldBe("IN_QUESTION");
        body.Text("exposures.0.coverageDecision").ShouldBe("PENDING");
        (await database.ScalarAsync<string>($"SELECT payload->>'outcome' FROM plt.outbox_message WHERE event_type = 'CoverageVerified' AND aggregate_id = '{body.Text("claimId")}'"))
            .ShouldBe("NOT_IN_FORCE");
    }

    [Fact]
    public async Task REQ_CLM_048_A_coverage_absent_from_the_snapshot_is_indicated_not_covered()
    {
        var policy = _slice.Policy("MTPL");
        var (response, body) = await _slice.SubmitAsync(Fnol(policy));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("exposures.0.coverageIndication").ShouldBe("NOT_COVERED");
        body!["coverageIndications"]!.AsArray().Select(i => i.Text("coverageCode") + "=" + i.Text("indication")).ShouldBe(["MTPL=COVERED", "OD=NOT_COVERED"], ignoreOrder: true);
    }

    [Fact]
    public async Task REQ_CLM_002_050_An_unknown_policy_is_unverified_and_an_unreachable_POL_is_503()
    {
        var unknown = Fnol(_slice.Policy(), change: b => b["policyId"] = Guid.NewGuid().ToString());
        var (response, body) = await _slice.SubmitAsync(unknown);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body?.ToJsonString());
        body.Text("code").ShouldBe("CLM-ERR-POLICY-UNVERIFIED");

        await using var down = new ClaimsSlice(database.AppConnectionString);
        down.Snapshots.Fail("pol.Snapshot.get", new InvalidOperationException("POL failed unexpectedly"));
        var (unavailable, unavailableBody) = await down.SubmitAsync(Fnol(down.Policy()));
        unavailable.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, unavailableBody?.ToJsonString());
        unavailableBody.Text("code").ShouldBe("CLM-ERR-DEPENDENCY-UNAVAILABLE");
    }

    [Fact]
    public async Task REQ_CLM_041_A_probable_duplicate_needs_a_link_or_an_override()
    {
        var policy = _slice.Policy();
        var lossAt = DaysAgo(3);
        var first = await _slice.SubmitAsync(Fnol(policy, lossAt));
        first.Response.StatusCode.ShouldBe(HttpStatusCode.OK, first.Body?.ToJsonString());

        var (duplicate, duplicateBody) = await _slice.SubmitAsync(Fnol(policy, lossAt));
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict, duplicateBody?.ToJsonString());
        duplicateBody.Text("code").ShouldBe("CLM-ERR-DUPLICATE-CANDIDATES");
        duplicateBody!.ToJsonString().ShouldContain(first.Body.Text("claimNumber"));

        var (validate, validateBody) = await _slice.SendAsync(HttpMethod.Post, "/api/clm/v1/fnol/validate", new JsonObject { ["fnol"] = Fnol(policy, lossAt) }, withKey: false);
        validate.StatusCode.ShouldBe(HttpStatusCode.OK);
        validateBody.Text("valid").ShouldBe("false");
        validateBody.Text("duplicateCandidates.0.claimNumber").ShouldBe(first.Body.Text("claimNumber"));

        var (linked, linkedBody) = await _slice.SubmitAsync(Fnol(policy, lossAt, change: b => b["duplicateDecision"] = new JsonObject
        {
            ["action"] = "LINK", ["linkedClaimId"] = first.Body.Text("claimId"), ["reasonCode"] = "SAME_EVENT_SECOND_REPORT",
        }));
        linked.StatusCode.ShouldBe(HttpStatusCode.OK, linkedBody?.ToJsonString());
        linkedBody.Text("claim.duplicateOfClaimId").ShouldBe(first.Body.Text("claimId"));

        var (overridden, overriddenBody) = await _slice.SubmitAsync(Fnol(policy, lossAt, change: b => b["duplicateDecision"] = new JsonObject
        {
            ["action"] = "OVERRIDE", ["reasonCode"] = "DIFFERENT_INCIDENT",
        }));
        overridden.StatusCode.ShouldBe(HttpStatusCode.OK, overriddenBody?.ToJsonString());
        overriddenBody.Text("duplicateCandidates.0.reasons").ShouldContain("SAME_LOSS_DATE");
    }

    [Fact]
    public async Task REQ_CLM_001_A_replayed_key_returns_the_same_claim_and_a_different_payload_is_IDEMPOTENCY_MISMATCH()
    {
        var policy = _slice.Policy();
        var key = Guid.NewGuid();
        var body = Fnol(policy);
        var first = await _slice.SubmitAsync(body, key: key);
        var replay = await _slice.SubmitAsync(body, key: key);

        first.Response.StatusCode.ShouldBe(HttpStatusCode.OK, first.Body?.ToJsonString());
        replay.Response.StatusCode.ShouldBe(HttpStatusCode.OK, replay.Body?.ToJsonString());
        replay.Body.Text("claimId").ShouldBe(first.Body.Text("claimId"));
        (await database.ScalarAsync<long>($"SELECT count(*) FROM clm.claim WHERE policy_id = '{policy.PolicyId}'")).ShouldBe(1);
        (await database.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ClaimReported' AND aggregate_id = '{first.Body.Text("claimId")}'")).ShouldBe(1);

        var (mismatch, mismatchBody) = await _slice.SubmitAsync(Fnol(policy, cause: "THEFT"), key: key);
        mismatch.StatusCode.ShouldBe(HttpStatusCode.Conflict, mismatchBody?.ToJsonString());
        mismatchBody.Text("code").ShouldBe("CLM-ERR-IDEMPOTENCY-MISMATCH");
    }

    [Fact]
    public async Task Permissions_refuse_anonymous_and_non_claims_roles()
    {
        var policy = _slice.Policy();
        (await _slice.SubmitAsync(Fnol(policy), roles: "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _slice.SendAsync(HttpMethod.Post, "/api/clm/v1/fnol/submit", Fnol(policy), roles: null)).Response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _slice.SendAsync(HttpMethod.Post, "/api/clm/v1/claims/search", new { policyNumber = policy.PolicyNumber }, roles: "Staff.Billing", withKey: false))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _slice.SubmitAsync(Fnol(policy), roles: Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task REQ_CLM_062_063_071_Exposure_create_moves_the_claim_to_InProgress_and_refuses_a_duplicate_without_reason()
    {
        var (claimId, version) = await OpenClaimAsync(exposure: false);

        var (created, createdBody) = await CreateExposureAsync(claimId, version);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, createdBody?.ToJsonString());
        createdBody.Text("exposure.exposureNumber").ShouldEndWith("-001");
        createdBody.Text("exposure.coverageIndication").ShouldBe("COVERED");
        createdBody.Text("claim.subStatus").ShouldBe("IN_PROGRESS");
        createdBody.Text("claim.handler").ShouldBe("USER:test-user");
        var current = int.Parse(createdBody.Text("claim.recordVersion"), System.Globalization.CultureInfo.InvariantCulture);

        var (duplicate, duplicateBody) = await CreateExposureAsync(claimId, current);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict, duplicateBody?.ToJsonString());
        duplicateBody.Text("code").ShouldBe("CLM-ERR-EXPOSURE-DUPLICATE");

        var (withReason, withReasonBody) = await CreateExposureAsync(claimId, current, duplicateReason: "SECOND_DAMAGE_AREA");
        withReason.StatusCode.ShouldBe(HttpStatusCode.Created, withReasonBody?.ToJsonString());
        withReasonBody.Text("exposure.exposureNumber").ShouldEndWith("-002");

        var (stale, staleBody) = await CreateExposureAsync(claimId, current, coverage: "MTPL");
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, staleBody?.ToJsonString());
        staleBody.Text("code").ShouldBe("CLM-ERR-STALE");

        // REQ-CLM-005: the claim's events stay on one gap-free sequence.
        (await database.ScalarAsync<string>(
                $"SELECT string_agg(event_type || ':' || aggregate_sequence, ',' ORDER BY aggregate_sequence) FROM plt.outbox_message WHERE aggregate_id = '{claimId}'"))
            .ShouldBe("ClaimReported:1,CoverageVerified:2,ExposureCreated:3,ExposureCreated:4");
    }

    [Fact]
    public async Task REQ_CLM_071_073_Close_closes_claim_and_exposures_publishes_ClaimClosed_and_further_actions_are_illegal()
    {
        var (claimId, version) = await OpenClaimAsync();

        var (closed, closedBody) = await CloseAsync(claimId, version, "COMPLETED");
        closed.StatusCode.ShouldBe(HttpStatusCode.OK, closedBody?.ToJsonString());
        closedBody.Text("claim.status").ShouldBe("CLOSED");
        closedBody.Text("claim.outcome").ShouldBe("COMPLETED");
        closedBody!["claim"]!["subStatus"].ShouldBeNull();
        var after = int.Parse(closedBody.Text("claim.recordVersion"), System.Globalization.CultureInfo.InvariantCulture);
        (await database.ScalarAsync<long>($"SELECT count(*) FROM clm.exposure WHERE claim_id = '{claimId}' AND status = 'CLOSED' AND outcome = 'COMPLETED'")).ShouldBe(1);
        (await database.ScalarAsync<string>(
                $"SELECT event_type || ':' || aggregate_sequence || ':' || (payload->>'outcome') FROM plt.outbox_message WHERE aggregate_id = '{claimId}' AND event_type = 'ClaimClosed'"))
            .ShouldBe("ClaimClosed:4:COMPLETED");

        // Closed → close again / new exposure: illegal transitions (REQ-CLM-071), with the current version.
        var (again, againBody) = await CloseAsync(claimId, after, "WITHDRAWN");
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, againBody?.ToJsonString());
        againBody.Text("code").ShouldBe("CLM-ERR-ILLEGAL-TRANSITION");
        var (exposure, exposureBody) = await CreateExposureAsync(claimId, after, coverage: "MTPL");
        exposure.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, exposureBody?.ToJsonString());
        exposureBody.Text("code").ShouldBe("CLM-ERR-ILLEGAL-TRANSITION");

        // With the version read before the close: STALE, never ILLEGAL-TRANSITION.
        var (stale, staleBody) = await CloseAsync(claimId, version, "WITHDRAWN");
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, staleBody?.ToJsonString());
        staleBody.Text("code").ShouldBe("CLM-ERR-STALE");

        var (unknown, unknownBody) = await CloseAsync(Guid.NewGuid().ToString(), 1, "COMPLETED");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound, unknownBody?.ToJsonString());
    }

    [Fact]
    public async Task Racing_closes_and_exposure_creates_end_in_one_winner_and_CLM_ERR_STALE_never_500()
    {
        var (claimId, version) = await OpenClaimAsync(exposure: false);
        var exposures = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => CreateExposureAsync(claimId, version, coverage: i % 2 == 0 ? "OD" : "MTPL")));
        exposures.Count(r => r.Response.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        exposures.Where(r => r.Response.StatusCode != HttpStatusCode.Created).ShouldAllBe(r => r.Response.StatusCode == HttpStatusCode.Conflict && r.Body.Text("code") == "CLM-ERR-STALE");

        var current = (int)await database.ScalarAsync<int>($"SELECT record_version FROM clm.claim WHERE claim_id = '{claimId}'");
        var closes = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => CloseAsync(claimId, current, "NO_PAYMENT")));
        closes.Count(r => r.Response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        closes.Where(r => r.Response.StatusCode != HttpStatusCode.OK).ShouldAllBe(r => r.Response.StatusCode == HttpStatusCode.Conflict && r.Body.Text("code") == "CLM-ERR-STALE");
        (await database.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{claimId}' AND event_type = 'ClaimClosed'")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_CLM_072_The_close_guard_refuses_while_the_financial_seam_reports_open_reserve_or_pending_payment()
    {
        await using var guarded = new ClaimsSlice(database.AppConnectionString, services => services.Replace(ServiceDescriptor.Scoped<IClaimFinancialGuard, OpenReserveGuard>()));
        var policy = guarded.Policy();
        var (submitted, body) = await guarded.SubmitAsync(Fnol(policy));
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());

        var (refused, refusedBody) = await guarded.SendAsync(HttpMethod.Post, "/api/clm/v1/claims/close", new
        {
            claimId = body.Text("claimId"), expectedRecordVersion = int.Parse(body.Text("claim.recordVersion"), System.Globalization.CultureInfo.InvariantCulture), outcome = "COMPLETED",
        });
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, refusedBody?.ToJsonString());
        refusedBody.Text("code").ShouldBe("CLM-ERR-CLOSE-GUARD");
        refusedBody!.ToJsonString().ShouldContain("OPEN_RESERVE");
        (await database.ScalarAsync<string>($"SELECT status FROM clm.claim WHERE claim_id = '{body.Text("claimId")}'")).ShouldBe("OPEN");
    }

    [Fact]
    public async Task REQ_CLM_011_Search_by_claim_number_policy_number_and_insured_party_never_in_a_URL_for_personal_criteria()
    {
        var policy = _slice.Policy();
        var first = await _slice.SubmitAsync(Fnol(policy, DaysAgo(4)));
        var second = await _slice.SubmitAsync(Fnol(policy, DaysAgo(3), cause: "THEFT"));
        first.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (byNumber, byNumberBody) = await _slice.SendAsync(HttpMethod.Get, $"/api/clm/v1/claims/search?claimNumber={first.Body.Text("claimNumber")}");
        byNumber.StatusCode.ShouldBe(HttpStatusCode.OK, byNumberBody?.ToJsonString());
        byNumberBody!["items"]!.AsArray().Count.ShouldBe(1);

        var (byParty, byPartyBody) = await _slice.SendAsync(HttpMethod.Post, "/api/clm/v1/claims/search?limit=1", new { insuredPartyId = policy.InsuredPartyId }, withKey: false);
        byParty.StatusCode.ShouldBe(HttpStatusCode.OK, byPartyBody?.ToJsonString());
        byPartyBody.Text("items.0.claim.claimNumber").ShouldBe(second.Body.Text("claimNumber"));
        var cursor = byPartyBody.Text("nextCursor");
        var (page2, page2Body) = await _slice.SendAsync(HttpMethod.Post, $"/api/clm/v1/claims/search?limit=1&cursor={cursor}", new { insuredPartyId = policy.InsuredPartyId }, withKey: false);
        page2.StatusCode.ShouldBe(HttpStatusCode.OK);
        page2Body.Text("items.0.claim.claimNumber").ShouldBe(first.Body.Text("claimNumber"));
        page2Body!["nextCursor"].ShouldBeNull();

        var (none, noneBody) = await _slice.SendAsync(HttpMethod.Post, "/api/clm/v1/claims/search", new { }, withKey: false);
        none.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, noneBody?.ToJsonString());
        noneBody.Text("code").ShouldBe("CLM-ERR-SEARCH-CRITERIA");
    }

    [Fact]
    public async Task A_claim_of_one_legal_entity_is_not_found_searched_or_closed_from_another()
    {
        var policy = _slice.Policy();
        var (response, body) = await _slice.SubmitAsync(Fnol(policy));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var claimId = body.Text("claimId");

        // A second synthetic entity in the MKT registry, served by its own stamp on the same database.
        await database.ExecuteAsSuperuserAsync(
            """
            INSERT INTO mkt.legal_entity
                (legal_entity_id, code, native_name, latin_name, home_jurisdiction, pack_id, functional_currency, timezone, status, is_test_entity, record_version, created_at)
            VALUES ('0192f0c4-0000-7000-8000-0000000000c2', 'GR-OTHER', 'GR-OTHER (synthetic)', 'GR-OTHER (synthetic)', 'GR', 'gr', 'EUR', 'Europe/Athens', 'ACTIVE', true, 1, now())
            ON CONFLICT (legal_entity_id) DO NOTHING
            """, Ct);
        await using var other = new ClaimsSlice(database.AppConnectionString, settings: new Dictionary<string, string?> { ["Stamp:LegalEntity"] = "GR-OTHER" });

        (await other.SendAsync(HttpMethod.Get, $"/api/clm/v1/claims/{claimId}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.SendAsync(HttpMethod.Get, $"/api/clm/v1/fnol/{claimId}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var (search, searchBody) = await other.SendAsync(HttpMethod.Post, "/api/clm/v1/claims/search", new { policyNumber = policy.PolicyNumber }, withKey: false);
        search.StatusCode.ShouldBe(HttpStatusCode.OK, searchBody?.ToJsonString());
        searchBody!["items"]!.AsArray().ShouldBeEmpty();
        var (close, closeBody) = await other.SendAsync(HttpMethod.Post, "/api/clm/v1/claims/close", new JsonObject { ["claimId"] = claimId, ["expectedRecordVersion"] = 1, ["outcome"] = "WITHDRAWN" });
        close.StatusCode.ShouldBe(HttpStatusCode.NotFound, closeBody?.ToJsonString());
        (await database.ScalarAsync<string>($"SELECT status FROM clm.claim WHERE claim_id = '{claimId}'")).ShouldBe("OPEN");
    }

    [Fact]
    public async Task In_process_contract_reads_the_claim_in_the_callers_legal_entity()
    {
        var (claimId, _) = await OpenClaimAsync();
        await using var scope = _slice.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CoreIns.Platform.Context.RequestContext>();
        context.Actor = CoreIns.SharedKernel.Identifiers.ActorRef.User("in-process-test");
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        var claims = scope.ServiceProvider.GetRequiredService<CoreIns.Modules.Claims.Contracts.IClaimsClaimService>();
        var claim = await claims.GetAsync(claimId, Ct);
        claim.Claim.Summary.ClaimId.Value.ToString().ShouldBe(claimId);
        claim.Claim.Exposures.Count.ShouldBe(1);
    }

    private async Task<(string ClaimId, int Version)> OpenClaimAsync(bool exposure = true)
    {
        var (response, body) = await _slice.SubmitAsync(Fnol(_slice.Policy(), exposure: exposure));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return (body.Text("claimId"), int.Parse(body.Text("claim.recordVersion"), System.Globalization.CultureInfo.InvariantCulture));
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> CreateExposureAsync(string claimId, int version, string coverage = "OD", string? duplicateReason = null) =>
        _slice.SendAsync(HttpMethod.Post, "/api/clm/v1/exposures", new JsonObject
        {
            ["claimId"] = claimId, ["expectedRecordVersion"] = version, ["kind"] = "OWN_DAMAGE", ["coverageCode"] = coverage, ["duplicateReason"] = duplicateReason,
        });

    private Task<(HttpResponseMessage Response, JsonNode? Body)> CloseAsync(string claimId, int version, string outcome) =>
        _slice.SendAsync(HttpMethod.Post, "/api/clm/v1/claims/close", new JsonObject { ["claimId"] = claimId, ["expectedRecordVersion"] = version, ["outcome"] = outcome });

    private static long Sequence(string claimNumber) => long.Parse(claimNumber[3..], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The financials seam as SL2-CLM-MONEY will fill it: here every exposure still has EUR 200.00 open reserve.</summary>
    private sealed class OpenReserveGuard : IClaimFinancialGuard
    {
        public Task<IReadOnlyList<ExposureFinancialPosition>> PositionsAsync(ClaimId claimId, IReadOnlyList<ExposureId> exposures, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExposureFinancialPosition>>([.. exposures.Select(e => new ExposureFinancialPosition(e, 200.00m, false))]);
    }
}
