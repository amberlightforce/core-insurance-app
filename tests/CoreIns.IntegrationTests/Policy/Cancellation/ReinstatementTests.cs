using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Policy.Renewal;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Cancellation;

/// <summary>BO-POL-07: <c>pol.Reinstatement.create</c> and <c>pol.Rewrite.create</c> on PostgreSQL, shiftable clock.</summary>
public sealed class ReinstatementTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private RenewalHarness _h = null!;

    public async ValueTask InitializeAsync()
    {
        var slice = new PolicySlice(database.AppConnectionString, settings: new Dictionary<string, string?> { ["Platform:Time:Mode"] = "Shiftable" });
        await slice.SeedAsync();
        _h = new RenewalHarness(slice, database.SuperuserConnectionString);
        await _h.BindPolicyAsync();
        await _h.GoToAsync(200);
    }

    public async ValueTask DisposeAsync() => await _h.Slice.DisposeAsync();

    private async Task AdvanceAsync(double days)
    {
        _h.Clock.Advance(TimeSpan.FromDays(days));
        var micros = (long)(_h.Clock.Offset.TotalMilliseconds * 1000);
        await _h.ExecuteAsync($"UPDATE plt.dev_clock SET offset_micros = GREATEST(offset_micros, {micros}), version = version + 1");
    }

    private string Iso(double addDays = 0) => _h.Clock.Now.ToDateTimeOffset().AddDays(addDays).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private async Task CancelAsync()
    {
        var (cancel, body) = await _h.PostAsync(
            "/api/pol/v1/cancellations", new { policyId = _h.PolicyId, source = "Policyholder", reasonCode = "CUSTOMER_REQUEST", effectiveAt = Iso(0), kind = "STANDARD" });
        cancel.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> ReinstateAsync(string type = "NO_LAPSE", double? effectiveInDays = null, bool dryRun = false, string? termId = null) =>
        _h.PostAsync(
            "/api/pol/v1/reinstatements" + (dryRun ? "?dryRun=true" : string.Empty),
            new { termId = termId ?? _h.TermId, type, effectiveAt = effectiveInDays is { } d ? Iso(d) : null, reason = "PAYMENT_RECEIVED" });

    private Task<string> TermStateAsync() =>
        _h.ScalarAsync<string>($"SELECT state || '/' || coalesce(cancelled_at::text, 'NONE') FROM pol.policy_term WHERE term_id = '{_h.TermId}' AND recorded_to IS NULL");

    private Task<decimal> NetAsync(string? kind = null) =>
        _h.ScalarAsync<decimal>(
            $"SELECT coalesce(sum(amount), 0) FROM pol.charge_line WHERE term_id = '{_h.TermId}'" + (kind is null ? string.Empty : $" AND transaction_kind = '{kind}'"));

    [Fact]
    public async Task Reinstating_without_lapse_reopens_the_term_and_nets_the_cancellation_out()
    {
        var written = await NetAsync();
        await CancelAsync();
        (await NetAsync()).ShouldBeLessThan(written);
        (await _h.CountEventsAsync("ChargeDeltaEmitted")).ShouldBeGreaterThan(0);
        var deltasBefore = await _h.CountEventsAsync("ChargeDeltaEmitted");

        var (response, body) = await ReinstateAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        (await TermStateAsync()).ShouldBe("IN_FORCE/NONE");
        (await NetAsync()).ShouldBe(written);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{_h.PolicyId}' AND kind = 'REINSTATEMENT'")).ShouldBe(1);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.job WHERE job_id = '{body.Text("jobId")}' AND job_type = 'REINSTATEMENT' AND state = 'BOUND'")).ShouldBe(1);
        // The cover resumes to the end of the term: the segments again reach the term end.
        (await _h.ScalarAsync<long>(
            $"SELECT count(*) FROM pol.segment s JOIN pol.policy_term t ON t.term_id = s.term_id AND t.recorded_to IS NULL WHERE s.term_id = '{_h.TermId}' AND s.recorded_to IS NULL AND s.valid_to = t.valid_to"))
            .ShouldBe(1);

        // BIL/RI-facing events: one PolicyReinstated, one complete delta set (once, not duplicated).
        (await _h.CountEventsAsync("PolicyReinstated")).ShouldBe(1);
        var reinstated = await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.charge_line WHERE term_id = '{_h.TermId}' AND transaction_kind = 'REINSTATEMENT'");
        reinstated.ShouldBeGreaterThan(0);
        (await _h.CountEventsAsync("ChargeDeltaEmitted")).ShouldBe(deltasBefore + reinstated);
    }

    [Fact]
    public async Task Reinstating_with_a_lapse_does_not_charge_the_gap_and_reports_it()
    {
        var written = await NetAsync();
        await CancelAsync();
        var cancelled = await NetAsync();
        await AdvanceAsync(5);

        var (response, body) = await ReinstateAsync("WITH_LAPSE", 0);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        var net = await NetAsync();
        net.ShouldBeLessThan(written);
        net.ShouldBeGreaterThan(cancelled);
        (await _h.CountEventsAsync("PolicyReinstated")).ShouldBe(1);
        (await _h.ScalarAsync<string>($"SELECT payload::text FROM plt.outbox_message WHERE event_type = 'PolicyReinstated' AND aggregate_id = '{_h.PolicyId}'")).ShouldContain("PERIOD");
    }

    [Fact]
    public async Task The_window_ends_and_only_a_cancelled_term_can_be_reinstated_once()
    {
        var (active, activeBody) = await ReinstateAsync();
        RenewalHarness.ShouldFailWith(active, activeBody, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");

        await CancelAsync();
        // A lapse needs an effective time after the cancellation.
        var (early, earlyBody) = await ReinstateAsync("WITH_LAPSE", -1);
        RenewalHarness.ShouldFailWith(early, earlyBody, HttpStatusCode.UnprocessableEntity, "POL-ERR-EFFDATE-LIMIT");
        await AdvanceAsync(31);
        var (late, lateBody) = await ReinstateAsync();
        RenewalHarness.ShouldFailWith(late, lateBody, HttpStatusCode.UnprocessableEntity, "POL-ERR-RIGHT-EXPIRED");
        (await TermStateAsync()).ShouldStartWith("CANCELLED/");
        (await _h.CountEventsAsync("PolicyReinstated")).ShouldBe(0);
    }

    [Fact]
    public async Task A_second_reinstatement_is_refused_and_a_reinstated_term_can_be_cancelled_again()
    {
        await CancelAsync();
        (await ReinstateAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var (again, againBody) = await ReinstateAsync();
        RenewalHarness.ShouldFailWith(again, againBody, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");
        (await _h.CountEventsAsync("PolicyReinstated")).ShouldBe(1);

        await AdvanceAsync(1);
        await CancelAsync();
        (await TermStateAsync()).ShouldStartWith("CANCELLED/");
    }

    [Fact]
    public async Task Dry_run_writes_nothing_and_bad_input_is_refused()
    {
        await CancelAsync();
        var (dry, dryBody) = await ReinstateAsync(dryRun: true);
        dry.StatusCode.ShouldBe(HttpStatusCode.Created, dryBody?.ToJsonString());
        (await TermStateAsync()).ShouldStartWith("CANCELLED/");
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{_h.PolicyId}' AND kind = 'REINSTATEMENT'")).ShouldBe(0);

        (await ReinstateAsync("SOMETIMES")).Response.IsSuccessStatusCode.ShouldBeFalse();
        (await _h.PostAsync("/api/pol/v1/reinstatements", new { termId = _h.TermId, type = "NO_LAPSE" })).Response.IsSuccessStatusCode.ShouldBeFalse();
        (await ReinstateAsync(termId: Guid.NewGuid().ToString())).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var (forbidden, _) = await _h.PostAsync(
            "/api/pol/v1/reinstatements", new { termId = _h.TermId, type = "NO_LAPSE", reason = "X" }, roles: "Staff.NoSuch");
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await TermStateAsync()).ShouldStartWith("CANCELLED/");
    }

    [Fact]
    public async Task A_rewrite_of_a_cancelled_term_creates_a_new_policy_through_quote_and_bind()
    {
        var body = new { termId = _h.TermId, rewriteType = "STANDARD", effectiveAt = Iso(1) };
        var (notCancelled, notCancelledBody) = await _h.PostAsync("/api/pol/v1/rewrites", body);
        RenewalHarness.ShouldFailWith(notCancelled, notCancelledBody, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");

        await CancelAsync();
        var (created, createdBody) = await _h.PostAsync("/api/pol/v1/rewrites", body);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, createdBody?.ToJsonString());
        var jobId = createdBody.Text("jobId");
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.job WHERE job_id = '{jobId}' AND job_type = 'SUBMISSION' AND state = 'DRAFT' AND target_term_id = '{_h.TermId}'")).ShouldBe(1);

        var (again, againBody) = await _h.PostAsync("/api/pol/v1/rewrites", body);
        RenewalHarness.ShouldFailWith(again, againBody, HttpStatusCode.BadRequest, "POL-ERR-OPEN-JOB");

        var (quoted, quote) = await _h.Slice.QuoteAsync(jobId);
        quoted.StatusCode.ShouldBe(HttpStatusCode.OK, quote?.ToJsonString());
        var (bound, bind) = await _h.Slice.BindAsync(jobId);
        bound.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        bind.Text("state").ShouldBe("BOUND");
        bind.Text("policyId").ShouldNotBe(_h.PolicyId);
        (await _h.CountEventsAsync("PolicyRewritten")).ShouldBe(1);
    }

    [Fact]
    public async Task A_rewrite_with_an_account_change_needs_the_target_account_and_a_future_time()
    {
        await CancelAsync();
        var (noAccount, _) = await _h.PostAsync("/api/pol/v1/rewrites", new { termId = _h.TermId, rewriteType = "ACCOUNT_CHANGE", effectiveAt = Iso(1) });
        noAccount.IsSuccessStatusCode.ShouldBeFalse();
        var (past, pastBody) = await _h.PostAsync("/api/pol/v1/rewrites", new { termId = _h.TermId, rewriteType = "STANDARD", effectiveAt = Iso(-2) });
        RenewalHarness.ShouldFailWith(past, pastBody, HttpStatusCode.UnprocessableEntity, "POL-ERR-EFFDATE-LIMIT");
        var (unknown, _) = await _h.PostAsync("/api/pol/v1/rewrites", new { termId = Guid.NewGuid(), rewriteType = "STANDARD", effectiveAt = Iso(1) });
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var (forbidden, _) = await _h.PostAsync(
            "/api/pol/v1/rewrites", new { termId = _h.TermId, rewriteType = "STANDARD", effectiveAt = Iso(1) }, roles: "Staff.NoSuch");
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
