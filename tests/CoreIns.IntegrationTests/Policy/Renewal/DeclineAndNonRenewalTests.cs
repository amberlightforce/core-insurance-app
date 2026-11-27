using System.Globalization;
using System.Net;
using CoreIns.Modules.Documents.Contracts;
using CoreIns.Modules.Policy.Commands.Cancellation.NonPayment;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Renewal;

/// <summary>
/// BO-POL-09: <c>pol.Renewal.decline</c> (customer) and <c>pol.Renewal.nonRenew</c> (insurer, statutory notice) on PostgreSQL with a
/// shiftable clock. The notice period is set to 20 days for the class (the shipped 60 is ILLUSTRATIVE and Unverified).
/// </summary>
public sealed class DeclineAndNonRenewalTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly FakeVulnerability _vulnerability = new();
    private readonly RecordingDocuments _documents = new();
    private RenewalHarness _renewal = null!;
    private string? _declinedAt;

    public async ValueTask InitializeAsync()
    {
        var slice = new PolicySlice(
            database.AppConnectionString,
            settings: new Dictionary<string, string?> { ["Platform:Time:Mode"] = "Shiftable", ["Policy:Renewal:NonRenewalNoticeDays"] = "20" },
            configureServices: services =>
            {
                services.AddSingleton<IVulnerabilityCheck>(_vulnerability);
                services.AddSingleton<IDocumentRequestService>(_documents);
            });
        await slice.SeedAsync();
        _renewal = new RenewalHarness(slice, database.SuperuserConnectionString);
        await _renewal.BindPolicyAsync();
    }

    public async ValueTask DisposeAsync() => await _renewal.Slice.DisposeAsync();

    private Task<(HttpResponseMessage Response, System.Text.Json.Nodes.JsonNode? Body)> DeclineAsync(Guid? key = null, string reason = "PRICE")
    {
        // Fixed once per test: a replay with the same key must carry the same payload.
        _declinedAt ??= _renewal.Clock.Now.ToDateTimeOffset().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        return _renewal.PostAsync("/api/pol/v1/renewals/decline", new { jobId = _renewal.JobId, termId = _renewal.TermId, reasonCode = reason, declinedAt = _declinedAt }, key);
    }

    private Task<(HttpResponseMessage Response, System.Text.Json.Nodes.JsonNode? Body)> NonRenewAsync(
        string roles = RenewalHarness.Manager, bool dryRun = false, Guid? key = null) =>
        _renewal.PostAsync(
            "/api/pol/v1/renewals/non-renew" + (dryRun ? "?dryRun=true" : string.Empty), new { termId = _renewal.TermId, reasonCode = "CLAIMS_EXPERIENCE" }, key, roles);

    [Fact]
    public async Task Declining_an_offered_renewal_closes_the_job_replays_idempotently_and_never_after_acceptance()
    {
        await _renewal.GoToAsync(30);
        (await _renewal.CreateAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _renewal.OfferAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var key = Guid.NewGuid();
        var (declined, body) = await DeclineAsync(key);
        declined.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("reasonCode").ShouldBe("PRICE");
        (await _renewal.JobStateAsync()).ShouldBe("NOT_TAKEN");
        (await _renewal.CountEventsAsync("PolicyNonRenewed")).ShouldBe(1);

        var (replay, _) = await DeclineAsync(key);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _renewal.CountEventsAsync("PolicyNonRenewed")).ShouldBe(1);

        var (again, againBody) = await DeclineAsync();
        RenewalHarness.ShouldFailWith(again, againBody, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");
        (await _renewal.TermVersionsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Declining_after_acceptance_is_refused_and_the_next_term_stays()
    {
        await _renewal.GoToAsync(30);
        (await _renewal.CreateAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _renewal.OfferAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _renewal.AcceptAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (declined, body) = await DeclineAsync();
        RenewalHarness.ShouldFailWith(declined, body, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");
        (await _renewal.TermVersionsAsync()).ShouldBe(2);
        (await _renewal.CountEventsAsync("PolicyNonRenewed")).ShouldBe(0);
    }

    [Fact]
    public async Task A_decline_needs_a_reason_code()
    {
        await _renewal.GoToAsync(30);
        (await _renewal.CreateAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var (declined, _) = await DeclineAsync(reason: string.Empty);
        declined.IsSuccessStatusCode.ShouldBeFalse();
        (await _renewal.JobStateAsync()).ShouldBe("DRAFT");
    }

    [Fact]
    public async Task Non_renewal_inside_the_notice_period_requests_the_notice_letter_and_records_the_unverified_legal_status()
    {
        await _renewal.GoToAsync(90);
        var (response, body) = await NonRenewAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("status").ShouldBe("NOTICE_REQUESTED");
        body.Text("noticeDays").ShouldBe("20");
        body.Text("legalStatus").ShouldBe("Unverified");

        var expiry = await _renewal.ScalarAsync<DateTime>($"SELECT valid_to FROM pol.policy_term WHERE term_id = '{_renewal.TermId}' AND recorded_to IS NULL");
        DateTime.Parse(body.Text("effectiveAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ShouldBe(DateTime.SpecifyKind(expiry, DateTimeKind.Utc));
        var deadline = DateTime.Parse(body.Text("noticeDeadline"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        (DateTime.SpecifyKind(expiry, DateTimeKind.Utc) - deadline).TotalDays.ShouldBeInRange(19, 21);

        _documents.Requests.Count.ShouldBe(1);
        _documents.Requests[0].TemplateKey.ShouldBe("DT-NONRENEWAL-NOTICE");
        (await _renewal.CountEventsAsync("PolicyNonRenewed")).ShouldBe(1);
        (await _renewal.ScalarAsync<long>($"SELECT count(*) FROM pol.non_renewal WHERE term_id = '{_renewal.TermId}' AND notice_document_id IS NOT NULL")).ShouldBe(1);

        // Once only, and the term can no longer be renewed.
        var (again, againBody) = await NonRenewAsync();
        RenewalHarness.ShouldFailWith(again, againBody, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");
        _documents.Requests.Count.ShouldBe(1);
        await _renewal.GoToAsync(30);
        var (created, createBody) = await _renewal.CreateAsync();
        RenewalHarness.ShouldFailWith(created, createBody, HttpStatusCode.Conflict, "POL-ERR-ILLEGAL-TRANSITION");
    }

    [Fact]
    public async Task Non_renewal_after_the_notice_deadline_is_too_late_and_writes_nothing()
    {
        await _renewal.GoToAsync(10);
        var (response, body) = await NonRenewAsync();
        RenewalHarness.ShouldFailWith(response, body, HttpStatusCode.UnprocessableEntity, "POL-ERR-NOTICE-TOO-LATE");
        _documents.Requests.ShouldBeEmpty();
        (await _renewal.ScalarAsync<long>($"SELECT count(*) FROM pol.non_renewal WHERE term_id = '{_renewal.TermId}'")).ShouldBe(0);
        (await _renewal.CountEventsAsync("PolicyNonRenewed")).ShouldBe(0);
    }

    [Fact]
    public async Task Non_renewal_replays_with_the_same_key_and_a_dry_run_writes_nothing()
    {
        await _renewal.GoToAsync(90);
        var (dry, dryBody) = await NonRenewAsync(dryRun: true);
        dry.StatusCode.ShouldBe(HttpStatusCode.OK, dryBody?.ToJsonString());
        (await _renewal.ScalarAsync<long>($"SELECT count(*) FROM pol.non_renewal WHERE term_id = '{_renewal.TermId}'")).ShouldBe(0);
        _documents.Requests.ShouldBeEmpty();

        var key = Guid.NewGuid();
        var (first, firstBody) = await NonRenewAsync(key: key);
        var (replay, replayBody) = await NonRenewAsync(key: key);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, firstBody?.ToJsonString());
        replay.StatusCode.ShouldBe(HttpStatusCode.OK, replayBody?.ToJsonString());
        replayBody.Text("nonRenewalId").ShouldBe(firstBody.Text("nonRenewalId"));
        _documents.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_vulnerable_or_unknown_policyholder_goes_to_psr_review_and_nothing_is_sent()
    {
        _vulnerability.Vulnerable = true;
        await _renewal.GoToAsync(90);
        var (response, body) = await NonRenewAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("status").ShouldBe("PSR_REVIEW");
        _documents.Requests.ShouldBeEmpty();
        (await _renewal.CountEventsAsync("PolicyNonRenewed")).ShouldBe(0);
    }

    [Fact]
    public async Task Non_renewal_closes_an_open_renewal_job_of_the_term()
    {
        await _renewal.GoToAsync(30);
        (await _renewal.CreateAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _renewal.OfferAsync()).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (response, body) = await NonRenewAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        (await _renewal.JobStateAsync()).ShouldBe("DECLINED");
        var (accepted, acceptBody) = await _renewal.AcceptAsync();
        accepted.IsSuccessStatusCode.ShouldBeFalse(acceptBody?.ToJsonString());
    }

    [Fact]
    public async Task Only_underwriting_managers_can_non_renew()
    {
        await _renewal.GoToAsync(90);
        var (response, _) = await NonRenewAsync(roles: Underwriter);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private sealed class FakeVulnerability : IVulnerabilityCheck
    {
        public bool Vulnerable { get; set; }

        public Task<bool> IsVulnerableAsync(PartyId party, CancellationToken cancellationToken) => Task.FromResult(Vulnerable);
    }

    private sealed class RecordingDocuments : IDocumentRequestService
    {
        public List<DocumentRequestSpec> Requests { get; } = [];

        public Task<DocumentRequestOutcome> RequestAsync(DocumentRequestSpec spec, CancellationToken cancellationToken = default)
        {
            Requests.Add(spec);
            return Task.FromResult(new DocumentRequestOutcome(DocumentId.New(), DocumentNumber.Parse("DOC-2026-000001"), "Rendered", "EMAIL", false));
        }
    }
}
