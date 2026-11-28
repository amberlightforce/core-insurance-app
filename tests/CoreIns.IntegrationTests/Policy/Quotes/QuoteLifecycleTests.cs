using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Platform.Contracts.Common;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Quotes;

/// <summary>
/// BO-POL-03: the quote job lifecycle over HTTP on PostgreSQL with RAT and UW doubles: <c>pol.Job.list</c>, <c>newVersion</c>,
/// <c>requote</c> (explicit artefact choice), <c>copyVersion</c> and <c>notTaken</c> (idempotent, closes the quote).
/// </summary>
public sealed class QuoteLifecycleTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private PolicySlice _slice = null!;

    private static DateTimeOffset InTwoDays => DateTimeOffset.UtcNow.AddDays(2);

    public async ValueTask InitializeAsync()
    {
        _slice = new PolicySlice(database.AppConnectionString);
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private async Task<(string JobId, string PolicyId)> QuotedJobAsync(string? party = null)
    {
        party ??= await _slice.CreatePartyAsync();
        var (jobId, _, _) = await _slice.DraftAsync(party, InTwoDays);
        var (quoted, quote) = await _slice.QuoteAsync(jobId);
        quoted.StatusCode.ShouldBe(HttpStatusCode.OK, quote?.ToJsonString());
        quote.Text("state").ShouldBe("QUOTED");
        var (_, job) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/jobs/{jobId}");
        return (jobId, job.Text("job.policyId"));
    }

    private async Task<JsonNode> JobAsync(string jobId)
    {
        var (response, body) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/jobs/{jobId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["job"]!;
    }

    private async Task<int> RecordVersionAsync(string jobId) => int.Parse((await JobAsync(jobId)).Text("recordVersion"));

    private static string VersionState(JsonNode job, int versionNo) =>
        job["versions"]!.AsArray().Single(v => v!["versionNo"]!.GetValue<int>() == versionNo)!.Text("state");

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(string path, object body, Guid? key = null) =>
        await SendAsync(_slice.Client, HttpMethod.Post, path, body, key: key);

    private async Task<long> CountAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task New_version_copies_the_current_quote_into_an_editable_draft_and_old_versions_stay_immutable()
    {
        var (jobId, _) = await QuotedJobAsync();
        var before = await JobAsync(jobId);
        var recordVersion = int.Parse(before.Text("recordVersion"));

        // A stale record version is refused and changes nothing.
        var (stale, staleBody) = await PostAsync("/api/pol/v1/jobs/new-version", new { jobId, sourceVersion = 1, expectedRecordVersion = recordVersion + 5 });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        staleBody.Text("code").ShouldBe("POL-ERR-STALE");

        var key = Guid.NewGuid();
        var request = new { jobId, sourceVersion = 1, expectedRecordVersion = recordVersion };
        var (created, body) = await PostAsync("/api/pol/v1/jobs/new-version", request, key);
        created.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("versionNo").ShouldBe("2");
        body.Text("state").ShouldBe("DRAFT");
        body.Text("supersededVersionNo").ShouldBe("1");

        var job = await JobAsync(jobId);
        job.Text("state").ShouldBe("DRAFT");
        job.Text("currentVersionNo").ShouldBe("2");
        VersionState(job, 1).ShouldBe("SUPERSEDED");
        VersionState(job, 2).ShouldBe("DRAFT");
        job["versions"]![1]!["riskTree"]!.ToJsonString().ShouldBe(job["versions"]![0]!["riskTree"]!.ToJsonString());
        job["versions"]![1]!["total"].ShouldBeNull();
        job["versions"]![0]!["total"].ShouldNotBeNull();

        // The same call replays (same key, same payload): no third version.
        var (replay, replayBody) = await PostAsync("/api/pol/v1/jobs/new-version", request, key);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        replayBody.Text("versionNo").ShouldBe("2");
        (await JobAsync(jobId)).Text("currentVersionNo").ShouldBe("2");

        // A Draft job has no quoted version to branch: refused. The old version cannot be bound.
        var (again, againBody) = await PostAsync("/api/pol/v1/jobs/new-version", new { jobId, sourceVersion = 2, expectedRecordVersion = await RecordVersionAsync(jobId) });
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        againBody.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");
        (await _slice.BindAsync(jobId, versionNo: 1)).Body.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");

        // The new draft is quoted unchanged; only then it is the one version that binds.
        (await _slice.QuoteAsync(jobId, versionNo: 2)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _slice.BindAsync(jobId, versionNo: 1)).Body.Text("code").ShouldBe("POL-ERR-QUOTE-STALE");
        (await _slice.BindAsync(jobId, versionNo: 2)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Requote_says_which_artefact_it_uses_records_it_and_supersedes_the_old_quote()
    {
        var (jobId, _) = await QuotedJobAsync();
        var pin = PolicySlice.RatingArtefactHash;
        var job = await JobAsync(jobId);
        job.Text("ratingArtefactHash").ShouldBe(pin);

        // The artefact has to be named: an unknown mode is refused before anything runs.
        (await PostAsync("/api/pol/v1/jobs/requote", new { jobId, versionNo = 1, expectedRecordVersion = int.Parse(job.Text("recordVersion")) })).Response.IsSuccessStatusCode.ShouldBeFalse();

        // PINNED: RAT is asked for the job's own artefact.
        var (pinned, pinnedBody) = await PostAsync("/api/pol/v1/jobs/requote", new { jobId, versionNo = 1, expectedRecordVersion = int.Parse(job.Text("recordVersion")), ratingArtefact = "PINNED" });
        pinned.StatusCode.ShouldBe(HttpStatusCode.OK, pinnedBody?.ToJsonString());
        pinnedBody.Text("versionNo").ShouldBe("2");
        pinnedBody.Text("state").ShouldBe("QUOTED");
        pinnedBody.Text("supersededVersionNo").ShouldBe("1");
        pinnedBody.Text("ratingArtefact").ShouldBe("PINNED");
        pinnedBody.Text("ratingArtefactHash").ShouldBe(pin);
        pinnedBody.Text("previousRatingArtefactHash").ShouldBe(pin);
        var calls = _slice.Rating.CallsTo("rat.Rate.rate").Select(c => ((RateRateRequest)c.Arguments[0]!).Envelope.RatingArtefactHash).ToList();
        calls.Count.ShouldBe(2);
        calls[1]!.Value.Value.ShouldBe(pin);

        // IN_FORCE_NOW: the pin is released, RAT picks the artefact in force and the job is pinned to it again.
        var (now, nowBody) = await PostAsync("/api/pol/v1/jobs/requote",
            new { jobId, versionNo = 2, expectedRecordVersion = await RecordVersionAsync(jobId), ratingArtefact = "IN_FORCE_NOW" });
        now.StatusCode.ShouldBe(HttpStatusCode.OK, nowBody?.ToJsonString());
        nowBody.Text("versionNo").ShouldBe("3");
        nowBody.Text("ratingArtefact").ShouldBe("IN_FORCE_NOW");
        _slice.Rating.CallsTo("rat.Rate.rate").Select(c => ((RateRateRequest)c.Arguments[0]!).Envelope.RatingArtefactHash).Last().ShouldBeNull();
        (await JobAsync(jobId)).Text("ratingArtefactHash").ShouldBe(pin);

        // Superseded versions never change; only the newest quoted one binds.
        var after = await JobAsync(jobId);
        VersionState(after, 1).ShouldBe("SUPERSEDED");
        VersionState(after, 2).ShouldBe("SUPERSEDED");
        VersionState(after, 3).ShouldBe("QUOTED");
        (await _slice.BindAsync(jobId, versionNo: 2)).Body.Text("code").ShouldBe("POL-ERR-QUOTE-STALE");

        // A hash is for PINNED only.
        (await PostAsync("/api/pol/v1/jobs/requote", new { jobId, versionNo = 3, expectedRecordVersion = await RecordVersionAsync(jobId), ratingArtefact = "IN_FORCE_NOW", ratingArtefactHash = pin }))
            .Response.IsSuccessStatusCode.ShouldBeFalse();

        // The choice is recorded in the audit trail.
        (await CountAsync("SELECT count(*) FROM plt.audit_event t WHERE operation = 'pol.Job.requote' AND outcome = 'Succeeded' AND t::text LIKE '%IN_FORCE_NOW%'")).ShouldBe(1);
        (await _slice.BindAsync(jobId, versionNo: 3)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Requote_with_a_blocking_underwriting_issue_leaves_the_new_quote_referred_and_the_bind_gate_closed()
    {
        var (jobId, _) = await QuotedJobAsync();
        _slice.Raise(BlockingPoint.PreBind);
        var (response, body) = await PostAsync("/api/pol/v1/jobs/requote",
            new { jobId, versionNo = 1, expectedRecordVersion = await RecordVersionAsync(jobId), ratingArtefact = "PINNED" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("referred").ShouldBe("true");
        var (gated, bind) = await _slice.BindAsync(jobId, versionNo: 2);
        gated.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        bind.Text("state").ShouldBe("QUOTED");
        bind!["transactionId"].ShouldBeNull();
    }

    [Fact]
    public async Task Requote_dry_run_prices_but_keeps_nothing()
    {
        var (jobId, _) = await QuotedJobAsync();
        var (response, body) = await PostAsync("/api/pol/v1/jobs/requote?dryRun=true",
            new { jobId, versionNo = 1, expectedRecordVersion = await RecordVersionAsync(jobId), ratingArtefact = "IN_FORCE_NOW" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("versionNo").ShouldBe("2");
        var job = await JobAsync(jobId);
        job.Text("currentVersionNo").ShouldBe("1");
        job.Text("state").ShouldBe("QUOTED");
        VersionState(job, 1).ShouldBe("QUOTED");
    }

    [Fact]
    public async Task Copy_version_restores_an_old_version_or_starts_a_new_job()
    {
        var (jobId, _) = await QuotedJobAsync();
        (await PostAsync("/api/pol/v1/jobs/requote", new { jobId, versionNo = 1, expectedRecordVersion = await RecordVersionAsync(jobId), ratingArtefact = "PINNED" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Restore version 1 as a new version 3 of the same job.
        var (restored, restoredBody) = await PostAsync("/api/pol/v1/jobs/copy-version",
            new { jobId, sourceVersion = 1, target = "NEW_VERSION", expectedRecordVersion = await RecordVersionAsync(jobId) });
        restored.StatusCode.ShouldBe(HttpStatusCode.OK, restoredBody?.ToJsonString());
        restoredBody.Text("versionNo").ShouldBe("3");
        restoredBody.Text("sourceVersionNo").ShouldBe("1");
        restoredBody.Text("state").ShouldBe("DRAFT");
        var job = await JobAsync(jobId);
        job["versions"]![2]!["riskTree"]!.ToJsonString().ShouldBe(job["versions"]![0]!["riskTree"]!.ToJsonString());
        VersionState(job, 2).ShouldBe("SUPERSEDED");

        // Copy into a new job: a fresh Draft submission of its own, same party and product, risk data copied, nothing priced.
        (await _slice.QuoteAsync(jobId, versionNo: 3)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (copied, copiedBody) = await PostAsync("/api/pol/v1/jobs/copy-version",
            new { jobId, sourceVersion = 3, target = "NEW_JOB", expectedRecordVersion = await RecordVersionAsync(jobId) });
        copied.StatusCode.ShouldBe(HttpStatusCode.OK, copiedBody?.ToJsonString());
        var newJobId = copiedBody.Text("jobId");
        newJobId.ShouldNotBe(jobId);
        copiedBody.Text("versionNo").ShouldBe("1");
        copiedBody.Text("state").ShouldBe("DRAFT");
        copiedBody.Text("jobNumber").ShouldMatch("^Q[0-9]{9}$");
        var copy = await JobAsync(newJobId);
        copy.Text("policyholderPartyId").ShouldBe(job.Text("policyholderPartyId"));
        copy.Text("productCode").ShouldBe(job.Text("productCode"));
        copy["versions"]![0]!["riskTree"]!.ToJsonString().ShouldBe(job["versions"]![2]!["riskTree"]!.ToJsonString());
        copy["versions"]![0]!["total"].ShouldBeNull();
        (await JobAsync(jobId)).Text("state").ShouldBe("QUOTED"); // the source is untouched
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'SubmissionCreated' AND business_keys->>'jobId' = '{newJobId}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Not_taken_closes_the_quote_once_blocks_binding_and_is_idempotent()
    {
        var (jobId, policyId) = await QuotedJobAsync();
        var recordVersion = await RecordVersionAsync(jobId);

        (await PostAsync("/api/pol/v1/jobs/not-taken", new { jobId, reasonCode = string.Empty, expectedRecordVersion = recordVersion })).Response.IsSuccessStatusCode.ShouldBeFalse();
        var (stale, staleBody) = await PostAsync("/api/pol/v1/jobs/not-taken", new { jobId, reasonCode = "PRICE", expectedRecordVersion = recordVersion + 3 });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        staleBody.Text("code").ShouldBe("POL-ERR-STALE");

        var (closed, body) = await PostAsync("/api/pol/v1/jobs/not-taken", new { jobId, reasonCode = "PRICE", text = "Went to a competitor", expectedRecordVersion = recordVersion });
        closed.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("state").ShouldBe("NOT_TAKEN");
        body.Text("replayed").ShouldBe("false");
        var job = await JobAsync(jobId);
        job.Text("state").ShouldBe("NOT_TAKEN");
        VersionState(job, 1).ShouldBe("EXPIRED");
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'JobNotTaken' AND aggregate_id = '{policyId}'")).ShouldBe(1);

        // Closed means closed: no bind, no new version, no requote.
        (await _slice.BindAsync(jobId)).Body.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");
        (await PostAsync("/api/pol/v1/jobs/new-version", new { jobId, sourceVersion = 1, expectedRecordVersion = int.Parse(job.Text("recordVersion")) }))
            .Body.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");

        // The same call again, with a fresh key and a stale record version: success, replayed, no second event, first reason kept.
        var (again, againBody) = await PostAsync("/api/pol/v1/jobs/not-taken", new { jobId, reasonCode = "OTHER", expectedRecordVersion = recordVersion });
        again.StatusCode.ShouldBe(HttpStatusCode.OK, againBody?.ToJsonString());
        againBody.Text("replayed").ShouldBe("true");
        againBody.Text("reasonCode").ShouldBe("PRICE");
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'JobNotTaken' AND aggregate_id = '{policyId}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Not_taken_needs_a_quoted_job_and_other_entities_jobs_are_not_found()
    {
        var party = await _slice.CreatePartyAsync();
        var (draftJob, _, _) = await _slice.DraftAsync(party, InTwoDays);
        var (response, body) = await PostAsync("/api/pol/v1/jobs/not-taken", new { jobId = draftJob, reasonCode = "PRICE", expectedRecordVersion = await RecordVersionAsync(draftJob) });
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        body.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");

        var (missing, missingBody) = await PostAsync("/api/pol/v1/jobs/not-taken", new { jobId = Guid.NewGuid(), reasonCode = "PRICE", expectedRecordVersion = 1 });
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missingBody.Text("code").ShouldBe("POL-ERR-NOT-FOUND");
    }

    [Fact]
    public async Task List_filters_by_state_party_and_producer_and_pages_by_keyset_without_repeats()
    {
        var partyA = await _slice.CreatePartyAsync();
        var partyB = await _slice.CreatePartyAsync();
        var (quotedA, _) = await QuotedJobAsync(partyA);
        var (quotedB, _) = await QuotedJobAsync(partyB);
        var (draftB, _, _) = await _slice.DraftAsync(partyB, InTwoDays);
        (await PostAsync("/api/pol/v1/jobs/not-taken", new { jobId = quotedB, reasonCode = "PRICE", expectedRecordVersion = await RecordVersionAsync(quotedB) }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        async Task<List<string>> Ids(string query)
        {
            var (response, body) = await SendAsync(_slice.Client, HttpMethod.Get, "/api/pol/v1/jobs" + query);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
            return [.. body!["items"]!.AsArray().Select(i => i.Text("jobId"))];
        }

        (await Ids($"?partyId={partyA}")).ShouldBe([quotedA]);
        (await Ids($"?partyId={partyB}&state=NOT_TAKEN")).ShouldBe([quotedB]);
        (await Ids($"?partyId={partyB}&state=DRAFT,NOT_TAKEN")).ToHashSet().ShouldBe([draftB, quotedB]);
        (await Ids($"?partyId={partyA}&state=QUOTED&jobType=SUBMISSION")).ShouldBe([quotedA]);
        (await Ids($"?partyId={partyA}&producerCode=NO-SUCH-PRODUCER")).ShouldBeEmpty();

        // Keyset paging: pages of one job each cover the party's jobs once, newest first, and the last page has no cursor.
        var seen = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 5; page++)
        {
            var (response, body) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/jobs?partyId={partyB}&limit=1" + (cursor is null ? string.Empty : $"&cursor={cursor}"));
            response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
            seen.AddRange(body!["items"]!.AsArray().Select(i => i.Text("jobId")));
            cursor = body["nextCursor"] is JsonValue ? body.Text("nextCursor") : null;
            if (cursor is null)
            {
                break;
            }
        }

        seen.ShouldBe([draftB, quotedB]);
        var (bad, badBody) = await SendAsync(_slice.Client, HttpMethod.Get, "/api/pol/v1/jobs?state=NOPE");
        bad.IsSuccessStatusCode.ShouldBeFalse();
        badBody.Text("code").ShouldBe("POL-ERR-VALIDATION");
        (await SendAsync(_slice.Client, HttpMethod.Get, "/api/pol/v1/jobs?cursor=%25%25")).Response.IsSuccessStatusCode.ShouldBeFalse();

        // The row carries what the screens need to act on it.
        var (_, one) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/jobs?partyId={partyA}");
        var item = one!["items"]![0]!;
        item.Text("state").ShouldBe("QUOTED");
        item.Text("recordVersion").ShouldBe((await RecordVersionAsync(quotedA)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        item.Text("total.amount").ShouldBe("479.21");
    }
}
