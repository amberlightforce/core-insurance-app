using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Rating.RatingTestSupport;

namespace CoreIns.IntegrationTests.Underwriting;

/// <summary>
/// BO-UW-04 on a real PostgreSQL 17 through the real Host: <c>uw.Issue.reopen</c> (an approval is invalidated and replaced by a new
/// Open issue, <c>ApprovalInvalidated</c> published), <c>uw.Issue.addManual</c> (blocks the point, survives re-evaluation) and
/// <c>uw.Issue.close</c> (not an Open blocking issue).
/// </summary>
public sealed class IssueLifecycleTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
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
        _factory = new ApiHostFactory(database.AppConnectionString);
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(
        HttpMethod method, string path, string user = Anna, string roles = Underwriter, object? body = null, string? key = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.AcceptLanguage.ParseAdd("en");
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await _client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<string> EvaluateOldCarAsync(Guid job)
    {
        var result = await UwTestSupport.EvaluateInProcessAsync(
            _factory.Services,
            new { jobRef = job, checkpoint = "PRE_BIND", snapshotRef = $"snapshot-{job:N}", productCode = MotorProduct, effectiveDate = "2026-11-01", riskSnapshot = RiskTree("1991", "3000.00") },
            Anna);
        return result.Text("issues.0.issueId");
    }

    private async Task<bool> BlockedAsync(Guid job)
    {
        var (response, body) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/issues/blocking-status?jobRef={job}&blockingPoint=PRE_BIND");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["blocked"]!.GetValue<bool>();
    }

    private async Task ApproveAsync(string issueId) =>
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/decide", Boss, Manager, new { issueIds = new[] { issueId }, decision = "APPROVE", reason = "Inspected." }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.OK);

    private async Task<long> CountAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task Reopening_an_approved_issue_invalidates_the_approval_and_blocks_again()
    {
        var job = Guid.CreateVersion7();
        var issueId = await EvaluateOldCarAsync(job);
        await ApproveAsync(issueId);
        (await BlockedAsync(job)).ShouldBeFalse();

        var (response, body) = await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/reopen", Boss, Manager, new { issueId, reason = "New loss information." });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var reopened = body!["issue"]!;
        reopened.Text("status").ShouldBe("Open");
        reopened.Text("id").ShouldNotBe(issueId);
        reopened["decision"].ShouldBeNull();

        (await BlockedAsync(job)).ShouldBeTrue();
        var (_, old) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/issues/{issueId}");
        old.Text("issue.status").ShouldBe("Invalidated");
        old.Text("issue.closeReason").ShouldBe("MANUAL_REOPEN");
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ApprovalInvalidated' AND payload::text LIKE '%{issueId}%' AND payload::text LIKE '%MANUAL_REOPEN%'"))
            .ShouldBe(await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ApprovalInvalidated' AND payload::text LIKE '%{issueId}%'"));
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ApprovalInvalidated' AND payload::text LIKE '%{issueId}%'")).ShouldBe(1);

        var (_, history) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/approval/history?jobRef={job}");
        history!["items"]!.AsArray().Select(r => r.Text("event")).ShouldBe(["DECIDED", "INVALIDATED"]);

        // The new issue can be decided afresh; an Open issue cannot be reopened.
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/reopen", Boss, Manager, new { issueId = reopened.Text("id"), reason = "Again." }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        await ApproveAsync(reopened.Text("id"));
        (await BlockedAsync(job)).ShouldBeFalse();
    }

    [Fact]
    public async Task Reopen_validates_replays_and_refuses_what_it_cannot_reopen()
    {
        var job = Guid.CreateVersion7();
        var issueId = await EvaluateOldCarAsync(job);
        var key = Guid.NewGuid().ToString();

        (await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/reopen", body: new { issueId, reason = "x" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/reopen", body: new { issueId, reason = " " })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/reopen", body: new { issueId = Guid.CreateVersion7(), reason = "x" })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/reopen", roles: "Staff.Billing", body: new { issueId, reason = "x" })).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await ApproveAsync(issueId);
        var (first, firstBody) = await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/reopen", Boss, Manager, new { issueId, reason = "Replay." }, key);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, firstBody?.ToJsonString());
        var (again, againBody) = await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/reopen", Boss, Manager, new { issueId, reason = "Replay." }, key);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        againBody.Text("issue.id").ShouldBe(firstBody.Text("issue.id"));
        (await CountAsync($"SELECT count(*) FROM uw.issue WHERE job_id = '{job}'")).ShouldBe(2);

        // A stale expectedRecordVersion on the (now Invalidated) issue is refused before anything changes.
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/reopen", Boss, Manager, new { issueId, reason = "x", expectedRecordVersion = 1 }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_manual_issue_blocks_its_point_until_decided_and_survives_evaluation()
    {
        var job = Guid.CreateVersion7();
        var rule = await EvaluateOldCarAsync(job);
        await ApproveAsync(rule);
        (await BlockedAsync(job)).ShouldBeFalse();

        var (response, body) = await SendAsync(
            HttpMethod.Post, "/api/uw/v1/issues/add-manual", body: new { jobRef = job, issueType = "VEHICLE_AGE_REFERRAL", blockingPoint = "PRE_BIND", reason = "Broker mentioned undisclosed modifications." });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var manual = body!["issue"]!;
        manual.Text("status").ShouldBe("Open");
        manual.Text("blockingPoint").ShouldBe("PRE_BIND");
        manual.Text("ruleId").ShouldBe("MANUAL");
        manual.Text("issueKey").ShouldStartWith("MANUAL:VEHICLE_AGE_REFERRAL:");
        (await BlockedAsync(job)).ShouldBeTrue();

        // A re-evaluation with the same facts neither closes nor invalidates it.
        await EvaluateOldCarAsync(job);
        var (_, got) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/issues/{manual.Text("id")}");
        got.Text("issue.status").ShouldBe("Open");
        (await BlockedAsync(job)).ShouldBeTrue();

        // It cannot be closed while it blocks; deciding it clears the point.
        var (closeRefused, closeBody) = await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/close", body: new { issueId = manual.Text("id"), reason = "Not needed." });
        closeRefused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, closeBody?.ToJsonString());
        closeBody.Text("code").ShouldBe("UW-ERR-ISSUE-TRANSITION");
        await ApproveAsync(manual.Text("id"));
        (await BlockedAsync(job)).ShouldBeFalse();

        // Unknown job (never evaluated) and unknown type are refused.
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/add-manual", body: new { jobRef = Guid.CreateVersion7(), issueType = "VEHICLE_AGE_REFERRAL", blockingPoint = "PRE_BIND", reason = "x" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/add-manual", body: new { jobRef = job, issueType = "NO_SUCH_TYPE", blockingPoint = "PRE_BIND", reason = "x" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Close_ends_a_non_blocking_manual_issue_and_an_approval_but_not_a_blocking_one()
    {
        var job = Guid.CreateVersion7();
        var rule = await EvaluateOldCarAsync(job);

        var (_, added) = await SendAsync(
            HttpMethod.Post, "/api/uw/v1/issues/add-manual", body: new { jobRef = job, issueType = "VEHICLE_AGE_REFERRAL", blockingPoint = "NON_BLOCKING", reason = "For information." });
        var info = added.Text("issue.id");
        var (closed, closedBody) = await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/close", body: new { issueId = info, reason = "Resolved by phone." });
        closed.StatusCode.ShouldBe(HttpStatusCode.OK, closedBody?.ToJsonString());
        closedBody.Text("issue.status").ShouldBe("Closed");
        closedBody.Text("issue.closeReason").ShouldBe("MANUAL_CLOSE");
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/close", body: new { issueId = info, reason = "Twice." })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // The rule's own Open blocking issue cannot be closed; an approved one can.
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/close", body: new { issueId = rule, reason = "No." })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        await ApproveAsync(rule);
        var (approvedClosed, _) = await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/close", body: new { issueId = rule, reason = "Superseded." });
        approvedClosed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'UWIssueClosed' AND payload::text LIKE '%MANUAL_CLOSE%' AND aggregate_id = '{job}'")).ShouldBe(2);
    }
}
