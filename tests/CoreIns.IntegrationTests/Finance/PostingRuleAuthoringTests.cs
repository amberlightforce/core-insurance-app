using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace CoreIns.IntegrationTests.Finance;

/// <summary>BO-FIN-07: posting rule authoring (draft, compile, test, previewImpact, submit, decide) on a real PostgreSQL through the real Host.</summary>
public sealed class PostingRuleAuthoringTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Book = "IFRS17";
    private const string Base = "/api/fin/v1/posting-rules";
    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;

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

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body = null, string roles = "Staff.Finance", string user = "maker-1")
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    /// <summary>The rules of the version in force, as authoring rows.</summary>
    private async Task<List<JsonObject>> InForceRowsAsync(string validAt = "2026-11-01")
    {
        var rows = new List<JsonObject>();
        string? cursor = null;
        do
        {
            var (response, page) = await SendAsync(HttpMethod.Get, $"{Base}?book={Book}&validAt={validAt}&limit=200" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}"));
            response.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
            foreach (var item in page!["items"]!.AsArray())
            {
                rows.Add(new JsonObject
                {
                    ["code"] = item!["ruleCode"]!.GetValue<string>(),
                    ["sourceEvent"] = item["sourceEventType"]!.GetValue<string>(),
                    ["entryType"] = item["entryType"]!.GetValue<string>(),
                    ["sourceAccount"] = item["sourceAccount"]!.GetValue<string>(),
                    ["chargeCategory"] = item["chargeCategory"]?.GetValue<string>(),
                    ["chargeType"] = item["chargeType"]?.GetValue<string>(),
                    ["account"] = item["account"]?.GetValue<string>(),
                    ["deriveFrom"] = item["deriveFrom"]?.GetValue<string>(),
                    ["descriptionEl"] = item["description"]!["el"]!.GetValue<string>(),
                    ["descriptionEn"] = item["description"]!["en"]!.GetValue<string>(),
                });
            }

            cursor = page["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null);
        rows.Count.ShouldBeGreaterThan(10);
        return rows;
    }

    private static JsonObject Fixed(List<JsonObject> rows) => rows.First(r => r["account"] is not null && r["chargeCategory"] is null && r["chargeType"] is null);

    private static JsonArray Clone(List<JsonObject> rows) => new([.. rows.Select(r => (JsonNode)JsonNode.Parse(r.ToJsonString())!)]);

    private static string OtherAccount(JsonObject rule) => rule["account"]!.GetValue<string>() == "GL-1110" ? "GL-1210" : "GL-1110";

    private async Task<long> CountAsync(string table)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand($"SELECT count(*) FROM {table}");
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(string op, JsonNode? rows, string? effective = "2090-01-01", object? samples = null, Guid? draftId = null, string user = "maker-1") =>
        SendAsync(HttpMethod.Post, $"{Base}/{op}", new JsonObject
        {
            ["ruleSet"] = new JsonObject { ["draftId"] = draftId?.ToString(), ["book"] = Book, ["effectiveFrom"] = effective },
            ["ruleRows"] = rows is null ? null : JsonNode.Parse(rows.ToJsonString()),
            ["sampleEvents"] = samples is null ? null : JsonSerializer.SerializeToNode(samples),
        }, user: user);

    [Fact]
    public async Task The_shipped_set_compiles_and_an_invalid_rule_fails_compile_and_cannot_be_submitted()
    {
        var rows = await InForceRowsAsync();
        var (ok, good) = await PostAsync("compile", Clone(rows));
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, good?.ToJsonString());
        good!["compileResults"]!["ok"]!.GetValue<bool>().ShouldBeTrue(good.ToJsonString());

        var broken = Clone(rows);
        broken[0]!["account"] = "GL-9999";
        broken[0]!["deriveFrom"] = null;
        var (bad, body) = await PostAsync("compile", broken);
        bad.StatusCode.ShouldBe(HttpStatusCode.OK);
        body!["compileResults"]!["ok"]!.GetValue<bool>().ShouldBeFalse();
        body["compileResults"]!["errors"]!.ToJsonString().ShouldContain("GL-9999");

        // Two rules that tie for one line are a compile error too.
        var tie = Clone(rows);
        var twin = JsonNode.Parse(tie[0]!.ToJsonString())!;
        twin["code"] = "TWIN";
        tie.Add(twin);
        var (_, tied) = await PostAsync("compile", tie);
        tied!["compileResults"]!["ok"]!.GetValue<bool>().ShouldBeFalse();

        // An invalid draft can be stored, but not submitted.
        var (drafted, draft) = await PostAsync("draft", broken);
        drafted.StatusCode.ShouldBe(HttpStatusCode.OK, draft?.ToJsonString());
        var id = draft!["draft"]!["draftId"]!.GetValue<string>();
        var (submit, problem) = await SendAsync(HttpMethod.Post, $"{Base}/submit", new { draftId = id });
        submit.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem!.ToJsonString().ShouldContain("FIN-ERR-COMPILE");
    }

    [Fact]
    public async Task Sample_events_are_tested_and_a_failing_sample_blocks_submission()
    {
        var rows = await InForceRowsAsync();
        var rule = Fixed(rows);
        var sample = (string expectAccount) => new[]
        {
            new
            {
                name = "fixed-rule", sourceEvent = rule["sourceEvent"]!.GetValue<string>(), entryType = rule["entryType"]!.GetValue<string>(),
                sourceAccount = rule["sourceAccount"]!.GetValue<string>(), expectRule = rule["code"]!.GetValue<string>(), expectAccount,
            },
        };

        var (_, passing) = await PostAsync("test", Clone(rows), samples: sample(rule["account"]!.GetValue<string>()));
        passing!["goldenResults"]!["passed"]!.GetValue<int>().ShouldBe(1, passing.ToJsonString());

        var (_, failing) = await PostAsync("test", Clone(rows), samples: sample("GL-0000"));
        failing!["goldenResults"]!["failed"]!.GetValue<int>().ShouldBe(1);

        var (_, draft) = await PostAsync("draft", Clone(rows), samples: sample("GL-0000"));
        var id = draft!["draft"]!["draftId"]!.GetValue<string>();
        var (submit, problem) = await SendAsync(HttpMethod.Post, $"{Base}/submit", new { draftId = id });
        submit.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem!.ToJsonString().ShouldContain("FIN-ERR-GOLDEN");
    }

    [Fact]
    public async Task Preview_impact_replays_posted_lines_and_writes_nothing()
    {
        var rows = await InForceRowsAsync();
        var rule = Fixed(rows);
        var original = rule["account"]!.GetValue<string>();
        var code = rule["code"]!.GetValue<string>();
        await PostJournalAsync(code, original);

        var changed = Clone(rows);
        var target = changed.Single(r => r!["code"]!.GetValue<string>() == code)!;
        target["account"] = OtherAccount(rule);

        string[] tables = ["fin.journal_entry", "fin.journal_line", "fin.posting_rule_set", "fin.posting_rule", "fin.posting_rule_draft", "fin.business_event"];
        var before = new List<long>();
        foreach (var table in tables)
        {
            before.Add(await CountAsync(table));
        }

        var (response, body) = await PostAsync("preview-impact", changed);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var preview = body!["preview"]!;
        preview["journals"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(1);
        preview["changed"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(1, body.ToJsonString());
        preview["differences"]!.AsArray().Any(d => d!["newAccount"]!.GetValue<string>() == OtherAccount(rule) && d["oldAccount"]!.GetValue<string>() == original).ShouldBeTrue();

        // The unchanged set replays with no difference.
        var (_, same) = await PostAsync("preview-impact", Clone(rows));
        same!["preview"]!["changed"]!.GetValue<int>().ShouldBe(0, same.ToJsonString());

        for (var i = 0; i < tables.Length; i++)
        {
            (await CountAsync(tables[i])).ShouldBe(before[i], tables[i]);
        }
    }

    [Fact]
    public async Task An_approved_set_activates_only_from_its_effective_date_and_never_rewrites_the_shipped_set()
    {
        var rows = await InForceRowsAsync();
        var rule = Fixed(rows);
        var code = rule["code"]!.GetValue<string>();
        var shipped = await VersionAsync();
        var shippedRules = await CountAsync($"fin.posting_rule r JOIN fin.posting_rule_set s ON s.rule_set_id = r.rule_set_id WHERE s.version_no = {shipped} AND s.book = '{Book}'");

        var revised = Clone(rows);
        revised.Single(r => r!["code"]!.GetValue<string>() == code)!["account"] = OtherAccount(rule);
        var (drafted, draft) = await PostAsync("draft", revised, effective: "2090-01-01");
        drafted.StatusCode.ShouldBe(HttpStatusCode.OK, draft?.ToJsonString());
        var id = draft!["draft"]!["draftId"]!.GetValue<string>();

        // Drafting changes nothing that posts.
        (await VersionAsync()).ShouldBe(shipped);

        var (submitted, sub) = await SendAsync(HttpMethod.Post, $"{Base}/submit", new { draftId = id });
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, sub?.ToJsonString());
        sub!["draft"]!["status"]!.GetValue<string>().ShouldBe("SUBMITTED");
        (await VersionAsync()).ShouldBe(shipped);

        // The maker cannot decide; a checker without the grant cannot either; the admin can.
        var (self, sod) = await SendAsync(HttpMethod.Post, $"{Base}/decide", new { draftId = id, decision = "APPROVE" });
        self.StatusCode.ShouldBe(HttpStatusCode.Forbidden, sod?.ToJsonString());
        var (low, denied) = await SendAsync(HttpMethod.Post, $"{Base}/decide", new { draftId = id, decision = "APPROVE" }, user: "checker-1");
        low.StatusCode.ShouldBe(HttpStatusCode.Forbidden, denied?.ToJsonString());
        (await VersionAsync()).ShouldBe(shipped);

        var (approved, body) = await SendAsync(HttpMethod.Post, $"{Base}/decide", new { draftId = id, decision = "APPROVE" }, roles: "Platform.Admin", user: "admin-1");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["draft"]!["status"]!.GetValue<string>().ShouldBe("APPROVED");
        var version = body["draft"]!["activatedVersion"]!.GetValue<int>();
        version.ShouldBe(shipped + 1);

        // A repeated decision is a replay.
        var (again, replay) = await SendAsync(HttpMethod.Post, $"{Base}/decide", new { draftId = id, decision = "APPROVE" }, roles: "Platform.Admin", user: "admin-2");
        again.StatusCode.ShouldBe(HttpStatusCode.OK, replay?.ToJsonString());
        (await VersionAsync()).ShouldBe(version);

        // Before the effective date the shipped set governs; from it, the new version.
        var before = await ListedAsync("2089-12-31", code);
        before["ruleSetVersion"]!.GetValue<int>().ShouldBe(shipped);
        before["account"]!.GetValue<string>().ShouldBe(rule["account"]!.GetValue<string>());
        var after = await ListedAsync("2090-01-01", code);
        after["ruleSetVersion"]!.GetValue<int>().ShouldBe(version);
        after["account"]!.GetValue<string>().ShouldBe(OtherAccount(rule));
        (await ListedAsync("2026-11-01", code))["ruleSetVersion"]!.GetValue<int>().ShouldBe(shipped);

        // The shipped version is untouched.
        (await CountAsync($"fin.posting_rule r JOIN fin.posting_rule_set s ON s.rule_set_id = r.rule_set_id WHERE s.version_no = {shipped} AND s.book = '{Book}'")).ShouldBe(shippedRules);
    }

    [Fact]
    public async Task A_rejected_draft_activates_nothing_and_a_past_effective_date_cannot_be_submitted()
    {
        var rows = await InForceRowsAsync();
        var shipped = await VersionAsync();

        var (_, past) = await PostAsync("draft", Clone(rows), effective: "2020-01-01");
        var pastId = past!["draft"]!["draftId"]!.GetValue<string>();
        (await SendAsync(HttpMethod.Post, $"{Base}/submit", new { draftId = pastId })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var (_, draft) = await PostAsync("draft", Clone(rows), effective: "2091-01-01");
        var id = draft!["draft"]!["draftId"]!.GetValue<string>();
        (await SendAsync(HttpMethod.Post, $"{Base}/submit", new { draftId = id }, user: "someone-else")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, $"{Base}/submit", new { draftId = id })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, $"{Base}/decide", new { draftId = id, decision = "REJECT" }, roles: "Platform.Admin", user: "admin-1")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (rejected, body) = await SendAsync(HttpMethod.Post, $"{Base}/decide", new { draftId = id, decision = "REJECT", comment = "Not now" }, roles: "Platform.Admin", user: "admin-1");
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["draft"]!["status"]!.GetValue<string>().ShouldBe("REJECTED");
        (await VersionAsync()).ShouldBe(shipped);
        (await SendAsync(HttpMethod.Post, $"{Base}/decide", new { draftId = id, decision = "APPROVE" }, roles: "Platform.Admin", user: "admin-2")).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_role_without_the_permission_is_refused()
    {
        var rows = await InForceRowsAsync();
        (await PostAsync("compile", Clone(rows), user: "u")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (denied, _) = await SendAsync(HttpMethod.Post, $"{Base}/draft", new { ruleRows = Clone(rows) }, roles: "Staff.Underwriter");
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<int> VersionAsync()
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand($"SELECT max(version_no) FROM fin.posting_rule_set WHERE book = '{Book}'");
        return (int)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<JsonNode> ListedAsync(string validAt, string code)
    {
        string? cursor = null;
        do
        {
            var (_, page) = await SendAsync(HttpMethod.Get, $"{Base}?book={Book}&validAt={validAt}&limit=200" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}"));
            var hit = page!["items"]!.AsArray().FirstOrDefault(i => i!["ruleCode"]!.GetValue<string>() == code);
            if (hit is not null)
            {
                return hit;
            }

            cursor = page["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null);
        throw new InvalidOperationException($"Rule {code} not listed at {validAt}.");
    }

    /// <summary>One balanced journal posted by the given rule (inserted as the superuser, like the reconciliation tests).</summary>
    private async Task PostJournalAsync(string ruleCode, string account)
    {
        const string day = "2026-11-10";
        await database.ExecuteAsSuperuserAsync(
            $"INSERT INTO fin.financial_period (period_id, legal_entity_id, period_code, status, created_at) VALUES ('{Guid.CreateVersion7()}', '{ApiHostFactory.LegalEntityId}', '2026-11', 'OPEN', now()) ON CONFLICT (legal_entity_id, period_code) DO NOTHING",
            TestContext.Current.CancellationToken);
        await using var super = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var lookup = super.CreateCommand(
            $"SELECT (SELECT rule_set_id FROM fin.posting_rule_set WHERE book = '{Book}' AND version_no = {await VersionAsync()}), (SELECT period_id FROM fin.financial_period WHERE legal_entity_id = '{ApiHostFactory.LegalEntityId}' AND period_code = '2026-11')");
        await using var reader = await lookup.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);
        var ruleSet = reader.GetGuid(0);
        var period = reader.GetGuid(1);
        await reader.DisposeAsync();
        var journal = Guid.CreateVersion7();
        var number = "JNL2026" + Random.Shared.NextInt64(9_000_000_000, 9_999_999_999).ToString(CultureInfo.InvariantCulture);
        string Line(int no, string acct, string side) =>
            $"INSERT INTO fin.journal_line (line_id, journal_id, line_no, legal_entity_id, book, account_code, side, amount, currency, amount_functional, functional_currency, rule_code, business_date) VALUES (gen_random_uuid(), '{journal}', {no}, '{ApiHostFactory.LegalEntityId}', '{Book}', '{acct}', '{side}', 10, 'EUR', 10, 'EUR', '{ruleCode}', DATE '{day}');";
        await database.ExecuteAsSuperuserAsync(
            $"""
            BEGIN;
            INSERT INTO fin.journal_entry (journal_id, journal_number, legal_entity_id, legal_entity_code, jurisdiction, book, accounting_date, business_date,
                period_id, source_type, source_module, source_event_type, source_event_ids, source_ref, rule_set_id, rule_set_version, rule_codes,
                functional_currency, correlation_id, posted_at, posted_by)
            VALUES ('{journal}', '{number}', '{ApiHostFactory.LegalEntityId}', 'GR-TEST', 'GR', '{Book}', DATE '{day}', DATE '{day}',
                '{period}', 'EVENT', 'BIL', 'BillingEntryPosted', ARRAY[]::uuid[], 'authoring-test', '{ruleSet}', 1, ARRAY['{ruleCode}'], 'EUR', 'x', now(), 'test');
            {Line(1, account, "DEBIT")}
            {Line(2, account, "CREDIT")}
            COMMIT;
            """, TestContext.Current.CancellationToken);
    }
}
