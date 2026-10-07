using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Underwriting.Contracts.Events;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy;

/// <summary>
/// SL-POL over HTTP on PostgreSQL 17 with PFC/RAT/UW/MKT sandbox doubles: submission → draft risk data → quote → bind →
/// policy as of validAt/knownAt; charge deltas and events in the bind transaction; errors, idempotency, dry-run,
/// permissions. Requirement ids are in the test names and comments (REQ → test traceability).
/// </summary>
public sealed class PolicyApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private PolicySlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset InTwoDays => DateTimeOffset.UtcNow.AddDays(2);

    public ValueTask InitializeAsync()
    {
        _slice = new PolicySlice(database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    [Fact]
    public async Task REQ_POL_145_011_182_quote_and_bind_create_policy_term_transaction_charges_and_events()
    {
        var party = await _slice.CreatePartyAsync();
        var effective = InTwoDays;

        // REQ-POL-145 / REQ-POL-047: a Draft submission with a job (quote) number from PLT numbering.
        var (created, submission) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/submissions", PolicySlice.Submission(party, effective));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, submission?.ToJsonString());
        var jobId = submission.Text("jobId");
        var policyId = submission.Text("policyId");
        created.Headers.Location!.ToString().ShouldBe($"/api/pol/v1/jobs/{jobId}");
        submission.Text("jobNumber").ShouldMatch("^Q[0-9]{9}$");
        submission.Text("state").ShouldBe("DRAFT");
        submission.Text("productVersion").ShouldBe("1.0");
        submission.Text("manifest.ratingArtefactHash").ShouldBe(PolicySlice.RatingArtefactHash);

        // REQ-POL-010, -036, -277, -279, -280, -282, -292, -149: risk data with static locators and a normalised plate.
        var (first, draft) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/jobs/update-draft",
            new { jobId, versionNo = 1, expectedDraftVersion = 0, instructions = PolicySlice.MotorRisk() });
        first.StatusCode.ShouldBe(HttpStatusCode.OK, draft?.ToJsonString());
        var vehicle = draft.Text("riskTree.vehicles.0.locator");
        Guid.Parse(vehicle).Version.ShouldBe(7);
        draft.Text("riskTree.vehicles.0.plate").ShouldBe("ikx-1234");
        draft.Text("riskTree.vehicles.0.plateNormalised").ShouldNotBe("null");
        draft.Text("riskTree.questionSets.0.questionSetCode").ShouldBe("GR_MOTOR_PREQUAL");
        draft!["validation"]!.AsArray().Select(v => v!["code"]!.GetValue<string>()).ShouldContain("COVERAGE_REQUIRED");
        var (second, filled) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/jobs/update-draft",
            new { jobId, versionNo = 1, expectedDraftVersion = 1, instructions = PolicySlice.DriverAndCovers(vehicle, party) });
        second.StatusCode.ShouldBe(HttpStatusCode.OK, filled?.ToJsonString());
        filled!["validation"]!.AsArray().ShouldBeEmpty();
        filled.Text("draftVersion").ShouldBe("2");

        // REQ-POL-011, -115, -123, -124, -126, -157: RAT rates, MKT rounds, UW accepts, Draft → Quoted.
        var (quoted, quote) = await _slice.QuoteAsync(jobId);
        quoted.StatusCode.ShouldBe(HttpStatusCode.OK, quote?.ToJsonString());
        quote.Text("state").ShouldBe("QUOTED");
        quote.Text("decision").ShouldBe("ACCEPT");
        quote.Text("referred").ShouldBe("false");
        quote.Text("premium.amount").ShouldBe("432.36");
        quote.Text("taxes.amount").ShouldBe("46.85");
        quote.Text("total.amount").ShouldBe("479.21");
        quote!["charges"]!.AsArray().Count.ShouldBe(3);
        quote.Text("worksheetId").ShouldBe(PolicySlice.WorksheetId);
        var rated = (Modules.Rating.Contracts.Api.RateRateRequest)_slice.Rating.CallsTo("rat.Rate.rate").Single().Arguments[0]!;
        rated.Envelope.RatingArtefactHash.Value.ShouldBe(PolicySlice.RatingArtefactHash);
        rated.Envelope.Lineage!.JobId!.Value.Value.ShouldBe(Guid.Parse(jobId));
        var uw = (Modules.Underwriting.Contracts.Api.RulesEvaluateRequest)_slice.Underwriting.CallsTo("uw.Rules.evaluate").Single().Arguments[0]!;
        uw.Checkpoint.ShouldBe(Modules.Underwriting.Contracts.Api.RulesEvaluateRequest.CheckpointValue.PreQuote);

        // REQ-POL-003, -030, -182, -119, -125, -005: bind.
        var (bound, bind) = await _slice.BindAsync(jobId);
        bound.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        bind.Text("state").ShouldBe("BOUND");
        bind.Text("policyId").ShouldBe(policyId);
        bind.Text("policyNumber").ShouldMatch("^POL[0-9]{9}$");
        bind.Text("termNumber").ShouldBe("1");
        bind.Text("termState").ShouldBe("SCHEDULED");
        bind!["gateResults"]!.AsArray().All(g => g!["passed"]!.GetValue<bool>()).ShouldBeTrue();
        bind["chargeDeltas"]!.AsArray().Count.ShouldBe(3);
        var transactionId = bind.Text("transactionId");
        var termId = bind.Text("termId");

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<string>(dataSource, $"SELECT state FROM pol.job WHERE job_id = '{jobId}'")).ShouldBe("BOUND");
        (await ScalarAsync<string>(dataSource, $"SELECT kind FROM pol.policy_transaction WHERE transaction_id = '{transactionId}'")).ShouldBe("ISSUANCE");
        (await ScalarAsync<decimal>(dataSource, $"SELECT total FROM pol.policy_transaction WHERE transaction_id = '{transactionId}'")).ShouldBe(479.21m);
        (await ScalarAsync<decimal>(dataSource, $"SELECT sum(amount) FROM pol.charge_line WHERE transaction_id = '{transactionId}'")).ShouldBe(479.21m);
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM pol.segment WHERE term_id = '{termId}' AND recorded_to IS NULL")).ShouldBe(1);

        // Events in the same transaction: SubmissionCreated, QuoteIssued, ChargeDeltaEmitted ×3 (one complete set), PolicyBound.
        (await ScalarAsync<string>(dataSource, $"SELECT string_agg(event_type, ',' ORDER BY aggregate_sequence) FROM plt.outbox_message WHERE aggregate_id = '{policyId}'"))
            .ShouldBe("SubmissionCreated,QuoteIssued,ChargeDeltaEmitted,ChargeDeltaEmitted,ChargeDeltaEmitted,PolicyBound");
        (await ScalarAsync<long>(dataSource,
                $"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ChargeDeltaEmitted' AND set_id = '{transactionId}' AND set_size = 3 AND business_keys->>'transactionId' = '{transactionId}'"))
            .ShouldBe(3);
        (await ScalarAsync<string>(dataSource, $"SELECT payload->>'paymentPlanRef' FROM plt.outbox_message WHERE event_type = 'PolicyBound' AND aggregate_id = '{policyId}'"))
            .ShouldBe("TEST_PLAN_ANNUAL");
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM plt.audit_event WHERE operation LIKE 'pol.%' AND outcome = 'Succeeded' AND business_keys->>'jobId' = '{jobId}'"))
            .ShouldBe(5);
        (await ScalarAsync<string>(dataSource, $"SELECT payload::text FROM plt.outbox_message WHERE aggregate_id = '{policyId}' AND event_type = 'PolicyBound'"))
            .ShouldNotContain("Παπαδοπούλου");

        // REQ-POL-331: the job view.
        var (job, view) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/jobs/{jobId}", roles: Billing);
        job.StatusCode.ShouldBe(HttpStatusCode.OK);
        view.Text("job.state").ShouldBe("BOUND");
        view.Text("job.policyNumber").ShouldBe(bind.Text("policyNumber"));
        view.Text("job.versions.0.state").ShouldBe("QUOTED");
        view.Text("job.versions.0.total.amount").ShouldBe("479.21");
    }

    [Fact]
    public async Task REQ_POL_002_084_131_policy_get_is_bitemporal_as_of_validAt_and_knownAt()
    {
        var party = await _slice.CreatePartyAsync();
        var effective = InTwoDays;
        var (jobId, _, _) = await _slice.DraftAsync(party, effective);
        (await _slice.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var beforeBind = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        await Task.Delay(20, Ct);
        var (_, bind) = await _slice.BindAsync(jobId);
        var policyId = bind.Text("policyId");
        string At(DateTimeOffset t) => Uri.EscapeDataString(t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture));

        // Known now, valid during the term: term, segment and risk tree; Scheduled → InForce derived on read (REQ-POL-131).
        var inTerm = effective.AddDays(10);
        var (now, current) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/policies/{policyId}?validAt={At(inTerm)}", roles: Billing);
        now.StatusCode.ShouldBe(HttpStatusCode.OK, current?.ToJsonString());
        current.Text("policy.policyNumber").ShouldBe(bind.Text("policyNumber"));
        current.Text("policy.legalEntity").ShouldBe("GR-TEST");
        current.Text("term.state").ShouldBe("IN_FORCE");
        current.Text("policy.status").ShouldBe("IN_FORCE");
        current.Text("segment.transactionId").ShouldBe(bind.Text("transactionId"));
        current.Text("riskTree.vehicles.0.make").ShouldBe("Toyota");
        current!["transactions"]!.AsArray().Count.ShouldBe(1);
        current.Text("transactions.0.kind").ShouldBe("ISSUANCE");
        current["charges"]!.AsArray().Count.ShouldBe(3);

        // Valid today (before the start): the term is not yet valid; nothing to show for that instant.
        var (today, beforeStart) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/policies/{policyId}", roles: Billing);
        today.StatusCode.ShouldBe(HttpStatusCode.OK);
        beforeStart!["term"].ShouldBeNull();
        beforeStart["segment"].ShouldBeNull();

        // As known before the bind: the policy did not exist yet.
        var (unknown, problem) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/policies/{policyId}?validAt={At(inTerm)}&knownAt={Uri.EscapeDataString(beforeBind)}", roles: Billing);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound, problem?.ToJsonString());
        problem.Text("code").ShouldBe("POL-ERR-NOT-FOUND");

        // A validAt date means the start of that day in the legal entity's zone; after the term it reads Expired.
        var afterTerm = effective.AddYears(1).AddDays(5).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var (expired, expiredBody) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/terms/{bind.Text("termId")}?validAt={afterTerm}", roles: Billing);
        expired.StatusCode.ShouldBe(HttpStatusCode.OK, expiredBody?.ToJsonString());
        expiredBody.Text("term.state").ShouldBe("EXPIRED");
        expiredBody!["segment"].ShouldBeNull();

        // In process (what CLM, DOC, BIL call): the same answer through IPolicyPolicyService.
        await using var scope = _slice.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        var service = scope.ServiceProvider.GetRequiredService<IPolicyPolicyService>();
        var read = await service.GetAsync(policyId, ValidAt.From(Instant.FromDateTimeOffset(inTerm)), cancellationToken: Ct);
        read.Term!.TermNumber.ShouldBe(1);
        read.Term.State.ShouldBe(TermStateCode.InForce);
    }

    [Fact]
    public async Task REQ_POL_153_editing_a_quoted_job_returns_it_to_draft_on_a_new_version()
    {
        var party = await _slice.CreatePartyAsync();
        var (jobId, draftVersion, _) = await _slice.DraftAsync(party, InTwoDays);
        (await _slice.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (edited, body) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/jobs/update-draft", new
        {
            jobId, versionNo = 1, expectedDraftVersion = draftVersion,
            instructions = new object[] { new { op = "SET_ANSWERS", questionSet = new { questionSetCode = "GR_MOTOR_PREQUAL", answers = new { garagedOvernight = false } } } },
        });
        edited.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("state").ShouldBe("DRAFT");
        body.Text("versionNo").ShouldBe("2");

        var (_, view) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/jobs/{jobId}");
        view.Text("job.currentVersionNo").ShouldBe("2");
        view.Text("job.versions.0.state").ShouldBe("SUPERSEDED");
        view.Text("job.versions.1.state").ShouldBe("DRAFT");

        // The superseded version cannot be bound; version 2 quotes and binds.
        (await _slice.BindAsync(jobId, versionNo: 1)).Body.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");
        (await _slice.QuoteAsync(jobId, versionNo: 2)).Body.Text("state").ShouldBe("QUOTED");
        (await _slice.BindAsync(jobId, versionNo: 2)).Body.Text("state").ShouldBe("BOUND");
    }

    [Fact]
    public async Task REQ_POL_157_003_UW_referral_keeps_the_quote_but_the_bind_gate_fails_without_a_transaction()
    {
        var party = await _slice.CreatePartyAsync();
        var (jobId, _, _) = await _slice.DraftAsync(party, InTwoDays);
        _slice.Raise(BlockingPoint.PreBind);

        var (_, quote) = await _slice.QuoteAsync(jobId);
        quote.Text("state").ShouldBe("QUOTED");
        quote.Text("decision").ShouldBe("REFER");
        quote.Text("referred").ShouldBe("true");
        quote.Text("issues.0.blockingPoint").ShouldBe("PRE_BIND");

        var (gated, bind) = await _slice.BindAsync(jobId);
        gated.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        bind.Text("state").ShouldBe("QUOTED");
        bind!["transactionId"].ShouldBeNull();
        bind["gateResults"]!.AsArray().Single(g => g!["gate"]!.GetValue<string>() == "UW_ISSUES")!["passed"]!.GetValue<bool>().ShouldBeFalse();
        var checkpoints = _slice.Underwriting.CallsTo("uw.Rules.evaluate")
            .Select(c => ((Modules.Underwriting.Contracts.Api.RulesEvaluateRequest)c.Arguments[0]!).Checkpoint.ToString()).ToList();
        checkpoints.ShouldBe(["PreQuote", "PreBind"]);

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var policyId = (await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/jobs/{jobId}")).Body.Text("job.policyId");
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM pol.policy WHERE policy_id = '{policyId}'")).ShouldBe(0);
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{policyId}' AND event_type IN ('PolicyBound', 'ChargeDeltaEmitted')"))
            .ShouldBe(0);

        // Approved in UW: the next bind passes the gate.
        _slice.AcceptAll();
        (await _slice.BindAsync(jobId)).Body.Text("state").ShouldBe("BOUND");
    }

    [Fact]
    public async Task REQ_POL_157_an_issue_blocking_PRE_QUOTE_keeps_the_job_in_draft_referred()
    {
        var party = await _slice.CreatePartyAsync();
        var (jobId, _, _) = await _slice.DraftAsync(party, InTwoDays);
        _slice.Raise(BlockingPoint.PreQuote);

        var (response, quote) = await _slice.QuoteAsync(jobId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, quote?.ToJsonString());
        quote.Text("state").ShouldBe("DRAFT");
        quote.Text("decision").ShouldBe("REFER");
        quote!["validUntil"].ShouldBeNull();
        (await _slice.BindAsync(jobId)).Body.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");
    }

    [Fact]
    public async Task REQ_POL_156_a_UW_decline_marks_the_job_declined()
    {
        var party = await _slice.CreatePartyAsync();
        var (jobId, _, _) = await _slice.DraftAsync(party, InTwoDays);
        (await _slice.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using (var scope = _slice.Factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var context = services.GetRequiredService<RequestContext>();
            context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
            context.Jurisdiction = Jurisdiction.Parse("GR");
            context.ConfigurationHash = ConfigurationHash.Parse(new string('a', 64));
            var session = services.GetRequiredService<DbSession>();
            await using var transaction = await session.BeginTransactionAsync(Ct);
            var payload = new DeclineIssuedV1
            {
                DeclineId = Guid.CreateVersion7(), DeclineNumber = "DCL-TEST-1", JobId = new JobId(Guid.Parse(jobId)), Scope = "FULL",
                ReasonCodes = ["TEST_REASON"], DeclinedPairs = [], Automated = false,
            };
            var envelope = services.GetRequiredService<IEventPublisher>().Publish(new OutgoingEvent(
                EventDescriptor.From(DeclineIssuedV1.Descriptor), "Job", jobId, payload, BusinessKeys.Empty.With("declineId", payload.DeclineId.ToString())));
            var handler = services.GetRequiredService<EventHandlerRegistry>().Find("POL.DeclineIssued.DeclineJob")!;
            await handler.Invoke(services, envelope, Ct);
            await handler.Invoke(services, envelope, Ct); // replay: no change, no failure
            await transaction.CommitAsync(Ct);
        }

        var (_, view) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/jobs/{jobId}");
        view.Text("job.state").ShouldBe("DECLINED");
        (await _slice.BindAsync(jobId)).Body.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");
    }

    [Fact]
    public async Task REQ_POL_070_071_129_bind_replays_by_key_and_a_dry_run_leaves_no_number_row_or_event()
    {
        var party = await _slice.CreatePartyAsync();
        var (jobId, _, _) = await _slice.DraftAsync(party, InTwoDays);
        (await _slice.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var policyId = (await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/jobs/{jobId}")).Body.Text("job.policyId");

        var (dry, preview) = await _slice.BindAsync(jobId, dryRun: true);
        dry.StatusCode.ShouldBe(HttpStatusCode.OK, preview?.ToJsonString());
        preview.Text("state").ShouldBe("BOUND");
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM pol.policy WHERE policy_id = '{policyId}'")).ShouldBe(0);
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{policyId}' AND event_type = 'PolicyBound'")).ShouldBe(0);

        var key = Guid.NewGuid();
        var (first, bind) = await _slice.BindAsync(jobId, key: key);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());

        // Gapless (REQ-POL-030): the dry run did not consume the number it showed.
        bind.Text("policyNumber").ShouldBe(preview.Text("policyNumber"));

        var (replay, again) = await _slice.BindAsync(jobId, key: key);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        again.Text("transactionId").ShouldBe(bind.Text("transactionId"));
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{policyId}'")).ShouldBe(1);

        // A new key is a new command: the job is already Bound.
        (await _slice.BindAsync(jobId)).Body.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");
    }

    [Fact]
    public async Task REQ_POL_056_concurrent_binds_of_one_job_bind_once()
    {
        var party = await _slice.CreatePartyAsync();
        var (jobId, _, _) = await _slice.DraftAsync(party, InTwoDays);
        (await _slice.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _slice.BindAsync(jobId)));
        results.Count(r => r.Response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        results.Where(r => r.Response.StatusCode != HttpStatusCode.OK).Select(r => r.Body.Text("code"))
            .ShouldAllBe(code => code == "POL-ERR-STALE" || code == "POL-ERR-ILLEGAL-TRANSITION");
    }

    [Fact]
    public async Task REQ_POL_072_typed_precondition_errors()
    {
        var party = await _slice.CreatePartyAsync();

        // REQ-POL-137: new business never before now.
        var (past, pastBody) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/submissions", PolicySlice.Submission(party, DateTimeOffset.UtcNow.AddDays(-1)));
        past.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        pastBody.Text("code").ShouldBe("POL-ERR-EFFDATE-LIMIT");

        // Unknown policyholder in PTY.
        var (unknown, unknownBody) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/submissions", PolicySlice.Submission(Guid.CreateVersion7().ToString(), InTwoDays));
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unknownBody.Text("code").ShouldBe("POL-ERR-VALIDATION");

        var (jobId, draftVersion, _) = await _slice.DraftAsync(party, InTwoDays);

        // Optimistic concurrency of the draft.
        var (stale, staleBody) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/jobs/update-draft",
            new { jobId, versionNo = 1, expectedDraftVersion = draftVersion - 1, instructions = new object[] { new { op = "REMOVE_DRIVER", locator = "x" } } });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        staleBody.Text("code").ShouldBe("POL-ERR-STALE");

        // Unknown locator.
        var (bad, badBody) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/jobs/update-draft",
            new { jobId, versionNo = 1, expectedDraftVersion = draftVersion, instructions = new object[] { new { op = "REMOVE_VEHICLE", locator = "no-such" } } });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        badBody.Text("code").ShouldBe("POL-ERR-VALIDATION");

        // Bind before quote; bind without confirmation (REQ-POL-181).
        (await _slice.BindAsync(jobId)).Body.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");
        (await _slice.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (unconfirmed, unconfirmedBody) = await _slice.BindAsync(jobId, confirmation: false);
        unconfirmed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        unconfirmedBody.Text("code").ShouldBe("POL-ERR-HUMAN-CONFIRMATION-REQUIRED");

        // REQ-POL-148: a quick quote is never bound.
        var (quick, _, _) = await _slice.DraftAsync(party, InTwoDays, quoteType: "QUICK");
        (await _slice.QuoteAsync(quick)).Body.Text("bindable").ShouldBe("false");
        (await _slice.BindAsync(quick)).Body.Text("code").ShouldBe("POL-ERR-QUICK-QUOTE-NOT-BINDABLE");

        // RAT failure.
        _slice.Rating.Fail("rat.Rate.rate", new CoreIns.Platform.Errors.DomainException(CoreIns.SharedKernel.Results.DomainError.Of(ModuleCode.RAT, "ARTEFACT")));
        var (rating, _, _) = await _slice.DraftAsync(party, InTwoDays);
        var (failed, failedBody) = await _slice.QuoteAsync(rating);
        failed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        failedBody.Text("code").ShouldBe("POL-ERR-RATING");
    }

    [Fact]
    public async Task Permissions_are_enforced_per_operation()
    {
        var party = await _slice.CreatePartyAsync();
        var (forbidden, _) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/submissions", PolicySlice.Submission(party, InTwoDays), roles: Billing);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (jobId, _, _) = await _slice.DraftAsync(party, InTwoDays);
        (await _slice.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _slice.BindAsync(jobId, roles: Billing)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Idempotency-Key is required on commands.
        var (keyless, keylessBody) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/jobs/quote", new { jobId, versionNo = 1 }, withKey: false);
        keyless.StatusCode.ShouldBe(HttpStatusCode.BadRequest, keylessBody?.ToJsonString());
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
