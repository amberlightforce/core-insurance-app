using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Work;

/// <summary>
/// BO-WRK-02: rule based assignment at create (round robin by open count, role / skill filters), <c>wrk.Assignment.simulate</c>,
/// user validation on explicit assignment, and the <c>ActivityAssigned</c> event.
/// </summary>
public sealed class AssignmentTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private const string Manager = "Staff.UnderwritingManager";
    private const string Psr = "WRK-POL-NONPAY-PSR-EXCEPTION";
    private const string PsrHold = "WRK-BIL-NONPAY-PSR-HOLD";
    private const string Referral = "WRK-UW-REFERRAL-INFORMATION";

    private readonly ApiHostFactory _factory = new(database.AppConnectionString);
    private HttpClient? _client;
    private Guid? _legalEntity;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client => _client ??= _factory.CreateClient();

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Without_a_rule_an_activity_stays_in_its_queue_and_simulate_says_so()
    {
        var created = await CreateAsync(Referral, "UW");
        created.Body.Text("assignment.assigneeType").ShouldBe("QUEUE");

        var simulated = await SendAsync(HttpMethod.Post, "/api/wrk/v1/assignments/simulate", new { draftActivity = new { patternCode = Referral } }, Manager, "mia");
        simulated.Response.StatusCode.ShouldBe(HttpStatusCode.OK, simulated.Body?.ToJsonString());
        simulated.Body.Text("target.type").ShouldBe("QUEUE");
        simulated.Body.Text("rulePath.0.outcome").ShouldBe("NO_RULE");
    }

    [Fact]
    public async Task Rules_round_robin_by_open_count_skip_ineligible_users_and_publish_ActivityAssigned()
    {
        await EnsureLegalEntityAsync();
        await ProfileAsync("rr-alice", "Staff.Underwriter", skills: "rr");
        await ProfileAsync("rr-bob", "Staff.Underwriter", skills: "rr");
        await ProfileAsync("rr-carol", "Staff.Billing", skills: "rr"); // cannot see the PSR queue
        await ProfileAsync("rr-dan", "Staff.Underwriter", active: false, skills: "rr");
        await RuleAsync("PSR-RR", "PSR", priority: 10, skill: "rr", pattern: Psr);

        var simulated = await SendAsync(HttpMethod.Post, "/api/wrk/v1/assignments/simulate", new { draftActivity = new { patternCode = Psr } }, Admin);
        simulated.Response.StatusCode.ShouldBe(HttpStatusCode.OK, simulated.Body?.ToJsonString());
        simulated.Body.Text("target.id").ShouldBe("rr-alice");
        simulated.Body.Text("rulePath.0.rule").ShouldBe("PSR-RR");
        var rejected = simulated.Body!["rejectedCandidates"]!.AsArray().Select(r => r!.Text("userId") + "=" + r.Text("reason")).ToList();
        rejected.ShouldContain("rr-carol=PSR-RR:CANNOT_SEE_QUEUE");
        rejected.ShouldContain("rr-dan=PSR-RR:INACTIVE");

        // Simulate changed nothing: three creates alternate by open count.
        var assignees = new List<string>();
        string? lastId = null;
        for (var i = 0; i < 3; i++)
        {
            var created = await CreateAsync(Psr, "POL");
            created.Body.Text("assignment.assigneeType").ShouldBe("USER");
            assignees.Add(created.Body.Text("assignment.assigneeId"));
            lastId = created.Body.Text("activityId");
        }

        assignees.ShouldBe(["rr-alice", "rr-bob", "rr-alice"]);
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync(dataSource, $"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{lastId}' AND event_type = 'ActivityAssigned'")).ShouldBe(1);
        var payload = await ScalarTextAsync(dataSource, $"SELECT payload::text FROM plt.outbox_message WHERE aggregate_id = '{lastId}' AND event_type = 'ActivityAssigned'");
        payload.ShouldContain("\"method\": \"RULE\"");
        payload.ShouldContain("\"ruleRef\": \"PSR-RR\"");
    }

    [Fact]
    public async Task A_skill_rule_runs_first_and_the_next_rule_covers_when_nobody_has_the_skill()
    {
        await EnsureLegalEntityAsync();
        await ProfileAsync("sk-eve", Manager, skills: "motor");
        await ProfileAsync("sk-fay", "Staff.Underwriter");
        await RuleAsync("PSR-MOTOR", "PSR", priority: 1, skill: "motor", pattern: PsrHold);
        await RuleAsync("PSR-ANY", "PSR", priority: 2, pattern: PsrHold);

        var first = await SendAsync(HttpMethod.Post, "/api/wrk/v1/assignments/simulate", new { draftActivity = new { patternCode = PsrHold } }, Admin);
        first.Body.Text("target.id").ShouldBe("sk-eve");
        first.Body.Text("rulePath.0.rule").ShouldBe("PSR-MOTOR");

        await SqlAsync("UPDATE wrk.assignee_profile SET skills = '' WHERE user_id = 'sk-eve'");
        var second = await SendAsync(HttpMethod.Post, "/api/wrk/v1/assignments/simulate", new { draftActivity = new { patternCode = PsrHold } }, Admin);
        second.Body.Text("rulePath.0.outcome").ShouldBe("NO_CANDIDATE");
        second.Body.Text("rulePath.1.rule").ShouldBe("PSR-ANY");
        second.Body.Text("target.type").ShouldBe("USER");
    }

    [Fact]
    public async Task Explicit_assignment_needs_an_existing_active_user_who_can_see_the_queue()
    {
        await EnsureLegalEntityAsync();
        await ProfileAsync("ex-gus", "Staff.Underwriter");
        await ProfileAsync("ex-hal", "Staff.Underwriter", active: false);
        await ProfileAsync("ex-ian", "Staff.Billing");
        var id = (await CreateAsync(Psr, "POL")).Body.Text("activityId");

        foreach (var user in new[] { "ex-nobody", "ex-hal", "ex-ian" })
        {
            var refused = await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities/assign", new { id, target = new { type = "USER", id = user } }, Manager, "mia");
            refused.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, user);
            refused.Body.Text("code").ShouldBe("WRK-ERR-TARGET-INELIGIBLE");
        }

        var ok = await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities/assign", new { id, target = new { type = "USER", id = "ex-gus" } }, Manager, "mia");
        ok.Response.StatusCode.ShouldBe(HttpStatusCode.OK, ok.Body?.ToJsonString());
        ok.Body.Text("assignment.assignee.id").ShouldBe("ex-gus");

        // Only a manager assigns to someone else.
        (await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities/assign", new { id, target = new { type = "USER", id = "ex-gus" } }, "Staff.Underwriter", "bob")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync(dataSource, $"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{id}' AND event_type = 'ActivityAssigned' AND payload->>'method' = 'MANUAL'")).ShouldBe(1);
    }

    [Fact]
    public async Task Assign_by_rules_routes_an_open_activity_and_is_for_managers_only()
    {
        await EnsureLegalEntityAsync();
        await ProfileAsync("by-jo", "Staff.Underwriter", skills: "notice");
        var id = (await CreateAsync("WRK-BIL-NONPAY-NOTICE-RETURNED", "BIL")).Body.Text("activityId");
        var noRule = await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities/assign", new { id, rulesOrMe = "rules" }, Manager, "mia");
        noRule.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        noRule.Body.Text("code").ShouldBe("WRK-ERR-TARGET-INELIGIBLE");

        await RuleAsync("PSR-NOTICE", "PSR", priority: 5, skill: "notice", pattern: "WRK-BIL-NONPAY-NOTICE-RETURNED");
        (await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities/assign", new { id, rulesOrMe = "rules" }, "Staff.Underwriter", "bob")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var routed = await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities/assign", new { id, rulesOrMe = "rules" }, Manager, "mia");
        routed.Response.StatusCode.ShouldBe(HttpStatusCode.OK, routed.Body?.ToJsonString());
        routed.Body.Text("assignment.assignee.id").ShouldBe("by-jo");
    }

    [Fact]
    public async Task Simulate_refuses_an_unknown_pattern_and_a_non_manager()
    {
        var unknown = await SendAsync(HttpMethod.Post, "/api/wrk/v1/assignments/simulate", new { draftActivity = new { patternCode = "NOPE" } }, Admin);
        unknown.Response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, "/api/wrk/v1/assignments/simulate", new { draftActivity = new { patternCode = Psr } }, "Staff.Underwriter", "bob")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>The legal entity id the API stamps on rows: read from a first activity.</summary>
    private async Task EnsureLegalEntityAsync()
    {
        if (_legalEntity is not null)
        {
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        if (await ScalarAsync(dataSource, "SELECT count(*) FROM wrk.activity") == 0)
        {
            await CreateAsync(Referral, "UW", cause: "seed-le");
        }

        await using var command = dataSource.CreateCommand("SELECT legal_entity_id FROM wrk.activity LIMIT 1");
        _legalEntity = (Guid)(await command.ExecuteScalarAsync(Ct))!;
    }

    private Task ProfileAsync(string user, string roles, bool active = true, string skills = "") => SqlAsync(
        $"INSERT INTO wrk.assignee_profile (legal_entity_id, user_id, display_name, roles, skills, active) VALUES ('{_legalEntity}', '{user}', '{user}', '{roles}', '{skills}', {(active ? "true" : "false")})");

    private Task RuleAsync(string code, string queue, int priority, string? skill = null, string? pattern = null) => SqlAsync(
        "INSERT INTO wrk.assignment_rule (rule_id, legal_entity_id, code, queue_code, pattern_code, required_role, required_skill, strategy, priority, active) " +
        $"VALUES ('{Guid.NewGuid()}', '{_legalEntity}', '{code}', '{queue}', {(pattern is null ? "NULL" : $"'{pattern}'")}, NULL, {(skill is null ? "NULL" : $"'{skill}'")}, 'ROUND_ROBIN_OPEN_COUNT', {priority}, true)");

    private async Task SqlAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<long> ScalarAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarTextAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> CreateAsync(string pattern, string raising, string? cause = null) => SendAsync(HttpMethod.Post, "/api/wrk/v1/activities", new
    {
        patternCode = pattern,
        raisingModule = raising,
        causeRef = cause,
        primaryObject = new { module = raising, type = "Case", id = Guid.NewGuid().ToString() },
    }, Admin);

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body, string roles, string user = "test-user")
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.AcceptLanguage.ParseAdd("en");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }
}
