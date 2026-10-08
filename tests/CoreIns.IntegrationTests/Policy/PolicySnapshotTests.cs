using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy;

/// <summary>
/// pol.Snapshot.get (REQ-POL-007) and pol.Policy.search / searchByCriteria (REQ-POL-014 subset) on PostgreSQL 17: the
/// policy, term and segment in force at a loss date, boundaries, date-form validAt (D-SLC-13), not-in-force results,
/// knownAt history, byte-identical re-reads through the snapshot reference (POL P5), and the number / insured lookup that
/// keeps identifiers out of URLs (D-SLC-05).
/// </summary>
public sealed class PolicySnapshotTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly TimeZoneInfo Athens = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");

    private PolicySlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _slice = new PolicySlice(database.AppConnectionString);
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private sealed record Bound(string PartyId, string PolicyId, string PolicyNumber, string TermId, string TransactionId, DateTime Start, DateTime End);

    private async Task<Bound> BindAsync()
    {
        var party = await _slice.CreatePartyAsync();
        var (jobId, _, _) = await _slice.DraftAsync(party, DateTimeOffset.UtcNow.AddDays(2));
        (await _slice.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (_, bind) = await _slice.BindAsync(jobId);
        var policyId = bind.Text("policyId");
        var (_, policy) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/policies/{policyId}?validAt={Uri.EscapeDataString(Iso(DateTime.UtcNow.AddDays(30)))}");
        var start = DateTime.Parse(policy.Text("term.period.from"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var end = DateTime.Parse(policy.Text("term.period.to"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        return new Bound(party, policyId, bind.Text("policyNumber"), bind.Text("termId"), bind.Text("transactionId"), start, end);
    }

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);

    private static string Day(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, Athens).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Q(string value) => Uri.EscapeDataString(value);

    private async Task<(HttpResponseMessage Response, string Raw)> RawAsync(string path, string roles = Underwriter)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        var response = await _slice.Client.SendAsync(request, Ct);
        return (response, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<JsonNode> SnapshotAsync(string query, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (response, raw) = await RawAsync("/api/pol/v1/snapshots/get?" + query);
        response.StatusCode.ShouldBe(expected, raw);
        return JsonNode.Parse(raw)!;
    }

    [Fact]
    public async Task REQ_POL_007_snapshot_inside_the_term_has_policy_term_segment_risk_and_coverages()
    {
        var p = await BindAsync();
        var inside = p.Start.AddDays(30);

        var snap = await SnapshotAsync($"policyId={p.PolicyId}&validAt={Q(Iso(inside))}");
        snap.Text("inForce").ShouldBe("true");
        snap.Text("status").ShouldBe("IN_FORCE");
        snap["notInForceReason"].ShouldBeNull();
        snap.Text("policy.policyId").ShouldBe(p.PolicyId);
        snap.Text("policy.policyNumber").ShouldBe(p.PolicyNumber);
        snap.Text("policy.insuredPartyId").ShouldBe(p.PartyId);
        snap.Text("policy.productCode").ShouldBe(_slice.Product);
        snap.Text("content.term.termId").ShouldBe(p.TermId);
        snap.Text("content.term.state").ShouldBe("IN_FORCE");
        DateTime.Parse(snap.Text("content.term.period.from"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).ShouldBe(p.Start);
        DateTime.Parse(snap.Text("content.term.period.to"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).ShouldBe(p.End);
        snap.Text("content.productVersion").ShouldBe("1.0");
        snap.Text("content.segment.transactionId").ShouldBe(p.TransactionId);
        snap.Text("content.segment.snapshotHash").ShouldMatch("^[0-9a-f]{64}$");
        snap.Text("content.vehicles.0.make").ShouldBe("Toyota");
        snap["content"]!["coverages"]!.AsArray().Select(c => c!["coverageCode"]!.GetValue<string>()).Order().ToArray().ShouldBe(["MTPL", "OWN-DAMAGE"]);
        snap["content"]!["coverages"]!.AsArray().All(c => c!["selected"]!.GetValue<bool>()).ShouldBeTrue();
        snap.Text("snapshotRef").ShouldMatch(@"^PS1\.[0-9a-f]{32}\.[0-9a-f]{32}\.\d+\.\d+$");
        snap.Text("snapshotRef").ShouldContain(snap.Text("content.segment.segmentId").Replace("-", string.Empty, StringComparison.Ordinal));

        // The same policy by number.
        var byNumber = await SnapshotAsync($"policyNumber={p.PolicyNumber}&validAt={Q(Iso(inside))}");
        byNumber.Text("policy.policyId").ShouldBe(p.PolicyId);
        byNumber.Text("content.segment.segmentId").ShouldBe(snap.Text("content.segment.segmentId"));
    }

    [Fact]
    public async Task REQ_POL_007_041_term_boundaries_are_half_open_start_in_just_before_out_end_out_just_before_in()
    {
        var p = await BindAsync();
        var micro = TimeSpan.FromTicks(10);

        var atStart = await SnapshotAsync($"policyId={p.PolicyId}&validAt={Q(Iso(p.Start))}");
        atStart.Text("inForce").ShouldBe("true");

        var beforeStart = await SnapshotAsync($"policyId={p.PolicyId}&validAt={Q(Iso(p.Start - micro))}");
        beforeStart.Text("inForce").ShouldBe("false");
        beforeStart.Text("notInForceReason").ShouldBe("NO_TERM_AT_INSTANT");
        beforeStart.Text("status").ShouldBe("SCHEDULED");
        beforeStart["content"].ShouldBeNull();
        beforeStart.Text("policy.policyNumber").ShouldBe(p.PolicyNumber);

        (await SnapshotAsync($"policyId={p.PolicyId}&validAt={Q(Iso(p.End - micro))}")).Text("inForce").ShouldBe("true");
        var atEnd = await SnapshotAsync($"policyId={p.PolicyId}&validAt={Q(Iso(p.End))}");
        atEnd.Text("inForce").ShouldBe("false");
        atEnd.Text("status").ShouldBe("EXPIRED");
        atEnd.Text("notInForceReason").ShouldBe("NO_TERM_AT_INSTANT");
        atEnd["content"].ShouldBeNull();
    }

    [Fact]
    public async Task REQ_POL_007_D_SLC_13_a_date_form_validAt_is_the_end_of_that_Athens_business_day()
    {
        var p = await BindAsync();

        // The start day: close of business on the start date is after the start, so the policy is in force "as of" it.
        var startDay = await SnapshotAsync($"policyId={p.PolicyId}&validAt={Day(p.Start)}");
        startDay.Text("inForce").ShouldBe("true");
        var closeOfStartDay = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(p.Start, Athens).Date.AddDays(1), DateTimeKind.Unspecified), Athens).AddTicks(-10);
        DateTime.Parse(startDay.Text("validAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).ShouldBe(closeOfStartDay);

        // The day before the start closes before the start: not in force, scheduled.
        var dayBefore = await SnapshotAsync($"policyId={p.PolicyId}&validAt={Day(p.Start.AddDays(-1))}");
        dayBefore.Text("inForce").ShouldBe("false");
        dayBefore.Text("status").ShouldBe("SCHEDULED");

        // The end date: the term ended during that day, so as of close of business it is expired; the day before is in force.
        (await SnapshotAsync($"policyId={p.PolicyId}&validAt={Day(p.End.AddDays(-1))}")).Text("inForce").ShouldBe("true");
        var endDay = await SnapshotAsync($"policyId={p.PolicyId}&validAt={Day(p.End)}");
        endDay.Text("inForce").ShouldBe("false");
        endDay.Text("status").ShouldBe("EXPIRED");
    }

    [Fact]
    public async Task REQ_POL_007_unknown_or_not_yet_recorded_policies_are_404_and_bad_input_is_422_never_500()
    {
        var p = await BindAsync();

        var unknown = await SnapshotAsync($"policyId={Guid.CreateVersion7()}", HttpStatusCode.NotFound);
        unknown.Text("code").ShouldBe("POL-ERR-NOT-FOUND");
        (await SnapshotAsync("policyNumber=POL000000000", HttpStatusCode.NotFound)).Text("code").ShouldBe("POL-ERR-NOT-FOUND");

        // As known before the policy was recorded.
        (await SnapshotAsync($"policyId={p.PolicyId}&validAt={Q(Iso(p.Start.AddDays(1)))}&knownAt={Q(Iso(DateTime.UtcNow.AddHours(-1)))}", HttpStatusCode.NotFound))
            .Text("code").ShouldBe("POL-ERR-NOT-FOUND");

        // Exactly one identifier; no knownAt in the future; no validAt/knownAt with a reference; well-formed references.
        (await SnapshotAsync("", HttpStatusCode.UnprocessableContent)).Text("code").ShouldBe("POL-ERR-VALIDATION");
        (await SnapshotAsync($"policyId={p.PolicyId}&policyNumber={p.PolicyNumber}", HttpStatusCode.UnprocessableContent)).Text("code").ShouldBe("POL-ERR-VALIDATION");
        (await SnapshotAsync($"policyId={p.PolicyId}&knownAt={Q(Iso(DateTime.UtcNow.AddDays(1)))}", HttpStatusCode.UnprocessableContent)).Text("code").ShouldBe("POL-ERR-VALIDATION");
        (await SnapshotAsync("snapshotRef=garbage", HttpStatusCode.UnprocessableContent)).Text("code").ShouldBe("POL-ERR-VALIDATION");
        var good = (await SnapshotAsync($"policyId={p.PolicyId}&validAt={Q(Iso(p.Start))}")).Text("snapshotRef");
        (await SnapshotAsync($"snapshotRef={Q(good)}&validAt={Q(Iso(p.Start))}", HttpStatusCode.UnprocessableContent)).Text("code").ShouldBe("POL-ERR-VALIDATION");
        (await SnapshotAsync($"policyId={p.PolicyId}&validAt=not-a-date", HttpStatusCode.UnprocessableContent)).Text("code").ShouldBe("POL-ERR-VALIDATION");
        (await SnapshotAsync("policyNumber=%20leading-space", HttpStatusCode.UnprocessableContent)).Text("code").ShouldBe("POL-ERR-VALIDATION");

        // A forged reference with a knownAt in the future is refused (it could answer differently after later changes).
        var parts = good.Split('.');
        parts[4] = ((DateTimeOffset.UtcNow.AddYears(5).ToUnixTimeMilliseconds()) * 1000).ToString(CultureInfo.InvariantCulture);
        var forged = string.Join('.', parts);
        (await SnapshotAsync($"snapshotRef={Q(forged)}", HttpStatusCode.UnprocessableContent)).Text("code").ShouldBe("POL-ERR-VALIDATION");
        await using var scope = _slice.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        var forgedError = await Should.ThrowAsync<DomainException>(() =>
            scope.ServiceProvider.GetRequiredService<IPolicySnapshotService>().GetAsync(snapshotRef: forged, cancellationToken: Ct));
        forgedError.Error.Code.ToString().ShouldBe("POL-ERR-VALIDATION");

        // Permission: the operation is granted to Staff.Underwriter (and admin), not to billing.
        (await RawAsync($"/api/pol/v1/snapshots/get?policyId={p.PolicyId}", Billing)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task REQ_POL_007_080_knownAt_before_a_later_change_returns_the_old_view_and_the_same_ref_is_byte_identical()
    {
        var p = await BindAsync();
        var inside = Iso(p.Start.AddDays(30));
        var original = await RawAsync($"/api/pol/v1/snapshots/get?policyId={p.PolicyId}&validAt={Q(inside)}");
        var originalNode = JsonNode.Parse(original.Raw)!;
        var originalRef = originalNode.Text("snapshotRef");
        originalNode["content"]!["coverages"]!.AsArray().Count.ShouldBe(2);

        await Task.Delay(50, Ct);
        var beforeChange = DateTime.UtcNow;
        await Task.Delay(50, Ct);
        await SupersedeSegmentAsync(p);

        // As known now: the later change (no coverages, a new segment) is what is in force at the same valid instant.
        var now = await SnapshotAsync($"policyId={p.PolicyId}&validAt={Q(inside)}");
        now["content"]!["coverages"]!.AsArray().Count.ShouldBe(0);
        now.Text("content.segment.segmentId").ShouldNotBe(originalNode.Text("content.segment.segmentId"));
        now.Text("snapshotRef").ShouldNotBe(originalRef);

        // As known before the change: the old view, with the old segment.
        var old = await SnapshotAsync($"policyId={p.PolicyId}&validAt={Q(inside)}&knownAt={Q(Iso(beforeChange))}");
        old["content"]!["coverages"]!.AsArray().Count.ShouldBe(2);
        old.Text("content.segment.segmentId").ShouldBe(originalNode.Text("content.segment.segmentId"));

        // The reference taken before the change re-reads byte for byte (POL P5), HTTP and in process.
        var reread = await RawAsync($"/api/pol/v1/snapshots/get?snapshotRef={Q(originalRef)}");
        reread.Response.StatusCode.ShouldBe(HttpStatusCode.OK, reread.Raw);
        // Only the live supersession metadata may differ: the content, its ref and knownAt are byte-identical (D-SL3-03 c).
        string Stable(string raw) { var node = JsonNode.Parse(raw)!.AsObject(); node.Remove("supersession"); return node.ToJsonString(); }
        Stable(reread.Raw).ShouldBe(Stable(original.Raw));
        JsonNode.Parse(reread.Raw)!["supersession"]!["superseded"]!.GetValue<bool>().ShouldBeTrue();
        JsonNode.Parse(original.Raw)!["supersession"]!["superseded"]!.GetValue<bool>().ShouldBeFalse();
        Stable((await RawAsync($"/api/pol/v1/snapshots/get?snapshotRef={Q(originalRef)}")).Raw).ShouldBe(Stable(original.Raw));

        await using var scope = _slice.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        var service = scope.ServiceProvider.GetRequiredService<IPolicySnapshotService>();
        var first = await service.GetAsync(snapshotRef: originalRef, cancellationToken: Ct);
        var second = await service.GetAsync(snapshotRef: originalRef, cancellationToken: Ct);
        JsonSerializer.Serialize(second, CoreIns.SharedKernel.Json.SharedKernelJson.Options)
            .ShouldBe(JsonSerializer.Serialize(first, CoreIns.SharedKernel.Json.SharedKernelJson.Options));
        first.Supersession!.Superseded.ShouldBeTrue();
        first.Supersession.SuccessorRef.ShouldNotBeNull();
        first.SnapshotRef.ShouldBe(originalRef);
        first.Content!.Coverages.Count.ShouldBe(2);
        first.Policy.PolicyNumber.Value.ShouldBe(p.PolicyNumber);

        // In process by policy number with a loss-date instant (what CLM calls).
        var byNumber = await service.GetAsync(ValidAt.From(Instant.FromDateTimeOffset(new DateTimeOffset(p.Start.AddDays(30), TimeSpan.Zero))), policyNumber: PolicyNumber.Parse(p.PolicyNumber), cancellationToken: Ct);
        byNumber.InForce.ShouldBeTrue();
        byNumber.Content!.Coverages.Count.ShouldBe(0);
    }

    [Fact]
    public async Task REQ_POL_014_D_SLC_05_lookup_by_policy_number_or_insured_party_uses_a_POST_body_never_the_URL()
    {
        var p = await BindAsync();
        var other = await BindAsync();

        // POST body: policy number.
        var (byNumber, numberBody) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/policies/search", new { policyNumber = p.PolicyNumber }, withKey: false);
        byNumber.StatusCode.ShouldBe(HttpStatusCode.OK, numberBody?.ToJsonString());
        byNumber.RequestMessage!.RequestUri!.Query.ShouldBeEmpty();
        byNumber.RequestMessage.RequestUri.ToString().ShouldNotContain(p.PolicyNumber);
        numberBody!["items"]!.AsArray().Count.ShouldBe(1);
        numberBody.Text("items.0.policyId").ShouldBe(p.PolicyId);
        numberBody.Text("items.0.insuredPartyId").ShouldBe(p.PartyId);
        numberBody.Text("items.0.status").ShouldBe("SCHEDULED");
        numberBody["nextCursor"].ShouldBeNull();

        // POST body: insured party id (the party differs per policy here).
        var (byParty, partyBody) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/policies/search", new { insuredPartyId = other.PartyId }, withKey: false);
        byParty.StatusCode.ShouldBe(HttpStatusCode.OK, partyBody?.ToJsonString());
        byParty.RequestMessage!.RequestUri!.Query.ShouldBeEmpty();
        partyBody!["items"]!.AsArray().Single()!["policyId"]!.GetValue<string>().ShouldBe(other.PolicyId);

        // Both criteria are ANDed; a mismatch finds nothing. validAt in the term reads in force.
        var (mismatch, mismatchBody) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/policies/search", new { policyNumber = p.PolicyNumber, insuredPartyId = other.PartyId }, withKey: false);
        mismatch.StatusCode.ShouldBe(HttpStatusCode.OK);
        mismatchBody!["items"]!.AsArray().ShouldBeEmpty();
        var (inForce, inForceBody) = await SendAsync(_slice.Client, HttpMethod.Post, $"/api/pol/v1/policies/search?validAt={Q(Iso(p.Start.AddDays(5)))}", new { policyNumber = p.PolicyNumber }, withKey: false);
        inForce.StatusCode.ShouldBe(HttpStatusCode.OK);
        inForceBody.Text("items.0.status").ShouldBe("IN_FORCE");

        // The GET form keeps only the non-personal policy number.
        var (get, getBody) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/policies/search?policyNumber={p.PolicyNumber}");
        get.StatusCode.ShouldBe(HttpStatusCode.OK, getBody?.ToJsonString());
        getBody.Text("items.0.policyId").ShouldBe(p.PolicyId);

        // Errors: no criterion, malformed number, unknown number (empty page), no permission.
        var (none, noneBody) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/policies/search", new { }, withKey: false);
        none.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        noneBody.Text("code").ShouldBe("POL-ERR-VALIDATION");
        (await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/policies/search", new { policyNumber = "POL000000000" }, withKey: false)).Body!["items"]!.AsArray().ShouldBeEmpty();
        (await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/policies/search", new { policyNumber = p.PolicyNumber }, roles: Billing, withKey: false)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // In process (what FNOL calls).
        await using var scope = _slice.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        var service = scope.ServiceProvider.GetRequiredService<IPolicyPolicyService>();
        var page = await service.SearchByCriteriaAsync(new PolicySearchCriteria { PolicyNumber = PolicyNumber.Parse(p.PolicyNumber) }, cancellationToken: Ct);
        page.Items.Single().PolicyId.Value.ShouldBe(Guid.Parse(p.PolicyId));
    }

    /// <summary>
    /// A later recorded change, as the app role would write it: the current segment is closed at the instant its successor (no
    /// coverages) is recorded, together with the transaction that carries it. Done in one transaction, as the database demands.
    /// </summary>
    private async Task SupersedeSegmentAsync(Bound p)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var command = dataSource.CreateCommand($"""
            BEGIN;
            UPDATE pol.policy SET last_recorded_at = transaction_timestamp(), record_version = record_version + 1 WHERE policy_id = '{p.PolicyId}';
            INSERT INTO pol.policy_transaction (transaction_id, policy_id, term_id, job_id, legal_entity_id, kind, sequence, effective_at, recorded_at,
                configuration_hash, artefact_hash, resolution_hash, intent, premium, taxes, total, currency, actor, correlation_id, origin)
            SELECT '{Guid.CreateVersion7()}', policy_id, term_id, gen_random_uuid(), legal_entity_id, 'CHANGE', 2, effective_at, transaction_timestamp(),
                configuration_hash, artefact_hash, resolution_hash, intent, 0, 0, 0, currency, actor, correlation_id, origin
              FROM pol.policy_transaction WHERE transaction_id = '{p.TransactionId}';
            UPDATE pol.segment SET recorded_to = transaction_timestamp() WHERE term_id = '{p.TermId}' AND recorded_to IS NULL;
            INSERT INTO pol.segment (segment_id, term_id, policy_id, transaction_id, legal_entity_id, valid_from, valid_to, recorded_from, snapshot_hash, snapshot)
            SELECT gen_random_uuid(), s.term_id, s.policy_id, t.transaction_id, s.legal_entity_id, s.valid_from, s.valid_to, transaction_timestamp(),
                '{new string('b', 64)}', jsonb_set(s.snapshot, ARRAY['coverages'], '[]'::jsonb)
              FROM pol.segment s, pol.policy_transaction t
             WHERE s.term_id = '{p.TermId}' AND s.recorded_to = transaction_timestamp() AND t.policy_id = s.policy_id AND t.sequence = 2;
            COMMIT;
            """);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
