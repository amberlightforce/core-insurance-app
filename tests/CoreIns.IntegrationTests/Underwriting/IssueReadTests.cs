using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Policy;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Rating.RatingTestSupport;

namespace CoreIns.IntegrationTests.Underwriting;

/// <summary>
/// BO-UW-03 on a real PostgreSQL 17 through the real Host: batch evaluation (<c>uw.Rules.evaluateBatch</c>: no side effects, pinned
/// or in-force version, deterministic with the rule-set hash, size cap) and the issue reads (<c>uw.Issue.get</c>, <c>explain</c>,
/// <c>list</c> with filters and keyset paging, <c>listForChannel</c>, <c>uw.Approval.history</c>).
/// </summary>
public sealed class IssueReadTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
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

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, string user = Anna, string roles = Underwriter, object? body = null)
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

    private Task<JsonNode> EvaluateAsync(Guid job, JsonObject risk) => UwTestSupport.EvaluateInProcessAsync(
        _factory.Services,
        new { jobRef = job, checkpoint = "PRE_BIND", snapshotRef = $"snapshot-{job:N}", productCode = MotorProduct, effectiveDate = "2026-11-01", riskSnapshot = risk },
        Anna);

    private static JsonObject OldCar(string value = "3000.00") => RiskTree("1991", value);

    private async Task<long> CountAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<JsonNode> BatchOkAsync(object body)
    {
        var (response, result) = await SendAsync(HttpMethod.Post, "/api/uw/v1/rules/evaluate-batch", body: body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, result?.ToJsonString());
        return result!;
    }

    [Fact]
    public async Task Batch_evaluates_many_risks_without_side_effects_and_is_deterministic()
    {
        var before = (await CountAsync("SELECT count(*) FROM uw.evaluation"), await CountAsync("SELECT count(*) FROM uw.issue"));
        var body = new
        {
            productCode = MotorProduct, checkpoint = "PRE_BIND", effectiveDate = "2026-11-01",
            items = new object[]
            {
                new { itemRef = "typical", riskSnapshot = RiskTree() },
                new { itemRef = "old", riskSnapshot = OldCar() },
                new { itemRef = "underage", riskSnapshot = RiskTree(birthDate: "2015-06-15") },
                new { itemRef = "broken", riskSnapshot = new JsonObject { ["vehicle"] = new JsonObject() } },
            },
        };

        var first = await BatchOkAsync(body);
        var second = await BatchOkAsync(body);

        first.Text("pinned").ShouldBe("false");
        first.Text("ruleSetCode").ShouldBe("UW-MOTOR-GR-B");
        first.Text("ruleSetHash").ShouldBe(second.Text("ruleSetHash"));
        first["results"]!.ToJsonString().ShouldBe(second["results"]!.ToJsonString());
        var results = first["results"]!.AsArray();
        results.Select(r => r.Text("itemRef")).ShouldBe(["typical", "old", "underage", "broken"]);
        results.Select(r => r.Text("outcome")).ShouldBe(["ACCEPT", "REFER", "DECLINE", "ERROR"]);
        results[1].Text("hits.0.ruleId").ShouldBe("REFER-OLD-VEHICLE");
        results[3]!["error"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        (await CountAsync("SELECT count(*) FROM uw.evaluation")).ShouldBe(before.Item1);
        (await CountAsync("SELECT count(*) FROM uw.issue")).ShouldBe(before.Item2);
    }

    [Fact]
    public async Task Batch_pins_a_version_refuses_unknown_pins_and_more_than_200_items()
    {
        var pinned = await BatchOkAsync(new
        {
            productCode = MotorProduct, checkpoint = "PRE_BIND", ruleSetCode = "UW-MOTOR-GR-B", ruleSetVersion = "1.0",
            items = new object[] { new { itemRef = "old", riskSnapshot = OldCar() } },
        });
        pinned.Text("pinned").ShouldBe("true");
        pinned.Text("ruleSetVersion").ShouldBe("1.0");
        pinned.Text("results.0.outcome").ShouldBe("REFER");

        var (missing, missingBody) = await SendAsync(HttpMethod.Post, "/api/uw/v1/rules/evaluate-batch", body: new
        {
            productCode = MotorProduct, checkpoint = "PRE_BIND", ruleSetCode = "UW-MOTOR-GR-B", ruleSetVersion = "9.9",
            items = new object[] { new { itemRef = "x", riskSnapshot = RiskTree() } },
        });
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound, missingBody?.ToJsonString());
        missingBody.Text("code").ShouldBe("UW-ERR-RULESET-NOT-FOUND");

        var (half, _) = await SendAsync(HttpMethod.Post, "/api/uw/v1/rules/evaluate-batch", body: new
        {
            productCode = MotorProduct, checkpoint = "PRE_BIND", ruleSetCode = "UW-MOTOR-GR-B",
            items = new object[] { new { itemRef = "x", riskSnapshot = RiskTree() } },
        });
        half.StatusCode.ShouldNotBe(HttpStatusCode.OK);

        var tooMany = Enumerable.Range(0, 201).Select(i => new { itemRef = $"i{i}", riskSnapshot = RiskTree() }).ToArray();
        var (big, bigBody) = await SendAsync(HttpMethod.Post, "/api/uw/v1/rules/evaluate-batch", body: new { productCode = MotorProduct, checkpoint = "PRE_BIND", items = tooMany });
        big.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, bigBody?.ToJsonString());
        bigBody.Text("code").ShouldBe("UW-ERR-LIMIT-EXCEEDED");

        var (forbidden, _) = await SendAsync(HttpMethod.Post, "/api/uw/v1/rules/evaluate-batch", roles: "Staff.Billing", body: new { productCode = MotorProduct, checkpoint = "PRE_BIND", items = tooMany[..1] });
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_and_explain_show_the_rule_the_fact_and_the_threshold()
    {
        var job = Guid.CreateVersion7();
        var issueId = (await EvaluateAsync(job, OldCar())).Text("issues.0.issueId");

        var (response, got) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/issues/{issueId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, got?.ToJsonString());
        got.Text("issue.id").ShouldBe(issueId);
        got.Text("issue.status").ShouldBe("Open");
        got.Text("explanation.ruleId").ShouldBe("REFER-OLD-VEHICLE");
        got.Text("explanation.ruleSetCode").ShouldBe("UW-MOTOR-GR-B");

        var (explained, explain) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/issues/explain?issueId={issueId}");
        explained.StatusCode.ShouldBe(HttpStatusCode.OK, explain?.ToJsonString());
        explain.Text("explanation.fact").ShouldBe("vehicleAgeYears");
        explain.Text("explanation.limit").ShouldBe("20");
        explain.Text("explanation.observed").ShouldBe("35");
        explain.Text("explanation.explanationKeys.0").ShouldBe("REFER-OLD-VEHICLE");

        (await SendAsync(HttpMethod.Get, $"/api/uw/v1/issues/{Guid.CreateVersion7()}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, $"/api/uw/v1/issues/explain?issueId={Guid.CreateVersion7()}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Channel_list_hides_rule_ids_and_other_internals_and_follows_the_language()
    {
        var job = Guid.CreateVersion7();
        var issueId = (await EvaluateAsync(job, OldCar())).Text("issues.0.issueId");

        var (response, page) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/issues/list-for-channel?jobRef={job}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        var item = page!["items"]!.AsArray().ShouldHaveSingleItem()!.AsObject();
        item.Text("issueId").ShouldBe(issueId);
        item.Text("channelStatus").ShouldBe("UNDER_REVIEW");
        item.Text("message").ShouldBe("The vehicle is older than 20 years.");
        item.Select(p => p.Key).Order().ShouldBe(["blockingPoint", "channelStatus", "issueId", "issueType", "message", "raisedAt"]);
        page.ToJsonString().ShouldNotContain("REFER-OLD-VEHICLE");

        (await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/decide", Boss, Manager, new { issueIds = new[] { issueId }, decision = "APPROVE", reason = "Inspected." }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (_, cleared) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/issues/list-for-channel?jobRef={job}");
        cleared.Text("items.0.channelStatus").ShouldBe("CLEARED");
        cleared!.ToJsonString().ShouldNotContain(Boss);
        (await SendAsync(HttpMethod.Get, "/api/uw/v1/issues/list-for-channel")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Approval_history_shows_the_approval_and_then_its_invalidation()
    {
        var job = Guid.CreateVersion7();
        var issueId = (await EvaluateAsync(job, OldCar())).Text("issues.0.issueId");
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/decide", Boss, Manager, new { issueIds = new[] { issueId }, decision = "APPROVE", reason = "Inspected." }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await EvaluateAsync(job, OldCar("4500.00"))).Text("issues.0.approvalStatus").ShouldBe("Open");

        var (response, history) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/approval/history?jobRef={job}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, history?.ToJsonString());
        var rows = history!["items"]!.AsArray();
        rows.Select(r => r.Text("event")).ShouldBe(["DECIDED", "INVALIDATED"]);
        rows[0].Text("decision").ShouldBe("APPROVE");
        rows[0].Text("approver").ShouldBe("USER:" + Boss);
        rows[0].Text("reason").ShouldBe("Inspected.");
        rows[0]!["fingerprint"]!.GetValue<string>().Length.ShouldBe(64);
        rows[0]!["authorityCheckId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        rows[1].Text("reason").ShouldBe("VALUE_CHANGED");
        rows[1].Text("issueId").ShouldBe(issueId);
        rows[1]!["invalidatedByEvaluationId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();

        var key = rows[0].Text("issueKey");
        var (_, byKey) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/approval/history?jobRef={job}&issueKey={Uri.EscapeDataString(key)}&limit=1");
        byKey!["items"]!.AsArray().Count.ShouldBe(1);
        var (_, next) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/approval/history?jobRef={job}&limit=1&cursor={Uri.EscapeDataString(byKey.Text("nextCursor"))}");
        next.Text("items.0.event").ShouldBe("INVALIDATED");
        (await SendAsync(HttpMethod.Get, "/api/uw/v1/approval/history?cursor=bad")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_filters_by_type_and_blocking_point_and_pages_by_keyset()
    {
        var jobs = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
        foreach (var job in jobs)
        {
            await EvaluateAsync(job, OldCar());
        }

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var path = "/api/uw/v1/issues?status=Open&issueType=VEHICLE_AGE_REFERRAL&blockingPoint=PRE_BIND&limit=2" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            var (response, page) = await SendAsync(HttpMethod.Get, path);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
            seen.AddRange(page!["items"]!.AsArray().Select(i => i.Text("jobRef")));
            cursor = page["nextCursor"]?.GetValue<string>();
            pages++;
        }
        while (cursor is not null && pages < 50);

        seen.Distinct().Count().ShouldBe(seen.Count);
        jobs.Select(j => j.ToString("D")).ShouldAllBe(j => seen.Contains(j));
        (await SendAsync(HttpMethod.Get, "/api/uw/v1/issues?issueType=NO_SUCH_TYPE")).Body!["items"]!.AsArray().ShouldBeEmpty();
        (await SendAsync(HttpMethod.Get, "/api/uw/v1/issues?blockingPoint=SOMETIME")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Get, "/api/uw/v1/issues?cursor=bad")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
