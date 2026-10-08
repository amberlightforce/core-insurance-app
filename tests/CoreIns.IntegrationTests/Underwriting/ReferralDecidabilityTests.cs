using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Policy;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Services;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Rating.RatingTestSupport;

namespace CoreIns.IntegrationTests.Underwriting;

/// <summary>
/// The referral workbench's decidability preview, the MINE queue and the rule explanation (SL5-UW-WB-API; REQ-UW-078, -010, -111,
/// SOD-UW-02, BR-UW-013) with every module real. One job is created by <c>uw-creator</c>, its coverage edited by <c>uw-editor</c>
/// and quoted and bound by <c>uw-quoter</c>; <c>uw-outsider</c> is an uninvolved senior underwriter. The preview and the commit
/// must never disagree (PITFALLS 5-6): the table-driven parity test compares them for every caller. The preview writes nothing.
/// </summary>
public sealed class ReferralDecidabilityTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Seniors = "Staff.Underwriter,Staff.UnderwritingManager";
    private const string Creator = "uw-creator";
    private const string Editor = "uw-editor";
    private const string Quoter = "uw-quoter";
    private const string Outsider = "uw-outsider";

    private PolicySlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _slice = new PolicySlice(database.AppConnectionString, realRatingAndUnderwriting: true);
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private static string YoungBirthDate => DateTime.UtcNow.AddYears(-19).AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void As(string user)
    {
        _slice.Client.DefaultRequestHeaders.Remove(TestAuthHandler.UserHeader);
        _slice.Client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body, string Text)> AsAsync(
        HttpMethod method, string path, string user, string roles = Seniors, object? body = null)
    {
        _slice.Client.DefaultRequestHeaders.Remove(TestAuthHandler.UserHeader);
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

        var response = await _slice.Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text), text);
    }

    /// <summary>A job created by Creator, edited by Editor and quoted and bound (referred) by Quoter: three PRE_BIND referrals (1991 car worth 120,000, 19-year-old driver).</summary>
    private async Task<string> ReferredJobAsync()
    {
        try
        {
            As(Creator);
            var party = await _slice.CreatePartyAsync(YoungBirthDate);
            var (created, submission) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/submissions", _slice.Submission(party, DateTimeOffset.UtcNow.AddDays(2)));
            created.StatusCode.ShouldBe(HttpStatusCode.Created, submission?.ToJsonString());
            var jobId = submission.Text("jobId");
            var risk = PolicySlice.MotorRisk();
            risk[0] = new
            {
                op = "SET_VEHICLE",
                vehicle = new
                {
                    plate = "ikx-1991", make = "Mercedes-Benz", model = "190E", firstRegistrationYear = 1991, engineCapacityCc = 2000, use = "PRIVATE",
                    value = new { amount = "120000.00", currency = "EUR" },
                },
            };
            var (first, draft) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/jobs/update-draft", new { jobId, versionNo = 1, expectedDraftVersion = 0, instructions = risk });
            first.StatusCode.ShouldBe(HttpStatusCode.OK, draft?.ToJsonString());

            As(Editor);
            var (second, filled) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/jobs/update-draft",
                new { jobId, versionNo = 1, expectedDraftVersion = 1, instructions = PolicySlice.DriverAndCovers(draft.Text("riskTree.vehicles.0.locator"), party) });
            second.StatusCode.ShouldBe(HttpStatusCode.OK, filled?.ToJsonString());

            As(Quoter);
            (await _slice.QuoteAsync(jobId)).Body.Text("state").ShouldBe("QUOTED");
            (await _slice.BindAsync(jobId)).Body.Text("state").ShouldBe("QUOTED");
            return jobId;
        }
        finally
        {
            _slice.Client.DefaultRequestHeaders.Remove(TestAuthHandler.UserHeader);
        }
    }

    private async Task<JsonNode> ReferralAsync(string jobId, string user, string roles = Seniors)
    {
        var (response, body, text) = await AsAsync(HttpMethod.Get, $"/api/uw/v1/referrals/{jobId}", user, roles);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        return body!["referral"]!;
    }

    private async Task<JsonNode> QueueAsync(string queue, string user, string roles = Seniors)
    {
        var (response, body, text) = await AsAsync(HttpMethod.Get, $"/api/uw/v1/referrals?queue={queue}&limit=100", user, roles);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        return body!;
    }

    private static bool InQueue(JsonNode page, string jobId) => page["items"]!.AsArray().Any(i => i!["jobRef"]!.GetValue<string>() == jobId);

    private static string[] Reasons(JsonNode? node) => [.. node!.AsArray().Select(r => r!.GetValue<string>())];

    private async Task<long> CountAsync(string sql)
    {
        await using var dataSource = Npgsql.NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static string IssueId(JsonNode referral, string issueType) =>
        referral["issues"]!.AsArray().Single(i => i!["issue"]!["issueType"]!.GetValue<string>() == issueType)!["issue"]!["id"]!.GetValue<string>();

    public static TheoryData<string, string, bool, string?> Callers => new()
    {
        { Creator, Seniors, false, "SOD_CREATOR" },
        { Editor, Seniors, false, "SOD_PARTICIPANT" },
        { Quoter, Seniors, false, "SOD_EVALUATOR" },
        { Outsider, Seniors, true, null },
    };

    [Theory]
    [MemberData(nameof(Callers))]
    public async Task The_preview_names_the_reason_and_the_commit_agrees(string user, string roles, bool canDecide, string? reason)
    {
        var jobId = await ReferredJobAsync();

        var referral = await ReferralAsync(jobId, user, roles);

        referral["decidability"]!["canDecide"]!.GetValue<bool>().ShouldBe(canDecide);
        if (reason is not null)
        {
            Reasons(referral["decidability"]!["reasons"]).ShouldContain(reason);
        }
        else
        {
            Reasons(referral["decidability"]!["reasons"]).ShouldBeEmpty();
        }

        foreach (var issue in referral["issues"]!.AsArray())
        {
            var d = issue!["decidability"]!;
            d["canDecide"]!.GetValue<bool>().ShouldBe(canDecide);
            d["authority"]!["type"]!.GetValue<string>().ShouldBe("UW.ISSUE_APPROVAL");
            d["authority"]!["outcome"]!.GetValue<string>().ShouldBe("ALLOW");
        }

        if (canDecide)
        {
            referral["issues"]![0]!["decidability"]!["authority"]!["sourceGrantId"]!.GetValue<string>().ShouldBe("uw-manager-issue-approval-illustrative");
        }

        // Parity: the commit gives the same allow/deny as the preview, for the same caller and roles.
        var (decide, decideBody, text) = await AsAsync(HttpMethod.Post, "/api/uw/v1/issues/decide", user, roles, new
        {
            issueIds = new[] { IssueId(referral, "DRIVER_AGE_REFERRAL") }, decision = "APPROVE", reason = "Parity probe.",
        });
        if (canDecide)
        {
            decide.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        }
        else
        {
            decide.StatusCode.ShouldBe(HttpStatusCode.Forbidden, text);
            decideBody.Text("code").ShouldBeOneOf("UW-ERR-SOD", "UW-ERR-AUTHORITY-DENIED");
        }
    }

    [Fact]
    public async Task A_platform_admin_without_authority_is_denied_by_both_the_preview_and_the_commit()
    {
        var jobId = await ReferredJobAsync();
        var referral = await ReferralAsync(jobId, "uw-admin", "Platform.Admin");
        var issue = referral["issues"]![0]!;
        issue["decidability"]!["authority"]!["outcome"]!.GetValue<string>().ShouldBe("DENY");

        // Admin holds no uw.Issue.decide permission at all: 403 at the door, never a decision.
        var (decide, _, _) = await AsAsync(HttpMethod.Post, "/api/uw/v1/issues/decide", "uw-admin", "Platform.Admin", new
        {
            issueIds = new[] { issue["issue"]!["id"]!.GetValue<string>() }, decision = "APPROVE", reason = "Admin probe.",
        });
        decide.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_service_identity_is_not_human_in_the_preview()
    {
        var jobId = await ReferredJobAsync();
        await using var scope = Scope(_slice.Factory.Services, Seniors);
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.Service("batch-job");

        var result = await scope.ServiceProvider.GetRequiredService<ReferralQueries>().GetAsync(jobId, Ct);

        result.IsSuccess.ShouldBeTrue();
        var decidability = result.Value.Referral.Decidability;
        decidability.CanDecide.ShouldBeFalse();
        decidability.Reasons.ShouldBe([DecidabilityReason.NotHuman]);
    }

    [Fact]
    public async Task The_preview_writes_no_audit_event_approval_request_or_decision()
    {
        var jobId = await ReferredJobAsync();
        const string audit = "SELECT count(*) FROM plt.audit_event WHERE operation = 'uw.Issue.decide'";
        const string approvals = "SELECT count(*) FROM plt.approval_request";
        const string decided = "SELECT count(*) FROM uw.issue WHERE decision IS NOT NULL";
        var before = (await CountAsync(audit), await CountAsync(approvals), await CountAsync(decided));

        for (var i = 0; i < 3; i++)
        {
            await ReferralAsync(jobId, Outsider);
            await QueueAsync("MINE", Outsider);
            await QueueAsync("OPEN", Outsider);
        }

        (await CountAsync(audit), await CountAsync(approvals), await CountAsync(decided)).ShouldBe(before);
    }

    [Fact]
    public async Task Mine_holds_exactly_the_decidable_referrals_and_a_decision_moves_the_job_out_of_it()
    {
        var jobId = await ReferredJobAsync();

        foreach (var involved in new[] { Creator, Editor, Quoter })
        {
            var page = await QueueAsync("MINE", involved);
            InQueue(page, jobId).ShouldBeFalse(involved);
            page["counts"]!["mine"]!.GetValue<int>().ShouldBe(page["items"]!.AsArray().Count);
        }

        var mine = await QueueAsync("MINE", Outsider);
        InQueue(mine, jobId).ShouldBeTrue();
        mine["counts"]!["mine"]!.GetValue<int>().ShouldBe(mine["items"]!.AsArray().Count);
        mine["counts"]!["open"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(mine["counts"]!["mine"]!.GetValue<int>());
        InQueue(await QueueAsync("OPEN", Outsider), jobId).ShouldBeTrue();

        var referral = await ReferralAsync(jobId, Outsider);
        var ids = referral["issues"]!.AsArray().Select(i => i!["issue"]!["id"]!.GetValue<string>()).ToArray();
        var (decide, _, text) = await AsAsync(HttpMethod.Post, "/api/uw/v1/issues/decide", Outsider, body: new { issueIds = ids, decision = "APPROVE", reason = "All three checked." });
        decide.StatusCode.ShouldBe(HttpStatusCode.OK, text);

        var after = await QueueAsync("MINE", Outsider);
        InQueue(after, jobId).ShouldBeFalse();
        after["counts"]!["mine"]!.GetValue<int>().ShouldBe(after["items"]!.AsArray().Count);
        InQueue(await QueueAsync("DECIDED_BY_ME_TODAY", Outsider), jobId).ShouldBeTrue();
        var decided = await ReferralAsync(jobId, Outsider);
        decided["decidability"]!["canDecide"]!.GetValue<bool>().ShouldBeFalse();
        Reasons(decided["decidability"]!["reasons"]).ShouldBe(["NOT_OPEN"]);
        decided["issues"]!.AsArray().ShouldAllBe(i => i!["decidability"]!["reasons"]!.AsArray().Any(r => r!.GetValue<string>() == "NOT_OPEN"));
    }

    [Fact]
    public async Task Mine_pages_with_a_cursor_and_honours_the_page_size()
    {
        await ReferredJobAsync();
        await ReferredJobAsync();

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var (response, body, text) = await AsAsync(HttpMethod.Get, "/api/uw/v1/referrals?queue=MINE&limit=1" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), Outsider);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
            seen.AddRange(body!["items"]!.AsArray().Select(i => i!["jobRef"]!.GetValue<string>()));
            cursor = body["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null && seen.Count < 100);

        seen.Count.ShouldBeGreaterThanOrEqualTo(2);
        seen.ShouldBeUnique();
        seen.Count.ShouldBe((await QueueAsync("MINE", Outsider))["counts"]!["mine"]!.GetValue<int>());
    }

    [Fact]
    public async Task Mine_over_the_cap_is_refused_and_the_count_is_withheld()
    {
        await ReferredJobAsync();
        // 500 more open jobs in the entity (synthetic rows): copy one open issue under new job ids.
        await database.ExecuteAsSuperuserAsync(
            """
            INSERT INTO uw.issue (issue_id, legal_entity_id, job_id, issue_type, issue_key, blocking_point, severity, lane, status, rule_id, message_en, message_el,
                                  record_version, created_at, raised_evaluation_id, fingerprint)
            SELECT gen_random_uuid(), i.legal_entity_id, gen_random_uuid(), i.issue_type, 'cap-' || n, i.blocking_point, i.severity, i.lane, 'Open', i.rule_id, i.message_en, i.message_el,
                   1, now(), i.raised_evaluation_id, i.fingerprint
              FROM (SELECT * FROM uw.issue WHERE status = 'Open' LIMIT 1) i, generate_series(1, 501) n
            """, Ct);

        try
        {
            var (response, body, _) = await AsAsync(HttpMethod.Get, "/api/uw/v1/referrals?queue=MINE", Outsider);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            body.Text("code").ShouldBe("UW-ERR-VALIDATION");
            var open = await QueueAsync("OPEN", Outsider);
            open["counts"]!["open"]!.GetValue<int>().ShouldBeGreaterThan(500);
            open["counts"]!["mine"].ShouldBeNull();
        }
        finally
        {
            // The database is shared by the tests of this class.
            await database.ExecuteAsSuperuserAsync("DELETE FROM uw.issue WHERE issue_key LIKE 'cap-%'", Ct);
        }
    }

    [Fact]
    public async Task Observed_and_limit_show_the_fact_and_the_rule_threshold_and_no_birth_date()
    {
        var jobId = await ReferredJobAsync();

        var (response, body, text) = await AsAsync(HttpMethod.Get, $"/api/uw/v1/referrals/{jobId}", Outsider);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        var reasons = body!["referral"]!["summary"]!["reasons"]!.AsArray();
        JsonNode Reason(string rule) => reasons.Single(r => r!["ruleId"]!.GetValue<string>() == rule)!;
        var vehicle = Reason("REFER-OLD-VEHICLE");
        int.Parse(vehicle["observed"]!.GetValue<string>(), CultureInfo.InvariantCulture).ShouldBeGreaterThan(30);
        vehicle["limit"]!.GetValue<string>().ShouldBe("20");
        vehicle["limitUnavailableReason"].ShouldBeNull();
        var value = Reason("REFER-HIGH-VALUE");
        value["observed"]!.GetValue<string>().ShouldBe("120000.00");
        value["limit"]!.GetValue<string>().ShouldBe("100000");
        var driver = Reason("REFER-YOUNG-DRIVER");
        driver["observed"]!.GetValue<string>().ShouldBe("FROM_18_TO_20");
        driver["limit"]!.GetValue<string>().ShouldBe("21");
        text.ShouldNotContain(YoungBirthDate);
    }

    [Fact]
    public async Task A_rule_without_a_declared_limit_says_why()
    {
        var jobId = await ReferredJobAsync();
        // Strip the stored explain of one rule (a rule set stored by an older build) and use an unknown rule id: no limit, with the reason.
        await database.ExecuteAsSuperuserAsync("UPDATE uw.issue SET rule_id = 'REFER-UNDECLARED' WHERE job_id = '" + jobId + "' AND issue_type = 'VEHICLE_AGE_REFERRAL'", Ct);

        var referral = await ReferralAsync(jobId, Outsider);

        var undeclared = referral["summary"]!["reasons"]!.AsArray().Single(r => r!["ruleId"]!.GetValue<string>() == "REFER-UNDECLARED")!;
        undeclared["limit"].ShouldBeNull();
        undeclared["observed"].ShouldBeNull();
        undeclared["limitUnavailableReason"]!.GetValue<string>().ShouldBe("RULE_DECLARES_NO_LIMIT");
    }

    [Fact]
    public async Task A_rejection_is_not_decidable_again_and_a_fact_refresh_does_not_reopen_the_preview()
    {
        var jobId = await ReferredJobAsync();
        var referral = await ReferralAsync(jobId, Outsider);
        var (rejected, _, text) = await AsAsync(HttpMethod.Post, "/api/uw/v1/issues/decide", Outsider, body: new
        {
            issueIds = new[] { IssueId(referral, "VEHICLE_AGE_REFERRAL") }, decision = "REJECT", reason = "Outside appetite.",
        });
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, text);

        // A fresh evaluation by the quoter with unchanged facts (PITFALLS 6) must not turn the rejection decidable again.
        try
        {
            As(Quoter);
            await _slice.BindAsync(jobId);
        }
        finally
        {
            _slice.Client.DefaultRequestHeaders.Remove(TestAuthHandler.UserHeader);
        }

        var after = await ReferralAsync(jobId, Outsider);
        var rejectedIssue = after["issues"]!.AsArray().Single(i => i!["issue"]!["issueType"]!.GetValue<string>() == "VEHICLE_AGE_REFERRAL")!;
        rejectedIssue["issue"]!["status"]!.GetValue<string>().ShouldBe("Rejected");
        rejectedIssue["decidability"]!["canDecide"]!.GetValue<bool>().ShouldBeFalse();
        Reasons(rejectedIssue["decidability"]!["reasons"]).ShouldContain("NOT_OPEN");
    }

    [Fact]
    public async Task The_workbench_withholds_other_entities_p2_and_underwriters_without_the_manager_role()
    {
        var jobId = await ReferredJobAsync();

        var (list, _, _) = await AsAsync(HttpMethod.Get, "/api/uw/v1/referrals?queue=MINE", "uw-plain", "Staff.Underwriter");
        list.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (get, _, _) = await AsAsync(HttpMethod.Get, $"/api/uw/v1/referrals/{jobId}", "uw-plain", "Staff.Underwriter");
        get.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var (_, _, text) = await AsAsync(HttpMethod.Get, $"/api/uw/v1/referrals/{jobId}", Outsider);
        text.ShouldNotContain(YoungBirthDate);
        text.ShouldNotContain("birthDate", Case.Insensitive);
        (await AsAsync(HttpMethod.Get, $"/api/uw/v1/referrals?queue=MINE&limit=100", Outsider)).Text.ShouldNotContain(YoungBirthDate);
    }

    [Fact]
    public async Task Another_legal_entity_never_sees_the_referral_in_mine_and_cannot_get_it()
    {
        var jobId = await ReferredJobAsync();
        await database.ExecuteAsSuperuserAsync(
            """
            INSERT INTO mkt.legal_entity
                (legal_entity_id, code, native_name, latin_name, home_jurisdiction, pack_id, functional_currency, timezone, status, is_test_entity, record_version, created_at)
            VALUES ('0192f0c4-0000-7000-8000-0000000000c2', 'GR-OTHER', 'GR-OTHER (synthetic)', 'GR-OTHER (synthetic)', 'GR', 'gr', 'EUR', 'Europe/Athens', 'ACTIVE', true, 1, now())
            ON CONFLICT (legal_entity_id) DO NOTHING
            """, Ct);
        await using var other = new ApiHostFactory(database.AppConnectionString, settings: new Dictionary<string, string?> { ["Stamp:LegalEntity"] = "GR-OTHER" });
        using var client = other.CreateClient();

        using var list = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/uw/v1/referrals?queue=MINE", UriKind.Relative));
        list.Headers.Add(TestAuthHandler.RolesHeader, Seniors);
        list.Headers.Add(TestAuthHandler.UserHeader, Outsider);
        var page = JsonNode.Parse(await (await client.SendAsync(list, Ct)).Content.ReadAsStringAsync(Ct))!;
        InQueue(page, jobId).ShouldBeFalse();
        page["counts"]!["mine"]!.GetValue<int>().ShouldBe(0);
        page["counts"]!["open"]!.GetValue<int>().ShouldBe(0);

        using var get = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/uw/v1/referrals/{jobId}", UriKind.Relative));
        get.Headers.Add(TestAuthHandler.RolesHeader, Seniors);
        get.Headers.Add(TestAuthHandler.UserHeader, Outsider);
        (await client.SendAsync(get, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
