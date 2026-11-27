using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Policy.Commands.Cancellation.NonPayment;
using CoreIns.Modules.Policy.Commands.Renewal;
using CoreIns.Modules.Policy.Services;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Platform.Contracts;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Renewal;

/// <summary>The renewal window in Athens calendar days, leap day included (BO-POL-10, REQ-POL-245).</summary>
public sealed class RenewalRunSelectionTests
{
    private static readonly TimeZoneInfo Athens = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");

    [Fact]
    public void A_leap_day_counts_as_a_day_of_the_window()
    {
        var now = Instant.FromUtc(2028, 2, 15, 8);
        RenewalRunSelection.InWindow(now, Instant.FromUtc(2028, 3, 31, 8), 45, Athens).ShouldBeTrue("14 days to Feb 29 plus 31 = 45");
        RenewalRunSelection.InWindow(now, Instant.FromUtc(2028, 4, 1, 8), 45, Athens).ShouldBeFalse("46 days");

        var common = Instant.FromUtc(2027, 2, 15, 8);
        RenewalRunSelection.InWindow(common, Instant.FromUtc(2027, 4, 1, 8), 45, Athens).ShouldBeTrue("no leap day: 45 days");
        RenewalRunSelection.InWindow(common, Instant.FromUtc(2027, 4, 2, 8), 45, Athens).ShouldBeFalse();
    }

    [Fact]
    public void A_term_that_has_already_ended_is_never_selected_and_the_end_day_itself_is_inside()
    {
        var now = Instant.FromUtc(2027, 6, 1, 8);
        RenewalRunSelection.InWindow(now, now, 45, Athens).ShouldBeFalse();
        RenewalRunSelection.InWindow(now, Instant.FromUtc(2027, 5, 31, 8), 45, Athens).ShouldBeFalse();
        RenewalRunSelection.InWindow(now, Instant.FromUtc(2027, 6, 1, 20), 45, Athens).ShouldBeTrue();
    }
}

/// <summary>
/// BO-POL-10: the renewal run (<c>pol.RenewalRun.*</c>) on PostgreSQL with a shiftable clock. The API only queues the run; the tests drive the
/// worker runner directly, as the hosted worker would. Several policies are bound, all ending on the same day.
/// </summary>
public sealed class RenewalRunTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly FakeVulnerability _vulnerability = new();
    private readonly ToggleAvailability _availability = new();
    private readonly List<(string TermId, string PartyId)> _terms = [];
    private RenewalHarness _renewal = null!;

    private RenewalRunWorkerRunner Worker => _renewal.Slice.Factory.Services.GetRequiredService<RenewalRunWorkerRunner>();

    public async ValueTask InitializeAsync()
    {
        // Runs left live by an earlier test of this class would block this one (one live run per legal entity).
        await using (var cleanup = Npgsql.NpgsqlDataSource.Create(database.SuperuserConnectionString))
        {
            await using var command = cleanup.CreateCommand("UPDATE pol.renewal_run SET status = 'COMPLETED' WHERE status IN ('RUNNING', 'PAUSED')");
            try
            {
                await command.ExecuteNonQueryAsync(Ct);
            }
            catch (Npgsql.PostgresException)
            {
                // the schema does not exist yet on the first test; the slice creates it
            }
        }

        var slice = new PolicySlice(
            database.AppConnectionString,
            settings: new Dictionary<string, string?>
            {
                ["Platform:Time:Mode"] = "Shiftable",
                ["Policy:Renewal:NonRenewalNoticeDays"] = "20",
                ["Policy:RenewalRun:ChunkSize"] = "1",
            },
            configureServices: services =>
            {
                services.AddSingleton<IVulnerabilityCheck>(_vulnerability);
                services.AddSingleton<IProductAvailabilityService>(_availability);
            });
        await slice.SeedAsync();
        _renewal = new RenewalHarness(slice, database.SuperuserConnectionString);
    }

    public async ValueTask DisposeAsync() => await _renewal.Slice.DisposeAsync();

    private async Task BindAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await _renewal.BindPolicyAsync();
            _terms.Add((_renewal.TermId, _renewal.PartyId));
        }
    }

    /// <summary>Starts a run. The product filter defaults to this host's unique product: the database is shared by the class's tests.</summary>
    private Task<(HttpResponseMessage Response, JsonNode? Body)> StartAsync(
        bool dryRun = false, int? windowDays = null, string? product = null, string? resumeRunId = null, string roles = RenewalHarness.Manager)
    {
        var body = new JsonObject { ["product"] = product ?? _renewal.Slice.Product };
        if (dryRun)
        {
            body["dryRun"] = true;
        }

        if (windowDays is { } days)
        {
            body["windowDays"] = days;
        }

        if (resumeRunId is not null)
        {
            body["resumeRunId"] = resumeRunId;
        }

        return _renewal.PostAsync("/api/pol/v1/renewal-runs/start", body, null, roles);
    }

    private static string RunId(JsonNode? body) => body!["run"]!["runId"]!.ToString();

    private Task<string> OutcomeAsync(string runId, string termId) =>
        _renewal.ScalarAsync<string>($"SELECT outcome FROM pol.renewal_run_item WHERE run_id = '{runId}' AND term_id = '{termId}'");

    private Task<long> RenewalJobsAsync() =>
        _renewal.ScalarAsync<long>(
            $"SELECT count(*) FROM pol.job WHERE job_type = 'RENEWAL' AND expiring_term_id IN ({string.Join(',', _terms.Select(t => $"'{t.TermId}'"))})");

    private Task<string> StatusAsync(string runId) => _renewal.ScalarAsync<string>($"SELECT status FROM pol.renewal_run WHERE run_id = '{runId}'");

    [Fact]
    public async Task A_run_renews_skips_non_renewed_and_routes_vulnerable_terms_and_a_rerun_never_duplicates()
    {
        await BindAsync(3);
        await _renewal.GoToAsync(60);
        (await _renewal.PostAsync("/api/pol/v1/renewals/non-renew", new { termId = _terms[1].TermId, reasonCode = "CLAIMS_EXPERIENCE" }, null, RenewalHarness.Manager))
            .Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _vulnerability.VulnerableParties.Add(_terms[2].PartyId);
        await _renewal.GoToAsync(30);

        var eventsBefore = await _renewal.ScalarAsync<long>("SELECT count(*) FROM plt.outbox_message WHERE event_type = 'RenewalRunCompleted'");

        // A dry run only evaluates: nothing is created, and it does not block the real run.
        var (dryStart, dryBody) = await StartAsync(dryRun: true);
        dryStart.StatusCode.ShouldBe(HttpStatusCode.OK, dryBody?.ToJsonString());
        var dryId = RunId(dryBody);
        (await Worker.RunAsync(null, Ct)).ShouldBe(1);
        (await OutcomeAsync(dryId, _terms[0].TermId)).ShouldBe("WOULD_CREATE");
        (await OutcomeAsync(dryId, _terms[1].TermId)).ShouldBe("SKIPPED_NON_RENEWED");
        (await OutcomeAsync(dryId, _terms[2].TermId)).ShouldBe("SKIPPED_VULNERABLE");
        (await RenewalJobsAsync()).ShouldBe(0);
        (await StatusAsync(dryId)).ShouldBe("COMPLETED");

        var (started, body) = await StartAsync();
        started.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["run"]!["total"]!.GetValue<int>().ShouldBe(3);
        var runId = RunId(body);
        (await Worker.RunAsync(null, Ct)).ShouldBe(1);

        (await OutcomeAsync(runId, _terms[0].TermId)).ShouldBe("CREATED");
        (await OutcomeAsync(runId, _terms[1].TermId)).ShouldBe("SKIPPED_NON_RENEWED");
        (await OutcomeAsync(runId, _terms[2].TermId)).ShouldBe("SKIPPED_VULNERABLE");
        (await StatusAsync(runId)).ShouldBe("COMPLETED");
        (await RenewalJobsAsync()).ShouldBe(1);
        (await _renewal.ScalarAsync<string>($"SELECT state FROM pol.job WHERE expiring_term_id = '{_terms[0].TermId}'")).ShouldBe("QUOTED");
        (await _renewal.ScalarAsync<string>($"SELECT sub_state FROM pol.job WHERE expiring_term_id = '{_terms[0].TermId}'")).ShouldBe("OFFERED");
        // Only the real run publishes RenewalRunCompleted (the dry run does not); the table is shared with the class's other tests.
        (await _renewal.ScalarAsync<long>("SELECT count(*) FROM plt.outbox_message WHERE event_type = 'RenewalRunCompleted'")).ShouldBe(eventsBefore + 1);

        // Same window again: every term is accounted for, no second renewal.
        var (_, again) = await StartAsync();
        var rerunId = RunId(again);
        await Worker.RunAsync(null, Ct);
        (await OutcomeAsync(rerunId, _terms[0].TermId)).ShouldBe("SKIPPED_EXISTING");
        (await RenewalJobsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Pause_stops_the_run_mid_way_and_resume_finishes_it_without_double_offers_and_one_run_is_live_at_a_time()
    {
        await BindAsync(3);
        await _renewal.GoToAsync(30);
        var (_, body) = await StartAsync();
        var runId = RunId(body);

        // One run per legal entity at a time.
        var (second, secondBody) = await StartAsync();
        RenewalHarness.ShouldFailWith(second, secondBody, HttpStatusCode.Conflict, "POL-ERR-RUN-ACTIVE");

        // Chunk size 1: one item, then the worker is told to stop.
        await Worker.RunAsync(1, Ct);
        (await _renewal.ScalarAsync<long>($"SELECT count(*) FROM pol.renewal_run_item WHERE run_id = '{runId}' AND outcome = 'CREATED'")).ShouldBe(1);
        var (paused, pauseBody) = await _renewal.PostAsync("/api/pol/v1/renewal-runs/pause", new { runId }, null, RenewalHarness.Manager);
        paused.StatusCode.ShouldBe(HttpStatusCode.OK, pauseBody?.ToJsonString());
        (await StatusAsync(runId)).ShouldBe("PAUSED");

        (await Worker.RunAsync(null, Ct)).ShouldBe(0);
        (await _renewal.ScalarAsync<long>($"SELECT count(*) FROM pol.renewal_run_item WHERE run_id = '{runId}' AND outcome = 'PENDING'")).ShouldBe(2);

        // A paused run still blocks a new one; resume continues where it stopped.
        var (blocked, blockedBody) = await StartAsync();
        RenewalHarness.ShouldFailWith(blocked, blockedBody, HttpStatusCode.Conflict, "POL-ERR-RUN-ACTIVE");
        var (resumed, resumeBody) = await StartAsync(resumeRunId: runId);
        resumed.StatusCode.ShouldBe(HttpStatusCode.OK, resumeBody?.ToJsonString());
        (await Worker.RunAsync(null, Ct)).ShouldBe(1);
        (await StatusAsync(runId)).ShouldBe("COMPLETED");
        (await RenewalJobsAsync()).ShouldBe(3);
        (await _renewal.ScalarAsync<long>($"SELECT count(*) FROM pol.renewal_run_item WHERE run_id = '{runId}' AND outcome = 'CREATED' AND attempts = 1")).ShouldBe(3);
    }

    [Fact]
    public async Task A_failed_item_is_recorded_with_its_code_and_a_retry_creates_the_renewal_when_the_cause_is_gone()
    {
        await BindAsync(2);
        await _renewal.GoToAsync(30);
        _availability.Available = false;
        var (_, body) = await StartAsync();
        var runId = RunId(body);
        await Worker.RunAsync(null, Ct);

        (await StatusAsync(runId)).ShouldBe("COMPLETED_WITH_ERRORS");
        (await OutcomeAsync(runId, _terms[0].TermId)).ShouldBe("FAILED");
        (await _renewal.ScalarAsync<string>($"SELECT error_code FROM pol.renewal_run_item WHERE run_id = '{runId}' AND term_id = '{_terms[0].TermId}'")).ShouldBe("POL-ERR-PRODUCT-UNAVAILABLE");
        (await RenewalJobsAsync()).ShouldBe(0);

        _availability.Available = true;
        var itemId = await _renewal.ScalarAsync<Guid>($"SELECT item_id FROM pol.renewal_run_item WHERE run_id = '{runId}' AND term_id = '{_terms[0].TermId}'");
        var (retried, retryBody) = await _renewal.PostAsync("/api/pol/v1/renewal-runs/retry-item", new { runId, itemId }, null, RenewalHarness.Manager);
        retried.StatusCode.ShouldBe(HttpStatusCode.OK, retryBody?.ToJsonString());
        (await StatusAsync(runId)).ShouldBe("RUNNING");
        await Worker.RunAsync(null, Ct);

        var why = await _renewal.ScalarAsync<string>($"SELECT coalesce(error_code, '') || ' ' || coalesce(detail, '') FROM pol.renewal_run_item WHERE run_id = '{runId}' AND term_id = '{_terms[0].TermId}'");
        (await OutcomeAsync(runId, _terms[0].TermId)).ShouldBe("CREATED", why);
        (await OutcomeAsync(runId, _terms[1].TermId)).ShouldBe("FAILED");
        (await StatusAsync(runId)).ShouldBe("COMPLETED_WITH_ERRORS");
        (await RenewalJobsAsync()).ShouldBe(1);

        // Only a failed item can be retried; the underwriter role cannot run a batch.
        var (notFailed, notFailedBody) = await _renewal.PostAsync("/api/pol/v1/renewal-runs/retry-item", new { runId, itemId }, null, RenewalHarness.Manager);
        RenewalHarness.ShouldFailWith(notFailed, notFailedBody, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");
        var (forbidden, _) = await StartAsync(roles: Underwriter);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_window_longer_than_the_lead_time_and_a_product_filter_are_honoured()
    {
        await BindAsync(1);
        await _renewal.GoToAsync(30);
        var (tooLong, tooLongBody) = await StartAsync(windowDays: 400);
        tooLong.IsSuccessStatusCode.ShouldBeFalse(tooLongBody?.ToJsonString());

        var (other, otherBody) = await StartAsync(product: "NO-SUCH-PRODUCT");
        other.StatusCode.ShouldBe(HttpStatusCode.OK, otherBody?.ToJsonString());
        otherBody!["run"]!["total"]!.GetValue<int>().ShouldBe(0);
        (await Worker.RunAsync(null, Ct)).ShouldBe(1);
        (await StatusAsync(RunId(otherBody))).ShouldBe("COMPLETED");

        var (narrow, narrowBody) = await StartAsync(windowDays: 10);
        narrow.StatusCode.ShouldBe(HttpStatusCode.OK, narrowBody?.ToJsonString());
        narrowBody!["run"]!["total"]!.GetValue<int>().ShouldBe(0);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeVulnerability : IVulnerabilityCheck
    {
        public HashSet<string> VulnerableParties { get; } = [];

        public Task<bool> IsVulnerableAsync(PartyId party, CancellationToken cancellationToken) =>
            Task.FromResult(VulnerableParties.Contains(party.Value.ToString("D")));
    }

    private sealed class ToggleAvailability : IProductAvailabilityService
    {
        public bool Available { get; set; } = true;

        public Task<AvailabilityCheckResponse> CheckAsync(AvailabilityCheckRequest request, ValidAt? validAt = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AvailabilityCheckResponse { Available = Available, Reason = Available ? null : "NO-VERSION-IN-WINDOW", Offerings = [] });
    }
}
