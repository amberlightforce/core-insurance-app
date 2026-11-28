using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Policy.Renewal;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Domain;
using CoreIns.SharedKernel;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Suspension;

/// <summary>BO-POL-08: <c>pol.Suspension.create</c> / <c>pol.Reactivation.create</c> (REQ-POL-236) on PostgreSQL, shiftable clock.</summary>
public sealed class SuspensionTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
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

    /// <summary>Moves the clock and the dev-clock offset together, so the policy watermark cap accepts the later record time.</summary>
    private async Task AdvanceAsync(double days)
    {
        _h.Clock.Advance(TimeSpan.FromDays(days));
        var micros = (long)(_h.Clock.Offset.TotalMilliseconds * 1000);
        await _h.ExecuteAsync($"UPDATE plt.dev_clock SET offset_micros = GREATEST(offset_micros, {micros}), version = version + 1");
    }

    private string Iso(double addDays = 0) => _h.Clock.Now.ToDateTimeOffset().AddDays(addDays).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private Task<(HttpResponseMessage Response, JsonNode? Body)> SuspendAsync(double days = 0, string path = "suspensions", bool dryRun = false, object? locator = null) =>
        _h.PostAsync(
            $"/api/pol/v1/{path}" + (dryRun ? "?dryRun=true" : string.Empty),
            new { termId = _h.TermId, effectiveAt = Iso(days), reason = "VEHICLE_STORED", evidence = new { doc = "x" }, elementLocator = locator });

    private Task<string> SuspendedFromAsync() =>
        _h.ScalarAsync<string>($"SELECT coalesce(suspended_from::text, 'NONE') FROM pol.policy_term WHERE term_id = '{_h.TermId}' AND recorded_to IS NULL");

    [Fact]
    public async Task Suspending_sets_the_flag_writes_a_zero_transaction_and_publishes_once()
    {
        var charges = await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.charge_line WHERE policy_id = '{_h.PolicyId}'");
        var (response, body) = await SuspendAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        (await SuspendedFromAsync()).ShouldNotBe("NONE");
        // Suspension is never a term state: the stored state is the one set at bind.
        (await _h.ScalarAsync<long>($"SELECT count(DISTINCT state) FROM pol.policy_term WHERE term_id = '{_h.TermId}'")).ShouldBe(1);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{_h.PolicyId}' AND kind = 'SUSPENSION' AND total = 0")).ShouldBe(1);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.charge_line WHERE policy_id = '{_h.PolicyId}'")).ShouldBe(charges);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.job WHERE job_id = '{body.Text("jobId")}' AND job_type = 'SUSPENSION' AND state = 'BOUND'")).ShouldBe(1);
        (await _h.CountEventsAsync("PolicyChanged")).ShouldBe(1);
    }

    [Fact]
    public async Task Suspending_twice_or_reactivating_an_active_term_is_refused()
    {
        (await SuspendAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var (again, body) = await SuspendAsync();
        RenewalHarness.ShouldFailWith(again, body, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");

        (await SuspendAsync(1, "reactivations")).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var (not, body2) = await SuspendAsync(2, "reactivations");
        RenewalHarness.ShouldFailWith(not, body2, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");
    }

    [Fact]
    public async Task A_change_is_blocked_while_suspended_and_allowed_again_after_reactivation()
    {
        (await SuspendAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var (blocked, blockedBody) = await _h.PostAsync("/api/pol/v1/policy-changes", new { policyId = _h.PolicyId, effectiveAt = Iso(0) });
        RenewalHarness.ShouldFailWith(blocked, blockedBody, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");

        (await SuspendAsync(1, "reactivations")).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await SuspendedFromAsync()).ShouldBe("NONE");
        await AdvanceAsync(1);
        var (ok, okBody) = await _h.PostAsync("/api/pol/v1/policy-changes", new { policyId = _h.PolicyId, effectiveAt = Iso(0) });
        ok.StatusCode.ShouldBe(HttpStatusCode.Created, okBody?.ToJsonString());
    }

    [Fact]
    public async Task Reactivation_restores_earning_and_keeps_both_transactions_for_the_earning_run()
    {
        (await SuspendAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        await AdvanceAsync(10);
        (await SuspendAsync(0, "reactivations")).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await SuspendedFromAsync()).ShouldBe("NONE");
        (await _h.ScalarAsync<string>(
            $"SELECT string_agg(kind, ',' ORDER BY sequence) FROM pol.policy_transaction WHERE policy_id = '{_h.PolicyId}' AND kind IN ('SUSPENSION', 'REACTIVATION')"))
            .ShouldBe("SUSPENSION,REACTIVATION");
        (await _h.CountEventsAsync("PolicyChanged")).ShouldBe(2);
    }

    [Fact]
    public async Task A_reactivation_cannot_precede_the_suspension()
    {
        (await SuspendAsync(5)).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var (response, body) = await SuspendAsync(1, "reactivations");
        RenewalHarness.ShouldFailWith(response, body, HttpStatusCode.UnprocessableEntity, "POL-ERR-OUT-OF-SEQUENCE");
    }

    [Fact]
    public async Task A_suspended_term_can_still_be_cancelled_and_dry_run_writes_nothing()
    {
        var (dry, dryBody) = await SuspendAsync(dryRun: true);
        dry.StatusCode.ShouldBe(HttpStatusCode.Created, dryBody?.ToJsonString());
        (await SuspendedFromAsync()).ShouldBe("NONE");

        (await SuspendAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var (cancel, cancelBody) = await _h.PostAsync(
            "/api/pol/v1/cancellations", new { policyId = _h.PolicyId, source = "Policyholder", reasonCode = "CUSTOMER_REQUEST", effectiveAt = Iso(0), kind = "STANDARD" });
        cancel.StatusCode.ShouldBe(HttpStatusCode.OK, cancelBody?.ToJsonString());
        var (after, afterBody) = await SuspendAsync(1, "reactivations");
        RenewalHarness.ShouldFailWith(after, afterBody, HttpStatusCode.UnprocessableEntity, "POL-ERR-AFTER-CANCELLATION");
    }

    [Fact]
    public async Task Bad_input_is_refused_and_the_term_is_untouched()
    {
        var (noReason, _) = await _h.PostAsync("/api/pol/v1/suspensions", new { termId = _h.TermId, effectiveAt = Iso(0) });
        noReason.IsSuccessStatusCode.ShouldBeFalse();
        var (element, _) = await SuspendAsync(locator: "no-such-vehicle");
        element.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var (backdated, _) = await SuspendAsync(-3);
        backdated.IsSuccessStatusCode.ShouldBeFalse();
        var (unknown, _) = await _h.PostAsync("/api/pol/v1/suspensions", new { termId = Guid.NewGuid(), effectiveAt = Iso(0), reason = "X" });
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var (forbidden, _) = await _h.PostAsync("/api/pol/v1/suspensions", new { termId = _h.TermId, effectiveAt = Iso(0), reason = "X" }, roles: "Staff.NoSuch");
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SuspendedFromAsync()).ShouldBe("NONE");
    }

    public static TheoryData<bool, PolicyTermState, bool, bool, string?> Table => new()
    {
        { true, PolicyTermState.InForce, false, false, null },
        { true, PolicyTermState.Scheduled, false, false, null },
        { true, PolicyTermState.InForce, true, false, "ILLEGAL-TRANSITION" },
        { true, PolicyTermState.PendingCancellation, false, false, "ILLEGAL-TRANSITION" },
        { true, PolicyTermState.Expired, false, false, "ILLEGAL-TRANSITION" },
        { true, PolicyTermState.Cancelled, false, false, "AFTER-CANCELLATION" },
        { false, PolicyTermState.InForce, true, false, null },
        { false, PolicyTermState.InForce, false, false, "ILLEGAL-TRANSITION" },
        { false, PolicyTermState.InForce, true, true, "AFTER-CANCELLATION" },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Transition_table(bool suspend, PolicyTermState state, bool suspended, bool cancelled, string? expected)
    {
        var from = Instant.FromUtcDateTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var now = from + TimeSpan.FromDays(10);
        var error = SuspensionRules.Check(
            suspend ? SuspensionAction.Suspend : SuspensionAction.Reactivate, state, cancelled ? now : null, suspended ? now - TimeSpan.FromDays(1) : null, from, from + TimeSpan.FromDays(365), from, now, now, TimeSpan.FromMinutes(5));
        (error is null).ShouldBe(expected is null);
        if (expected is not null)
        {
            error!.Code.ToString().ShouldContain(expected);
        }
    }
}
