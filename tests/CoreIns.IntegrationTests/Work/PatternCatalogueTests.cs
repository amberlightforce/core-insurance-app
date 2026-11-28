using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Work.Domain;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Work;

/// <summary>
/// BO-WRK-03: the queue and pattern catalogue lives in <c>wrk.queue</c> / <c>wrk.activity_pattern</c> (seed parity with the former code constants),
/// is administered through <c>/api/wrk/v1/admin/patterns</c>, and create honours the pattern's suggested assignee, amount band and overrides.
/// </summary>
public sealed class PatternCatalogueTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private const string Path = "/api/wrk/v1/admin/patterns";

    private static readonly string[] Uw = ["UW"];
    private static readonly string[] Candidates = ["sg-ann", "sg-ben"];

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
    public async Task The_seeded_catalogue_equals_the_former_code_constants()
    {
        var listed = await SendAsync(HttpMethod.Get, Path, null, Admin);
        listed.Response.StatusCode.ShouldBe(HttpStatusCode.OK, listed.Body?.ToJsonString());

        var queues = listed.Body!["queues"]!.AsArray().ToDictionary(q => q!.Text("code"), q => q!);
        foreach (var seed in WorkCatalog.SeedQueues)
        {
            queues.ShouldContainKey(seed.Code);
            queues[seed.Code].Text("title").ShouldBe(seed.Title);
            queues[seed.Code]["roles"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(seed.Roles);
        }

        var patterns = listed.Body["patterns"]!.AsArray().ToDictionary(p => p!.Text("code"), p => p!);
        foreach (var seed in WorkCatalog.SeedPatterns)
        {
            patterns.ShouldContainKey(seed.Code);
            var row = patterns[seed.Code];
            row.Text("version").ShouldBe(seed.Version);
            row.Text("title").ShouldBe(seed.Title);
            row.Text("category").ShouldBe(seed.Category);
            row.Text("priority").ShouldBe(seed.Priority);
            row.Text("queue").ShouldBe(seed.Queue);
            row.Text("slaPolicy").ShouldBe(seed.SlaPolicy);
            row["targetMinutes"]!.GetValue<int>().ShouldBe(seed.TargetMinutes);
            row["escalationMinutes"]!.GetValue<int>().ShouldBe(seed.EscalationMinutes);
            (row["gateBlocking"]?.GetValue<bool>() ?? false).ShouldBe(seed.GateBlocking);
            row["raisingModules"]!.AsArray().Select(m => m!.GetValue<string>()).ShouldBe(seed.RaisingModules.Select(m => m.ToString()));
        }
    }

    [Fact]
    public async Task Only_the_platform_admin_reads_or_changes_the_catalogue()
    {
        (await SendAsync(HttpMethod.Get, Path, null, "Staff.UnderwritingManager", "mia")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Put, Path, new { queues = new[] { new { code = "NOPE-Q", title = "x", roles = new[] { Admin } } } }, "Staff.UnderwritingManager", "mia"))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_new_pattern_in_a_new_queue_is_creatable_versioned_on_change_and_refused_when_deactivated()
    {
        var queue = "CAT-NEW-Q";
        var code = "WRK-CAT-NEW-PATTERN";
        var put = await UpsertAsync(queue, code, target: 600);
        put.Response.StatusCode.ShouldBe(HttpStatusCode.OK, put.Body?.ToJsonString());
        Pattern(put.Body, code).Text("version").ShouldBe("1");

        var created = await CreateAsync(code, "UW");
        created.Response.StatusCode.ShouldBe(HttpStatusCode.OK, created.Body?.ToJsonString());
        created.Body.Text("assignment.assigneeType").ShouldBe("QUEUE");
        created.Body.Text("assignment.assigneeId").ShouldBe(queue);
        (await TargetMinutesAsync(created.Body)).ShouldBe(600);

        // The same content keeps the version; a change takes the next one and a new activity uses it.
        Pattern((await UpsertAsync(queue, code, target: 600)).Body, code).Text("version").ShouldBe("1");
        Pattern((await UpsertAsync(queue, code, target: 300)).Body, code).Text("version").ShouldBe("2");
        (await TargetMinutesAsync((await CreateAsync(code, "UW")).Body)).ShouldBe(300);

        // A module outside raisingModules never creates it.
        (await CreateAsync(code, "POL")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var off = await UpsertAsync(queue, code, target: 300, active: false);
        off.Response.StatusCode.ShouldBe(HttpStatusCode.OK, off.Body?.ToJsonString());
        var refused = await CreateAsync(code, "UW");
        refused.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.Body.Text("code").ShouldBe("WRK-ERR-PATTERN-NOT-APPLICABLE");
    }

    [Fact]
    public async Task The_upsert_refuses_a_pattern_in_an_unknown_queue_and_nonsense_minutes()
    {
        var unknown = await UpsertPatternOnlyAsync("CAT-NO-SUCH-Q", "WRK-CAT-BAD-QUEUE", 60, 120);
        unknown.Response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        var backwards = await UpsertPatternOnlyAsync("UW-REFERRAL", "WRK-CAT-BAD-MINUTES", 120, 60);
        backwards.Response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        var empty = await SendAsync(HttpMethod.Put, Path, new { }, Admin);
        empty.Response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Amount_band_scales_the_sla_and_an_explicit_override_beats_the_band_and_the_pattern()
    {
        var code = "WRK-CAT-BANDS";
        (await UpsertAsync("CAT-BANDS-Q", code, target: 1000, bands: new Dictionary<string, int> { ["HIGH"] = 50, ["LOW"] = 200 })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await TargetMinutesAsync((await CreateAsync(code, "UW")).Body)).ShouldBe(1000);
        (await TargetMinutesAsync((await CreateAsync(code, "UW", amountBand: "HIGH")).Body)).ShouldBe(500);
        (await TargetMinutesAsync((await CreateAsync(code, "UW", amountBand: "LOW")).Body)).ShouldBe(2000);
        (await TargetMinutesAsync((await CreateAsync(code, "UW", amountBand: "UNKNOWN")).Body)).ShouldBe(1000);
        (await TargetMinutesAsync((await CreateAsync(code, "UW", amountBand: "HIGH", overrides: new { targetMinutes = 90 })).Body)).ShouldBe(90);

        var priority = await CreateAsync(code, "UW", overrides: new { priority = "HIGH" });
        (await GetActivityAsync(priority.Body)).Text("items.0.activity.priority").ShouldBe("HIGH");

        // Fail closed: an override that is unknown or outside the range creates nothing.
        (await CreateAsync(code, "UW", overrides: new { slaPolicy = "NONE" })).Response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        (await CreateAsync(code, "UW", overrides: new { targetMinutes = 0 })).Response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Suggested_assignee_precedence_is_request_then_rules_then_the_pattern_default_then_the_queue()
    {
        await EnsureLegalEntityAsync();
        var queue = "CAT-SUGG-Q";
        var code = "WRK-CAT-SUGG";
        await ProfileAsync("sg-ann", "Staff.Underwriter");
        await ProfileAsync("sg-ben", "Staff.Underwriter");
        await ProfileAsync("sg-off", "Staff.Underwriter", active: false);
        await ProfileAsync("sg-bill", "Staff.Billing");

        // The pattern default alone: assigned to the default user.
        (await UpsertAsync(queue, code, target: 600, suggested: "sg-ann")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CreateAsync(code, "UW")).Body.Text("assignment.assigneeId").ShouldBe("sg-ann");

        // The request's suggestion beats the pattern default; an ineligible one (inactive, wrong role, unknown) falls back to the default.
        (await CreateAsync(code, "UW", suggested: "sg-ben")).Body.Text("assignment.assigneeId").ShouldBe("sg-ben");
        foreach (var bad in new[] { "sg-off", "sg-bill", "sg-nobody" })
        {
            var fallback = await CreateAsync(code, "UW", suggested: bad);
            fallback.Body.Text("assignment.assigneeId").ShouldBe("sg-ann", bad);
        }

        // An assignment rule beats the pattern default but not the request's suggestion.
        await SqlAsync("INSERT INTO wrk.assignment_rule (rule_id, legal_entity_id, code, queue_code, pattern_code, required_role, required_skill, strategy, priority, active) " +
            $"VALUES ('{Guid.NewGuid()}', '{_legalEntity}', 'CAT-SUGG-RULE', '{queue}', '{code}', NULL, NULL, 'ROUND_ROBIN_OPEN_COUNT', 1, true)");
        var ruled = (await CreateAsync(code, "UW")).Body.Text("assignment.assigneeId");
        Candidates.ShouldContain(ruled);
        (await CreateAsync(code, "UW", suggested: "sg-ben")).Body.Text("assignment.assigneeId").ShouldBe("sg-ben");
        await SqlAsync("UPDATE wrk.assignment_rule SET active = false WHERE code = 'CAT-SUGG-RULE'");

        // Without a default and without a suggestion the activity stays in the queue.
        (await UpsertAsync(queue, code, target: 600)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CreateAsync(code, "UW")).Body.Text("assignment.assigneeType").ShouldBe("QUEUE");
    }

    private static JsonNode Pattern(JsonNode? body, string code) =>
        body!["patterns"]!.AsArray().Single(p => p!.Text("code") == code)!;

    private Task<(HttpResponseMessage Response, JsonNode? Body)> UpsertAsync(
        string queue, string code, int target, bool active = true, string? suggested = null, object? bands = null) =>
        SendAsync(HttpMethod.Put, Path, new
        {
            queues = new[] { new { code = queue, title = "Catalogue test queue " + queue, roles = new[] { "Staff.Underwriter", Admin } } },
            patterns = new[]
            {
                new
                {
                    code, title = "Catalogue test " + code, category = "REVIEW", priority = "NORMAL", queue, slaPolicy = "WRK-SLA-TEST",
                    targetMinutes = target, escalationMinutes = target * 2, gateBlocking = false, raisingModules = Uw,
                    suggestedAssignee = suggested, amountBandMultipliers = bands, active,
                },
            },
        }, Admin);

    private Task<(HttpResponseMessage Response, JsonNode? Body)> UpsertPatternOnlyAsync(string queue, string code, int target, int escalation) =>
        SendAsync(HttpMethod.Put, Path, new
        {
            patterns = new[]
            {
                new
                {
                    code, title = "Catalogue test " + code, category = "REVIEW", priority = "NORMAL", queue, slaPolicy = "WRK-SLA-TEST",
                    targetMinutes = target, escalationMinutes = escalation, raisingModules = Uw,
                },
            },
        }, Admin);

    private async Task<int> TargetMinutesAsync(JsonNode? created)
    {
        var activity = (await GetActivityAsync(created)).AsObject()["items"]![0]!["activity"]!;
        var createdAt = DateTimeOffset.Parse(activity.Text("createdAt"), CultureInfo.InvariantCulture);
        var target = DateTimeOffset.Parse(activity.Text("targetAt"), CultureInfo.InvariantCulture);
        return (int)Math.Round((target - createdAt).TotalMinutes);
    }

    private async Task<JsonNode> GetActivityAsync(JsonNode? created)
    {
        var got = await SendAsync(HttpMethod.Get, "/api/wrk/v1/activities/" + created.Text("activityId"), null, Admin);
        got.Response.StatusCode.ShouldBe(HttpStatusCode.OK, got.Body?.ToJsonString());
        return got.Body!;
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> CreateAsync(
        string pattern, string raising, string? suggested = null, string? amountBand = null, object? overrides = null) =>
        SendAsync(HttpMethod.Post, "/api/wrk/v1/activities", new
        {
            patternCode = pattern,
            raisingModule = raising,
            suggestedAssignee = suggested,
            amountBand,
            overrides,
            primaryObject = new { module = raising, type = "Case", id = Guid.NewGuid().ToString() },
        }, Admin);

    private async Task EnsureLegalEntityAsync()
    {
        if (_legalEntity is not null)
        {
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using (var count = dataSource.CreateCommand("SELECT count(*) FROM wrk.activity"))
        {
            if (Convert.ToInt64(await count.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture) == 0)
            {
                (await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities", new
                {
                    patternCode = "WRK-UW-REFERRAL-INFORMATION",
                    raisingModule = "UW",
                    causeRef = "seed-le-cat",
                    primaryObject = new { module = "UW", type = "Case", id = Guid.NewGuid().ToString() },
                }, Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
            }
        }

        await using var command = dataSource.CreateCommand("SELECT legal_entity_id FROM wrk.activity LIMIT 1");
        _legalEntity = (Guid)(await command.ExecuteScalarAsync(Ct))!;
    }

    private Task ProfileAsync(string user, string roles, bool active = true) => SqlAsync(
        $"INSERT INTO wrk.assignee_profile (legal_entity_id, user_id, display_name, roles, skills, active) VALUES ('{_legalEntity}', '{user}', '{user}', '{roles}', '', {(active ? "true" : "false")})");

    private async Task SqlAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
    }

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
