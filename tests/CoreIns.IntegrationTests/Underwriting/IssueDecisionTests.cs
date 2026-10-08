using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Policy;
using CoreIns.Modules.Underwriting.Commands;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Domain;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Rating.RatingTestSupport;

namespace CoreIns.IntegrationTests.Underwriting;

/// <summary>
/// The referral decision (uw.Issue.decide, uw.Issue.list; PRD-04 §7.3, REQ-UW-079, -091..093, -109, -115, -116) on a real
/// PostgreSQL 17 through the real Host, with the illustrative UW.ISSUE_APPROVAL grant of appsettings.json
/// (Staff.UnderwritingManager). Underwriter <c>uw-anna</c> quotes; manager <c>uw-boss</c> decides.
/// </summary>
public sealed class IssueDecisionTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Underwriter = "Staff.Underwriter";
    private const string Manager = "Staff.UnderwritingManager";
    private const string Anna = "uw-anna";
    private const string Boss = "uw-boss";

    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        // Staff.Underwriter is also given the permission here so that the authority check (not the permission) refuses it.
        _factory = new ApiHostFactory(database.AppConnectionString, settings: new Dictionary<string, string?> { ["Platform:Permissions:Grants:uw.Issue.decide:1"] = Underwriter });
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsUserAsync(HttpMethod method, string path, string user, string roles, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.AcceptLanguage.ParseAdd("en");
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await _client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<JsonNode> EvaluateAsync(Guid job, JsonObject risk)
    {
        var (response, body) = await SendAsUserAsync(HttpMethod.Post, "/api/uw/v1/rules/evaluate", Anna, Underwriter, new
        {
            jobRef = job, checkpoint = "PRE_BIND", snapshotRef = $"snapshot-{job:N}", productCode = MotorProduct, effectiveDate = "2026-11-01", riskSnapshot = risk,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!;
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> DecideAsync(string issueId, string decision = "APPROVE", string user = Boss, string roles = Manager, string reason = "Vehicle inspected; acceptable.") =>
        SendAsUserAsync(HttpMethod.Post, "/api/uw/v1/issues/decide", user, roles, new { issueIds = new[] { issueId }, decision, reason });

    private async Task<bool> BlockedAsync(Guid job)
    {
        var (response, body) = await SendAsUserAsync(HttpMethod.Get, $"/api/uw/v1/issues/blocking-status?jobRef={job}&blockingPoint=PRE_BIND", Anna, Underwriter);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["blocked"]!.GetValue<bool>();
    }

    private async Task<JsonArray> IssuesAsync(Guid job)
    {
        var (response, body) = await SendAsUserAsync(HttpMethod.Get, $"/api/uw/v1/issues?jobRef={job}", Anna, Underwriter);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["items"]!.AsArray();
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static JsonObject OldCar(string firstRegistration = "1991", string value = "3000.00") => RiskTree(firstRegistration, value);

    [Fact]
    public async Task REQ_UW_079_a_manager_approves_the_referral_and_it_no_longer_blocks_while_the_facts_are_unchanged()
    {
        var job = Guid.CreateVersion7();
        var raised = await EvaluateAsync(job, OldCar());
        raised.Text("outcome").ShouldBe("REFER");
        var issueId = raised.Text("issues.0.issueId");

        var listed = await IssuesAsync(job);
        listed.Count.ShouldBe(1);
        listed[0].Text("issueType").ShouldBe("VEHICLE_AGE_REFERRAL");
        listed[0].Text("ruleId").ShouldBe("REFER-OLD-VEHICLE");
        listed[0].Text("status").ShouldBe("Open");
        listed[0].Text("raisedBy").ShouldBe("USER:" + Anna);

        var (decided, decision) = await DecideAsync(issueId);

        decided.StatusCode.ShouldBe(HttpStatusCode.OK, decision?.ToJsonString());
        decision.Text("decisions.0.status").ShouldBe("Approved");
        decision!["pendingSecondApproval"]!.GetValue<bool>().ShouldBeFalse();
        var checkId = decision.Text("checkIds.0");
        decision.Text("decisions.0.authorityCheckId").ShouldBe(checkId);
        (await BlockedAsync(job)).ShouldBeFalse();
        var approved = (await IssuesAsync(job))[0]!;
        approved.Text("status").ShouldBe("Approved");
        approved.Text("decision.decision").ShouldBe("APPROVE");
        approved.Text("decision.reason").ShouldBe("Vehicle inspected; acceptable.");
        approved.Text("decision.authorityCheckId").ShouldBe(checkId);
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'UWIssueApproved' AND business_keys->>'issueId' = '{issueId}'")).ShouldBe(1);
        (await CountAsync($"SELECT count(*) FROM plt.audit_event WHERE operation = 'uw.Issue.decide' AND object_id = '{issueId}' AND outcome = 'Succeeded'")).ShouldBe(1);

        // REQ-UW-092: re-evaluating the same facts keeps the approval (no new issue, no referral).
        var again = await EvaluateAsync(job, OldCar());
        again.Text("issues.0.issueId").ShouldBe(issueId);
        again.Text("issues.0.approvalStatus").ShouldBe("Approved");
        (await CountAsync($"SELECT count(*) FROM uw.issue WHERE job_id = '{job}'")).ShouldBe(1);
        (await BlockedAsync(job)).ShouldBeFalse();

        var (twice, twiceBody) = await DecideAsync(issueId);
        twice.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, twiceBody?.ToJsonString());
        twiceBody.Text("code").ShouldBe("UW-ERR-ISSUE-TRANSITION");
    }

    [Fact]
    public async Task REQ_UW_093_changed_facts_invalidate_the_approval_and_raise_a_new_open_issue()
    {
        var job = Guid.CreateVersion7();
        var issueId = (await EvaluateAsync(job, OldCar())).Text("issues.0.issueId");
        (await DecideAsync(issueId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var changed = await EvaluateAsync(job, OldCar(value: "4500.00"));

        changed.Text("issues.0.approvalStatus").ShouldBe("Open");
        changed.Text("issues.0.issueId").ShouldNotBe(issueId);
        (await BlockedAsync(job)).ShouldBeTrue();
        var issues = await IssuesAsync(job);
        issues.Select(i => i!["status"]!.GetValue<string>()).ShouldBe(["Invalidated", "Open"]);
        issues[0].Text("closeReason").ShouldBe("VALUE_CHANGED");
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ApprovalInvalidated' AND business_keys->>'issueId' = '{issueId}'")).ShouldBe(1);
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'UWIssueRaised' AND business_keys->>'jobId' = '{job}' AND (payload->>'reopened')::boolean")).ShouldBe(1);
    }

    [Fact]
    public async Task A_rejection_keeps_blocking_until_the_facts_change_and_closes_when_the_rule_no_longer_hits()
    {
        var job = Guid.CreateVersion7();
        var issueId = (await EvaluateAsync(job, OldCar())).Text("issues.0.issueId");

        var (rejected, body) = await DecideAsync(issueId, "REJECT", reason: "Too old for the appetite.");

        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("decisions.0.status").ShouldBe("Rejected");
        (await BlockedAsync(job)).ShouldBeTrue();
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'UWIssueRejected' AND business_keys->>'issueId' = '{issueId}'")).ShouldBe(1);

        var same = await EvaluateAsync(job, OldCar());
        same.Text("issues.0.approvalStatus").ShouldBe("Rejected");
        (await BlockedAsync(job)).ShouldBeTrue();

        var fixedRisk = await EvaluateAsync(job, RiskTree());
        fixedRisk.Text("outcome").ShouldBe("ACCEPT");
        (await BlockedAsync(job)).ShouldBeFalse();
        (await IssuesAsync(job))[0].Text("status").ShouldBe("Closed");
    }

    // SOD-UW-02 / BR-UW-013 / REQ-UW-116: whoever ran an evaluation for the job may not decide its issues, even with authority.
    [Fact]
    public async Task REQ_UW_116_the_underwriter_who_evaluated_the_job_cannot_decide_its_issue()
    {
        var job = Guid.CreateVersion7();
        var issueId = (await EvaluateAsync(job, OldCar())).Text("issues.0.issueId");

        var (response, body) = await DecideAsync(issueId, user: Anna, roles: $"{Underwriter},{Manager}");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, body?.ToJsonString());
        body.Text("code").ShouldBe("UW-ERR-SOD");
        (await IssuesAsync(job))[0].Text("status").ShouldBe("Open");
    }

    // REQ-UW-109, BR-UW-011: the binding authority check refuses a decider without a UW.ISSUE_APPROVAL grant; nothing is recorded.
    [Fact]
    public async Task REQ_UW_109_an_underwriter_without_an_issue_approval_grant_is_denied()
    {
        var job = Guid.CreateVersion7();
        var issueId = (await EvaluateAsync(job, OldCar())).Text("issues.0.issueId");

        var (response, body) = await DecideAsync(issueId, user: "uw-carl", roles: Underwriter);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, body?.ToJsonString());
        body.Text("code").ShouldBe("UW-ERR-AUTHORITY-DENIED");
        (await IssuesAsync(job))[0].Text("status").ShouldBe("Open");
        (await BlockedAsync(job)).ShouldBeTrue();

        var (noPermission, _) = await DecideAsync(issueId, user: "uw-dora", roles: "Staff.Billing");
        noPermission.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Decide_validates_the_request_and_refuses_a_stale_version_or_an_unknown_issue()
    {
        var job = Guid.CreateVersion7();
        var issueId = (await EvaluateAsync(job, OldCar())).Text("issues.0.issueId");

        var (noReason, noReasonBody) = await SendAsUserAsync(HttpMethod.Post, "/api/uw/v1/issues/decide", Boss, Manager, new { issueIds = new[] { issueId }, decision = "APPROVE", reason = " " });
        noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest, noReasonBody?.ToJsonString());
        noReasonBody.Text("code").ShouldBe("UW-ERR-VALIDATION");

        var (stale, staleBody) = await SendAsUserAsync(HttpMethod.Post, "/api/uw/v1/issues/decide", Boss, Manager,
            new { issueIds = new[] { issueId }, decision = "APPROVE", reason = "ok", expectedRecordVersions = new Dictionary<string, int> { [issueId] = 99 } });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, staleBody?.ToJsonString());
        staleBody.Text("code").ShouldBe("UW-ERR-STALE");

        var (unknown, unknownBody) = await DecideAsync(Guid.CreateVersion7().ToString());
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound, unknownBody?.ToJsonString());
        unknownBody.Text("code").ShouldBe("UW-ERR-NOT-FOUND");

        var (badStatus, _) = await SendAsUserAsync(HttpMethod.Get, "/api/uw/v1/issues?status=Nope", Boss, Manager);
        badStatus.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // REQ-UW-115 / BR-UW-014: a service identity never decides.
    [Fact]
    public async Task REQ_UW_115_a_service_identity_cannot_decide()
    {
        var job = Guid.CreateVersion7();
        var issueId = Guid.Parse((await EvaluateAsync(job, OldCar())).Text("issues.0.issueId"));
        await using var scope = Scope(_factory.Services, $"{Underwriter},{Manager}");
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.Service("batch-job");
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<DecideIssues, IssueDecideResponse>>();

        using (context.Use(CommandOptions.New().IdempotencyKey, dryRun: false))
        {
            var result = await handler.HandleAsync(
                new DecideIssues(new IssueDecideRequest { IssueIds = [new UwIssueId(issueId)], Decision = IssueDecisionCode.Approve, Reason = "batch" }), Ct);

            result.IsSuccess.ShouldBeFalse();
            result.Error!.Code.Value.ShouldBe("UW-ERR-HUMAN-DECISION-REQUIRED");
        }
    }

    [Fact]
    public async Task The_referral_queue_lists_open_issues_of_the_legal_entity()
    {
        var job = Guid.CreateVersion7();
        var issueId = (await EvaluateAsync(job, OldCar())).Text("issues.0.issueId");

        var (response, body) = await SendAsUserAsync(HttpMethod.Get, "/api/uw/v1/issues?limit=200", Boss, Manager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var items = body!["items"]!.AsArray();
        items.ShouldAllBe(i => i!["status"]!.GetValue<string>() == "Open");
        items.Select(i => i!["id"]!.GetValue<string>()).ShouldContain(issueId);
    }
}

/// <summary>
/// "i cant bind": the quote wizard's flow end to end with every module real. The underwriter quotes and tries to bind; the
/// PRE_BIND referral blocks (UW_ISSUES_OPEN); a senior underwriter approves it; the underwriter binds again and it binds.
/// </summary>
public sealed class ReferralApprovalBindTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private PolicySlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _slice = new PolicySlice(database.AppConnectionString, realRatingAndUnderwriting: true);
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    [Fact]
    public async Task A_referred_bind_succeeds_after_a_senior_underwriter_approves_the_issue()
    {
        var birth = DateTime.UtcNow.AddYears(-19).AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var party = await _slice.CreatePartyAsync(birth);
        var (jobId, _, _) = await _slice.DraftAsync(party, DateTimeOffset.UtcNow.AddDays(2));
        (await _slice.QuoteAsync(jobId)).Body.Text("state").ShouldBe("QUOTED");

        var (_, refused) = await _slice.BindAsync(jobId);
        refused.Text("state").ShouldBe("QUOTED");
        refused!["gateResults"]!.AsArray().Single(g => g!["gate"]!.GetValue<string>() == "UW_ISSUES")!["reason"]!.GetValue<string>().ShouldBe("UW_ISSUES_OPEN");

        var (listed, issues) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/uw/v1/issues?jobRef={jobId}");
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, issues?.ToJsonString());
        var open = issues!["items"]!.AsArray().Single(i => i!["status"]!.GetValue<string>() == "Open")!;
        open.Text("issueType").ShouldBe("DRIVER_AGE_REFERRAL");

        using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/uw/v1/issues/decide", UriKind.Relative)))
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, "Staff.Underwriter,Staff.UnderwritingManager");
            request.Headers.Add(TestAuthHandler.UserHeader, "uw-senior");
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            request.Content = JsonContent.Create(new
            {
                issueIds = new[] { open.Text("id") }, decision = "APPROVE", reason = "Experienced young driver; accepted.",
                expectedRecordVersions = new Dictionary<string, int> { [open.Text("id")] = open["recordVersion"]!.GetValue<int>() },
            });
            using var decided = await _slice.Client.SendAsync(request, Ct);
            decided.StatusCode.ShouldBe(HttpStatusCode.OK, await decided.Content.ReadAsStringAsync(Ct));
        }

        var (bound, bind) = await _slice.BindAsync(jobId);

        bound.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        bind.Text("state").ShouldBe("BOUND");
        bind!["gateResults"]!.AsArray().Single(g => g!["gate"]!.GetValue<string>() == "UW_ISSUES")!["passed"]!.GetValue<bool>().ShouldBeTrue();
    }
}

/// <summary>The approval fingerprint and the issue reconciliation without a database (REQ-UW-059, -091..093).</summary>
public sealed class IssueReconciliationTests
{
    private static readonly DateOnly Effective = new(2026, 11, 1);

    private static UwRisk Risk(int year = 1991, decimal value = 3000m, string birth = "1985-06-15") =>
        new("veh-1", new DateOnly(year, 1, 1), value, 1400, "PRIVATE", [DateOnly.Parse(birth, CultureInfo.InvariantCulture)], 0);

    [Fact]
    public void REQ_UW_091_the_fingerprint_is_stable_for_the_same_facts_and_changes_with_any_fact_the_rules_read()
    {
        var fingerprint = Risk().Fingerprint(Effective);

        fingerprint.Length.ShouldBe(64);
        fingerprint.ShouldMatch("^[0-9a-f]{64}$");
        Risk().Fingerprint(Effective).ShouldBe(fingerprint);
        (Risk() with { VehicleElementId = "veh-9" }).Fingerprint(Effective).ShouldBe(fingerprint); // the element is the key, not a fact
        Risk(value: 3000.0m).Fingerprint(Effective).ShouldBe(fingerprint); // same amount, other scale
        Risk(year: 1990).Fingerprint(Effective).ShouldNotBe(fingerprint);
        Risk(value: 3000.01m).Fingerprint(Effective).ShouldNotBe(fingerprint);
        Risk(birth: "1985-06-16").Fingerprint(Effective).ShouldNotBe(fingerprint);
        (Risk() with { Usage = "TAXI" }).Fingerprint(Effective).ShouldNotBe(fingerprint);
        (Risk() with { EngineCc = 1600 }).Fingerprint(Effective).ShouldNotBe(fingerprint);
        (Risk() with { ClaimsLast5Years = 1 }).Fingerprint(Effective).ShouldNotBe(fingerprint);
        Risk().Fingerprint(Effective.AddDays(1)).ShouldNotBe(fingerprint);
        fingerprint.ShouldNotContain("1985");
    }

    private static ReconcilableIssue Issue(string key, string status, string? decided = null) => new(Guid.CreateVersion7(), key, status, "f-old", decided);

    [Fact]
    public void A_new_key_raises_and_an_open_key_is_kept()
    {
        var open = Issue("A", IssueStatus.Open);

        var steps = IssueReconciliation.Plan([open], ["A", "B"], "f1");

        steps.Select(s => (s.IssueKey, s.Action)).ShouldBe([("A", IssueAction.KeepOpen), ("B", IssueAction.Raise)]);
        steps[0].Existing.ShouldBe(open);
    }

    [Fact]
    public void REQ_UW_092_093_an_approval_holds_on_the_same_fingerprint_and_is_invalidated_on_another()
    {
        IssueReconciliation.Plan([Issue("A", IssueStatus.Approved, "f1")], ["A"], "f1").Single().Action.ShouldBe(IssueAction.KeepApproved);
        IssueReconciliation.Plan([Issue("A", IssueStatus.Approved, "f1")], ["A"], "f2").Single().Action.ShouldBe(IssueAction.InvalidateAndRaise);
        IssueReconciliation.Plan([Issue("A", IssueStatus.ApprovedWithConditions, "f1")], ["A"], "f1").Single().Action.ShouldBe(IssueAction.KeepApproved);
    }

    [Fact]
    public void A_rejection_stays_on_the_same_facts_and_is_closed_and_raised_again_when_they_change()
    {
        IssueReconciliation.Plan([Issue("A", IssueStatus.Rejected, "f1")], ["A"], "f1").Single().Action.ShouldBe(IssueAction.KeepRejected);
        IssueReconciliation.Plan([Issue("A", IssueStatus.Rejected, "f1")], ["A"], "f2").Single().Action.ShouldBe(IssueAction.CloseAndRaise);
    }

    [Fact]
    public void REQ_UW_059_a_key_that_no_longer_hits_closes_open_and_rejected_issues_but_leaves_an_approval()
    {
        var steps = IssueReconciliation.Plan(
            [Issue("A", IssueStatus.Open), Issue("B", IssueStatus.Rejected, "f1"), Issue("C", IssueStatus.Approved, "f1")], [], "f1");

        steps.Select(s => (s.IssueKey, s.Action)).ShouldBe([("A", IssueAction.Close), ("B", IssueAction.Close)]);
    }

    [Fact]
    public void Open_rejected_and_invalidated_block_approved_does_not()
    {
        IssueStatus.Blocks(IssueStatus.Open).ShouldBeTrue();
        IssueStatus.Blocks(IssueStatus.Rejected).ShouldBeTrue();
        IssueStatus.Blocks(IssueStatus.Invalidated).ShouldBeTrue();
        IssueStatus.Blocks(IssueStatus.Approved).ShouldBeFalse();
        IssueStatus.Blocks(IssueStatus.Closed).ShouldBeFalse();
    }

    [Fact]
    public void A_synthetic_actor_gets_a_stable_pseudonymous_user_id_and_an_entra_oid_is_kept()
    {
        var oid = Guid.NewGuid();

        DecideIssuesHandler.UserIdOf(ActorRef.User(oid.ToString())).Value.ShouldBe(oid);
        DecideIssuesHandler.UserIdOf(ActorRef.User("dev:uwsenior")).ShouldBe(DecideIssuesHandler.UserIdOf(ActorRef.User("dev:uwsenior")));
        DecideIssuesHandler.UserIdOf(ActorRef.User("dev:uwsenior")).ShouldNotBe(DecideIssuesHandler.UserIdOf(ActorRef.User("dev:underwriter")));
    }
}
