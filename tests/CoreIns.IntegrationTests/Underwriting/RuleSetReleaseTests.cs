using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Rating.RatingTestSupport;

namespace CoreIns.IntegrationTests.Underwriting;

/// <summary>
/// Rule-set release (BO-UW-02: uw.RuleSet.submit, approve, schedule; REQ-UW-030..054) and the issue-type catalogue
/// (uw.IssueType.search, get, upsert) on a real PostgreSQL 17 through the real Host. The built-in UW-MOTOR-GR-B 1.0 is the source version.
/// </summary>
public sealed class RuleSetReleaseTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Manager = "Staff.UnderwritingManager";
    private const string Bind = "UW-MOTOR-GR-B";
    private const string Maker = "uw-maker";
    private const string Checker = "uw-checker";

    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static string Day(int offset) => Today.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _client = _factory.CreateClient();
        // The first evaluation publishes the built-in rule sets (UW-MOTOR-GR-B 1.0).
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/uw/v1/rules/evaluate", "uw-anna", "Staff.Underwriter", EvaluateBody("2026-11-01", RiskTree()));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static object EvaluateBody(string date, JsonObject risk) => new
    {
        jobRef = Guid.CreateVersion7(), checkpoint = "PRE_BIND", snapshotRef = "release-test", productCode = MotorProduct, effectiveDate = date, riskSnapshot = risk,
    };

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, string user, string roles, object? body = null)
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

    private Task<(HttpResponseMessage Response, JsonNode? Body)> RuleSetAsync(string op, object body, string user = Maker, string roles = Manager) =>
        SendAsync(HttpMethod.Post, "/api/uw/v1/rule-sets" + op, user, roles, body);

    private static object ClaimsRule(string id = "REFER-CLAIMS") => new
    {
        id,
        conditions = new[] { "-", "-", "-", "[3..4]", "-" },
        outputs = new[] { "\"REFER\"", "\"CLAIMS_REFERRAL\"", "\"REFER\"", "\"PRE_BIND\"", "\"Three or more claims.\"", "\"Τρεις ή περισσότερες ζημιές.\"" },
    };

    private static object Case(string name, int claims, params string[] expected) => new
    {
        name, firstRegistrationYear = "2020", vehicleValue = "15000.00", engineCc = 1400, usage = "PRIVATE", driverBirthDates = new[] { "1985-06-15" },
        claimsLast5Years = claims, effectiveDate = "2026-11-01", expectedRuleIds = expected,
    };

    private static object Patch(string op, object? rule = null, object? testCase = null) => new { op, rule, testCase };

    /// <summary>A new Draft of the built-in 1.0, optionally with the claims rule (and its two test cases) added.</summary>
    private async Task<string> DraftAsync(bool claimsRule)
    {
        var (draft, draftBody) = await RuleSetAsync("/draft", new { ruleSetCode = Bind, fromVersion = "1.0" });
        draft.StatusCode.ShouldBe(HttpStatusCode.OK, draftBody?.ToJsonString());
        var version = draftBody.Text("version.version");
        if (claimsRule)
        {
            var (edited, editBody) = await RuleSetAsync("/edit", new
            {
                ruleSetCode = Bind, version,
                patches = new[] { Patch("ADD_RULE", rule: ClaimsRule()), Patch("ADD_TEST_CASE", testCase: Case("three-claims", 3, "REFER-CLAIMS")), Patch("ADD_TEST_CASE", testCase: Case("no-claims", 0)) },
            });
            edited.StatusCode.ShouldBe(HttpStatusCode.OK, editBody?.ToJsonString());
            editBody!["validation"]!["valid"]!.GetValue<bool>().ShouldBeTrue(editBody.ToJsonString());
        }

        return version;
    }

    private async Task SubmitAndApproveAsync(string version)
    {
        var (submit, submitBody) = await RuleSetAsync("/submit", new { ruleSetCode = Bind, version, reason = "Release test" });
        submit.StatusCode.ShouldBe(HttpStatusCode.OK, submitBody?.ToJsonString());
        var (approve, approveBody) = await RuleSetAsync("/approve", new { ruleSetCode = Bind, version, decision = "Approve", reason = "Reviewed." }, Checker);
        approve.StatusCode.ShouldBe(HttpStatusCode.OK, approveBody?.ToJsonString());
        approveBody.Text("version.status").ShouldBe("Approved");
    }

    private async Task<string> EvaluateVersionAsync(string date, JsonObject risk)
    {
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/uw/v1/rules/evaluate", "uw-anna", "Staff.Underwriter", EvaluateBody(date, risk));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body.Text("ruleSetCode") + " " + body.Text("ruleSetVersion") + " " + string.Join(',', body!["reasons"]!.AsArray().Select(r => r!["ruleId"]!.GetValue<string>()));
    }

    // REQ-UW-043, -044: maker-checker release, then a scheduled version takes effect on its date and not before.
    [Fact]
    public async Task A_released_version_takes_effect_on_its_date_and_never_changes_afterwards()
    {
        var version = await DraftAsync(claimsRule: true);
        var date = Day(30);

        // Maker submits; the submitter cannot approve and a Draft/Submitted version cannot be edited or scheduled.
        var (submit, submitBody) = await RuleSetAsync("/submit", new { ruleSetCode = Bind, version, reason = "Three claims need an underwriter." });
        submit.StatusCode.ShouldBe(HttpStatusCode.OK, submitBody?.ToJsonString());
        submitBody.Text("version.status").ShouldBe("Submitted");
        submitBody.Text("version.submittedBy").ShouldBe("USER:" + Maker);
        submitBody.Text("approvalRequestId").ShouldNotBeNullOrEmpty();
        submitBody.Text("version.contentHash").Length.ShouldBe(64);

        var (selfApprove, selfBody) = await RuleSetAsync("/approve", new { ruleSetCode = Bind, version });
        selfApprove.StatusCode.ShouldBe(HttpStatusCode.Forbidden, selfBody?.ToJsonString());
        selfBody.Text("code").ShouldBe("PLT-ERR-SELF-APPROVAL");
        (await RuleSetAsync("/edit", new { ruleSetCode = Bind, version, patches = new[] { Patch("ADD_RULE", rule: ClaimsRule("X")) } })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await RuleSetAsync("/schedule", new { ruleSetCode = Bind, version, effectiveFrom = date })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // A person without the underwriting-manager permission cannot decide at all; the second manager can.
        (await RuleSetAsync("/approve", new { ruleSetCode = Bind, version }, Checker, "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (approve, approveBody) = await RuleSetAsync("/approve", new { ruleSetCode = Bind, version, reason = "Rules reviewed." }, Checker);
        approve.StatusCode.ShouldBe(HttpStatusCode.OK, approveBody?.ToJsonString());
        approveBody.Text("version.status").ShouldBe("Approved");
        approveBody.Text("version.decidedBy").ShouldBe("USER:" + Checker);

        // Never retroactive.
        var (past, pastBody) = await RuleSetAsync("/schedule", new { ruleSetCode = Bind, version, effectiveFrom = Day(-5) });
        past.StatusCode.ShouldBe(HttpStatusCode.BadRequest, pastBody?.ToJsonString());

        var (schedule, scheduleBody) = await RuleSetAsync("/schedule", new { ruleSetCode = Bind, version, effectiveFrom = date });
        schedule.StatusCode.ShouldBe(HttpStatusCode.OK, scheduleBody?.ToJsonString());
        scheduleBody.Text("version.status").ShouldBe("Active");
        scheduleBody.Text("version.effectiveFrom").ShouldBe(date);
        scheduleBody!["inEffect"]!.GetValue<bool>().ShouldBeFalse();

        // Before the date 1.0 decides (three claims do not refer); from the date the new version does.
        var risk = RiskTree(claims: 3);
        (await EvaluateVersionAsync(Day(29), risk)).ShouldBe($"{Bind} 1.0 ");
        (await EvaluateVersionAsync(date, risk)).ShouldBe($"{Bind} {version} REFER-CLAIMS");
        (await EvaluateVersionAsync(Day(45), risk)).ShouldBe($"{Bind} {version} REFER-CLAIMS");

        // An active version is never changed: not edited, not scheduled again, not submitted again.
        (await RuleSetAsync("/edit", new { ruleSetCode = Bind, version, patches = new[] { Patch("ADD_RULE", rule: ClaimsRule("Y")) } })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await RuleSetAsync("/schedule", new { ruleSetCode = Bind, version, effectiveFrom = Day(60) })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await RuleSetAsync("/submit", new { ruleSetCode = Bind, version })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Two versions cannot take effect on the same date for one product and checkpoint.
        var other = await DraftAsync(claimsRule: false);
        await SubmitAndApproveAsync(other);
        var (clash, clashBody) = await RuleSetAsync("/schedule", new { ruleSetCode = Bind, version = other, effectiveFrom = date });
        clash.StatusCode.ShouldBe(HttpStatusCode.Conflict, clashBody?.ToJsonString());
    }

    // REQ-UW-036, -038: only a version that validates can be submitted.
    [Fact]
    public async Task Submit_refuses_an_empty_table_and_an_untested_rule()
    {
        var (created, createdBody) = await RuleSetAsync(string.Empty, new { ruleSetCode = "UW-REL-EMPTY", productCode = MotorProduct, checkpoint = "PRE_BIND" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, createdBody?.ToJsonString());
        var (empty, emptyBody) = await RuleSetAsync("/submit", new { ruleSetCode = "UW-REL-EMPTY", version = "1.0" });
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest, emptyBody?.ToJsonString());

        var version = await DraftAsync(claimsRule: false);
        var (edited, editBody) = await RuleSetAsync("/edit", new { ruleSetCode = Bind, version, patches = new[] { Patch("ADD_RULE", rule: ClaimsRule("REFER-UNTESTED")) } });
        edited.StatusCode.ShouldBe(HttpStatusCode.OK, editBody?.ToJsonString());
        var (untested, untestedBody) = await RuleSetAsync("/submit", new { ruleSetCode = Bind, version });
        untested.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, untestedBody?.ToJsonString());
        untestedBody.Text("code").ShouldBe("UW-ERR-RULE-UNTESTED");

        (await RuleSetAsync("/submit", new { ruleSetCode = "NO-SUCH", version = "1.0" })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // A rejection returns the version to Draft with the checker's reason; it can be edited and submitted again.
    [Fact]
    public async Task A_rejected_version_returns_to_draft_and_can_be_resubmitted()
    {
        var version = await DraftAsync(claimsRule: false);
        (await RuleSetAsync("/submit", new { ruleSetCode = Bind, version })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (noReason, _) = await RuleSetAsync("/approve", new { ruleSetCode = Bind, version, decision = "Reject" }, Checker);
        noReason.IsSuccessStatusCode.ShouldBeFalse();
        var (rejected, rejectedBody) = await RuleSetAsync("/approve", new { ruleSetCode = Bind, version, decision = "Reject", reason = "Thresholds need a review." }, Checker);
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, rejectedBody?.ToJsonString());
        rejectedBody.Text("version.status").ShouldBe("Draft");
        rejectedBody.Text("version.decisionReason").ShouldBe("Thresholds need a review.");

        (await RuleSetAsync("/edit", new { ruleSetCode = Bind, version, patches = new[] { Patch("ADD_RULE", rule: ClaimsRule("REFER-CLAIMS-R")), Patch("ADD_TEST_CASE", testCase: Case("three-claims", 3, "REFER-CLAIMS-R")) } })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (again, againBody) = await RuleSetAsync("/submit", new { ruleSetCode = Bind, version });
        again.StatusCode.ShouldBe(HttpStatusCode.OK, againBody?.ToJsonString());
        againBody.Text("version.status").ShouldBe("Submitted");
        againBody!["version"]!["decisionReason"].ShouldBeNull();
    }

    // Edit optimistic concurrency: the recordVersion read must still be the stored one.
    [Fact]
    public async Task An_edit_with_a_stale_record_version_is_refused()
    {
        var (draft, draftBody) = await RuleSetAsync("/draft", new { ruleSetCode = Bind, fromVersion = "1.0" });
        draft.StatusCode.ShouldBe(HttpStatusCode.OK, draftBody?.ToJsonString());
        var version = draftBody.Text("version.version");
        var read = draftBody!["version"]!["recordVersion"]!.GetValue<int>();

        var (first, firstBody) = await RuleSetAsync("/edit", new { ruleSetCode = Bind, version, recordVersion = read, patches = new[] { Patch("ADD_RULE", rule: ClaimsRule("C1")) } });
        first.StatusCode.ShouldBe(HttpStatusCode.OK, firstBody?.ToJsonString());
        firstBody!["version"]!["recordVersion"]!.GetValue<int>().ShouldBe(read + 1);

        var (stale, staleBody) = await RuleSetAsync("/edit", new { ruleSetCode = Bind, version, recordVersion = read, patches = new[] { Patch("ADD_RULE", rule: ClaimsRule("C2")) } });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, staleBody?.ToJsonString());
        staleBody.Text("code").ShouldBe("UW-ERR-STALE");
    }

    // REQ-UW-092, D-UW-01: an approval holds for the facts it saw under one rule-set version; a new version invalidates it.
    [Fact]
    public async Task A_new_rule_version_invalidates_old_approvals()
    {
        var job = Guid.CreateVersion7();
        var risk = RiskTree("1991", "3000.00");
        object Body(string date) => new { jobRef = job, checkpoint = "PRE_BIND", snapshotRef = $"snapshot-{job:N}", productCode = MotorProduct, effectiveDate = date, riskSnapshot = risk };

        var raised = await UwTestSupport.EvaluateInProcessAsync(_factory.Services, Body("2026-11-01"), "uw-anna");
        raised.Text("ruleSetVersion").ShouldBe("1.0");
        var issueId = raised.Text("issues.0.issueId");
        var (decided, decision) = await SendAsync(HttpMethod.Post, "/api/uw/v1/issues/decide", "uw-boss", Manager, new { issueIds = new[] { issueId }, decision = "APPROVE", reason = "Vehicle inspected; acceptable." });
        decided.StatusCode.ShouldBe(HttpStatusCode.OK, decision?.ToJsonString());

        // The same facts under the same version keep the approval.
        var same = await UwTestSupport.EvaluateInProcessAsync(_factory.Services, Body("2026-11-01"), "uw-anna");
        same.Text("issues.0.approvalStatus").ShouldBe("Approved");

        var version = await DraftAsync(claimsRule: false);
        await SubmitAndApproveAsync(version);
        var date = Day(90);
        var (schedule, scheduleBody) = await RuleSetAsync("/schedule", new { ruleSetCode = Bind, version, effectiveFrom = date });
        schedule.StatusCode.ShouldBe(HttpStatusCode.OK, scheduleBody?.ToJsonString());

        var next = await UwTestSupport.EvaluateInProcessAsync(_factory.Services, Body(date), "uw-anna");
        next.Text("ruleSetVersion").ShouldBe(version);
        next.Text("issues.0.approvalStatus").ShouldBe("Open");
        next.Text("issues.0.issueId").ShouldNotBe(issueId);
        (await CountAsync($"SELECT count(*) FROM uw.issue WHERE issue_id = '{issueId}' AND status = 'Invalidated'")).ShouldBe(1);
    }

    // The release routes are for underwriting managers and administrators only.
    [Fact]
    public async Task Release_needs_the_manager_role()
    {
        foreach (var op in new[] { "/submit", "/approve", "/schedule" })
        {
            var (response, _) = await RuleSetAsync(op, new { ruleSetCode = Bind, version = "1.0" }, Maker, "Staff.Underwriter");
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, op);
        }
    }

    // BO-UW-02: the issue-type catalogue (seeded from the built-in rule sets) is searched, read and maintained with optimistic concurrency.
    [Fact]
    public async Task The_issue_type_catalogue_is_seeded_searched_and_maintained()
    {
        var (search, searchBody) = await SendAsync(HttpMethod.Get, "/api/uw/v1/issue-types/search?limit=2", Maker, "Staff.Underwriter");
        search.StatusCode.ShouldBe(HttpStatusCode.OK, searchBody?.ToJsonString());
        searchBody!["items"]!.AsArray().Count.ShouldBe(2);
        var cursor = searchBody.Text("nextCursor");
        var (page2, page2Body) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/issue-types/search?limit=50&cursor={Uri.EscapeDataString(cursor)}", Maker, "Staff.Underwriter");
        page2.StatusCode.ShouldBe(HttpStatusCode.OK, page2Body?.ToJsonString());
        page2Body!["items"]!.AsArray().Select(i => i!["code"]!.GetValue<string>()).ShouldContain("VEHICLE_AGE_REFERRAL");

        var (found, foundBody) = await SendAsync(HttpMethod.Get, "/api/uw/v1/issue-types/search?q=ηλικ", Maker, "Staff.Underwriter");
        found.StatusCode.ShouldBe(HttpStatusCode.OK, foundBody?.ToJsonString());
        foundBody!["items"]!.AsArray().Select(i => i!["code"]!.GetValue<string>()).ShouldBe(["DRIVER_AGE_REFERRAL", "VEHICLE_AGE_REFERRAL"]);

        var (got, gotBody) = await SendAsync(HttpMethod.Get, "/api/uw/v1/issue-types/DRIVER_AGE_REFERRAL", Maker, "Staff.Underwriter");
        got.StatusCode.ShouldBe(HttpStatusCode.OK, gotBody?.ToJsonString());
        gotBody.Text("blockingPoint").ShouldBe("PRE_BIND");
        gotBody.Text("defaultLane").ShouldBe("ASSISTED");
        (await SendAsync(HttpMethod.Get, "/api/uw/v1/issue-types/NO_SUCH", Maker, "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        object Upsert(int? recordVersion = null, string nameEn = "Flood zone") => new
        {
            code = "FLOOD_ZONE_REFERRAL", nameEl = "Παραπομπή πλημμυρικής ζώνης", nameEn, severity = "REFER", defaultLane = "EXPERT", blockingPoint = "PRE_ISSUE", recordVersion,
        };

        var (created, createdBody) = await SendAsync(HttpMethod.Post, "/api/uw/v1/issue-types/upsert", Maker, Manager, Upsert());
        created.StatusCode.ShouldBe(HttpStatusCode.OK, createdBody?.ToJsonString());
        createdBody!["recordVersion"]!.GetValue<int>().ShouldBe(1);
        createdBody!["active"]!.GetValue<bool>().ShouldBeTrue();
        createdBody.Text("blockingPoint").ShouldBe("PRE_ISSUE");

        (await SendAsync(HttpMethod.Post, "/api/uw/v1/issue-types/upsert", Maker, Manager, Upsert())).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict); // exists: send the recordVersion
        var (changed, changedBody) = await SendAsync(HttpMethod.Post, "/api/uw/v1/issue-types/upsert", Maker, Manager, Upsert(1, "Flood zone (revised)"));
        changed.StatusCode.ShouldBe(HttpStatusCode.OK, changedBody?.ToJsonString());
        changedBody!["recordVersion"]!.GetValue<int>().ShouldBe(2);
        changedBody.Text("nameEn").ShouldBe("Flood zone (revised)");
        var (stale, staleBody) = await SendAsync(HttpMethod.Post, "/api/uw/v1/issue-types/upsert", Maker, Manager, Upsert(1));
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, staleBody?.ToJsonString());
        staleBody.Text("code").ShouldBe("UW-ERR-STALE");

        var (badCode, _) = await SendAsync(HttpMethod.Post, "/api/uw/v1/issue-types/upsert", Maker, Manager, new
        {
            code = "bad code", nameEl = "α", nameEn = "a", severity = "REFER", defaultLane = "ASSISTED", blockingPoint = "PRE_BIND",
        });
        badCode.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/issue-types/upsert", Maker, "Staff.Underwriter", Upsert())).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
