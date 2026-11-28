using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Policy.Renewal;
using CoreIns.Modules.Policy.Commands.Cancellation;
using CoreIns.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Cancellation;

/// <summary>BO-POL-06: <c>pol.Cancellation.schedule</c> / <c>pol.Cancellation.rescind</c> and the sweep that runs due ones, on PostgreSQL, shiftable clock.</summary>
public sealed class ScheduledCancellationTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly TimeZoneInfo Athens = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");
    private RenewalHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var slice = new PolicySlice(database.AppConnectionString, settings: new Dictionary<string, string?> { ["Platform:Time:Mode"] = "Shiftable" });
        await slice.SeedAsync();
        _h = new RenewalHarness(slice, database.SuperuserConnectionString);
        await _h.BindPolicyAsync();
        await _h.GoToAsync(200);
    }

    public async ValueTask DisposeAsync() => await _h.Slice.DisposeAsync();

    private async Task AdvanceToAsync(Instant target)
    {
        _h.Clock.Advance(target.ToDateTimeOffset() - _h.Clock.Now.ToDateTimeOffset());
        var micros = (long)(_h.Clock.Offset.TotalMilliseconds * 1000);
        await _h.ExecuteAsync($"UPDATE plt.dev_clock SET offset_micros = GREATEST(offset_micros, {micros}), version = version + 1");
    }

    private static string Iso(Instant at) => at.ToDateTimeOffset().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private Instant In(double days) => Instant.FromUtcDateTime(_h.Clock.Now.ToDateTimeOffset().AddDays(days).UtcDateTime);

    private Task<(HttpResponseMessage Response, JsonNode? Body)> ScheduleAsync(Instant at, bool dryRun = false, string source = "Policyholder") =>
        _h.PostAsync(
            "/api/pol/v1/cancellations/schedule" + (dryRun ? "?dryRun=true" : string.Empty),
            new { policyId = _h.PolicyId, source, reasonCode = "CUSTOMER_REQUEST", effectiveAt = Iso(at) });

    private Task<(HttpResponseMessage Response, JsonNode? Body)> RescindAsync(string jobId) =>
        _h.PostAsync("/api/pol/v1/cancellations/rescind", new { policyId = _h.PolicyId, jobId, reasonCode = "CUSTOMER_CHANGED_MIND" });

    private async Task<int> SweepAsync()
    {
        await using var scope = _h.Slice.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ScheduledCancellationSweepRunner>().RunAsync(Ct);
    }

    private Task<long> ScheduledRowsAsync() => _h.ScalarAsync<long>($"SELECT count(*) FROM pol.scheduled_cancellation WHERE policy_id = '{_h.PolicyId}'");

    private Task<string> StatusAsync(string jobId) => _h.ScalarAsync<string>($"SELECT status FROM pol.scheduled_cancellation WHERE scheduled_id = '{jobId}'");

    private Task<long> CancellationJobsAsync() =>
        _h.ScalarAsync<long>($"SELECT count(*) FROM pol.job WHERE policy_id = '{_h.PolicyId}' AND job_type = 'CANCELLATION'");

    [Fact]
    public async Task Scheduling_previews_the_refund_stores_the_row_and_publishes_once_and_a_dry_run_writes_nothing()
    {
        var (dry, dryBody) = await ScheduleAsync(In(30), dryRun: true);
        dry.StatusCode.ShouldBe(HttpStatusCode.OK, dryBody?.ToJsonString());
        (await ScheduledRowsAsync()).ShouldBe(0);

        var (response, body) = await ScheduleAsync(In(30));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("state").ShouldBe("SCHEDULED");
        body.Text("servicingPreview.refundMethod").ShouldBe("PRO_RATA");
        decimal.Parse(body.Text("servicingPreview.refundDue.amount"), CultureInfo.InvariantCulture).ShouldBeGreaterThan(0m);
        (await StatusAsync(body.Text("jobId"))).ShouldBe("SCHEDULED");
        (await _h.CountEventsAsync("CancellationScheduled")).ShouldBe(1);
        // Nothing is cancelled yet: no job, no cancelled term version, no refund lines.
        (await CancellationJobsAsync()).ShouldBe(0);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.policy_term WHERE term_id = '{_h.TermId}' AND cancelled_at IS NOT NULL")).ShouldBe(0);
        (await _h.CountEventsAsync("PolicyCancelled")).ShouldBe(0);

        var (again, againBody) = await ScheduleAsync(In(40));
        RenewalHarness.ShouldFailWith(again, againBody, HttpStatusCode.Conflict, "POL-ERR-JOB-CONFLICT");
    }

    [Fact]
    public async Task A_time_that_is_not_in_the_future_or_outside_the_term_or_another_source_is_refused()
    {
        var (past, pastBody) = await ScheduleAsync(In(-1));
        RenewalHarness.ShouldFailWith(past, pastBody, HttpStatusCode.UnprocessableEntity, "POL-ERR-EFFDATE-LIMIT");
        var (far, farBody) = await ScheduleAsync(In(2000));
        RenewalHarness.ShouldFailWith(far, farBody, HttpStatusCode.UnprocessableEntity, "POL-ERR-EFFDATE-LIMIT");
        var (source, _) = await ScheduleAsync(In(30), source: "Insurer");
        source.IsSuccessStatusCode.ShouldBeFalse();
        var (forbidden, _) = await _h.PostAsync(
            "/api/pol/v1/cancellations/schedule", new { policyId = _h.PolicyId, source = "Policyholder", reasonCode = "X", effectiveAt = Iso(In(30)) }, roles: "Staff.NoSuch");
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ScheduledRowsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Rescinding_before_the_effective_time_stops_the_sweep_and_frees_the_term()
    {
        var (_, scheduled) = await ScheduleAsync(In(30));
        var jobId = scheduled.Text("jobId");
        var (rescinded, body) = await RescindAsync(jobId);
        rescinded.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("state").ShouldBe("RESCINDED");
        (await StatusAsync(jobId)).ShouldBe("RESCINDED");
        (await _h.CountEventsAsync("CancellationRescinded")).ShouldBe(1);

        var (twice, twiceBody) = await RescindAsync(jobId);
        RenewalHarness.ShouldFailWith(twice, twiceBody, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");
        var (unknown, _) = await RescindAsync(Guid.NewGuid().ToString());
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await AdvanceToAsync(In(31));
        await SweepAsync();
        (await CancellationJobsAsync()).ShouldBe(0);
        (await ScheduleAsync(In(10))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_sweep_runs_a_due_cancellation_once_and_a_late_rescind_is_refused()
    {
        var (_, scheduled) = await ScheduleAsync(In(30));
        var jobId = scheduled.Text("jobId");
        await SweepAsync(); // not due yet
        (await StatusAsync(jobId)).ShouldBe("SCHEDULED");

        await AdvanceToAsync(In(31));
        await SweepAsync();
        (await StatusAsync(jobId)).ShouldBe("EXECUTED");
        (await CancellationJobsAsync()).ShouldBe(1);
        (await _h.CountEventsAsync("PolicyCancelled")).ShouldBe(1);
        (await _h.ScalarAsync<string>($"SELECT state FROM pol.policy_term WHERE term_id = '{_h.TermId}' AND recorded_to IS NULL")).ShouldBe("CANCELLED");

        // Double-execute guard: a second sweep finds nothing and writes nothing.
        await SweepAsync();
        (await CancellationJobsAsync()).ShouldBe(1);
        (await _h.CountEventsAsync("PolicyCancelled")).ShouldBe(1);
        var (late, lateBody) = await RescindAsync(jobId);
        RenewalHarness.ShouldFailWith(late, lateBody, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");
    }

    [Fact]
    public async Task A_suspended_term_can_be_scheduled_and_the_sweep_cancels_it()
    {
        var (suspended, suspendedBody) = await _h.PostAsync(
            "/api/pol/v1/suspensions", new { termId = _h.TermId, effectiveAt = Iso(_h.Clock.Now), reason = "VEHICLE_STORED" });
        suspended.StatusCode.ShouldBe(HttpStatusCode.Created, suspendedBody?.ToJsonString());
        var (response, body) = await ScheduleAsync(In(30));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());

        await AdvanceToAsync(In(31));
        await SweepAsync();
        (await StatusAsync(body.Text("jobId"))).ShouldBe("EXECUTED");
        (await _h.ScalarAsync<string>($"SELECT state FROM pol.policy_term WHERE term_id = '{_h.TermId}' AND recorded_to IS NULL")).ShouldBe("CANCELLED");
    }

    [Fact]
    public async Task A_permanent_refusal_at_run_time_parks_the_row_as_failed_and_is_not_retried()
    {
        var (_, scheduled) = await ScheduleAsync(In(30));
        var jobId = scheduled.Text("jobId");
        // The term is cancelled now by the ordinary command: the scheduled one can no longer run.
        var (now, nowBody) = await _h.PostAsync(
            "/api/pol/v1/cancellations", new { policyId = _h.PolicyId, source = "Policyholder", reasonCode = "CUSTOMER_REQUEST", effectiveAt = Iso(_h.Clock.Now), kind = "STANDARD" });
        now.StatusCode.ShouldBe(HttpStatusCode.OK, nowBody?.ToJsonString());

        await AdvanceToAsync(In(31));
        await SweepAsync();
        (await StatusAsync(jobId)).ShouldBe("FAILED");
        await SweepAsync();
        (await CancellationJobsAsync()).ShouldBe(1);
    }

    /// <summary>NX-EC-04: on the day the clocks change, the effective moment is the start of that Athens day (a 23 or 25 hour day), not a UTC midnight.</summary>
    [Fact]
    public async Task A_cancellation_effective_at_the_start_of_a_clock_change_day_is_due_exactly_then_and_prorated_from_that_date()
    {
        var end = await _h.ScalarAsync<DateTime>($"SELECT valid_to FROM pol.policy_term WHERE term_id = '{_h.TermId}' AND recorded_to IS NULL");
        var day = new BusinessDate(DateOnly.FromDateTime(_h.Clock.Now.ToDateTimeOffset().UtcDateTime).AddDays(1));
        while (Athens.GetUtcOffset(day.StartOfDayIn(Athens).ToDateTimeOffset()) == Athens.GetUtcOffset(new BusinessDate(day.Value.AddDays(1)).StartOfDayIn(Athens).ToDateTimeOffset()))
        {
            day = new BusinessDate(day.Value.AddDays(1));
        }

        day.Value.ToDateTime(TimeOnly.MinValue).ShouldBeLessThan(end);
        var effective = day.StartOfDayIn(Athens);
        (new BusinessDate(day.Value.AddDays(1)).StartOfDayIn(Athens) - effective).TotalHours.ShouldNotBe(24d);
        var (response, body) = await ScheduleAsync(effective);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["servicingPreview"]!["proratedLines"]![0]!["period"]!["from"]!.GetValue<string>().ShouldBe(day.ToString());

        await AdvanceToAsync(effective - TimeSpan.FromMinutes(1));
        await SweepAsync();
        await AdvanceToAsync(effective + TimeSpan.FromMinutes(1));
        await SweepAsync();
        (await _h.ScalarAsync<string>(
            $"SELECT (cancelled_at AT TIME ZONE 'Europe/Athens')::date::text FROM pol.policy_term WHERE term_id = '{_h.TermId}' AND recorded_to IS NULL")).ShouldBe(day.ToString());
    }
}
