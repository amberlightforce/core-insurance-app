using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Underwriting.Domain;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Rating.RatingTestSupport;

namespace CoreIns.IntegrationTests.Underwriting;

/// <summary>
/// Rule-set authoring (BO-UW-01: uw.RuleSet.create, draft, edit, validate, test, simulate, export, import; REQ-UW-030..040) on a real
/// PostgreSQL 17 through the real Host. The built-in UW-MOTOR-GR-B 1.0 is the source rule set; authored versions stay Drafts.
/// </summary>
public sealed class RuleSetAuthoringTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Manager = "Staff.UnderwritingManager";
    private const string Bind = "UW-MOTOR-GR-B";

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

    private Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(string path, object body, string roles = Manager) =>
        SendAsync(_client, HttpMethod.Post, "/api/uw/v1/rule-sets" + path, body, roles);

    /// <summary>The first evaluation publishes the built-in rule sets; returns once UW-MOTOR-GR-B 1.0 is stored.</summary>
    private async Task SeedAsync()
    {
        var (response, body) = await SendAsync(_client, HttpMethod.Post, "/api/uw/v1/rules/evaluate", new
        {
            jobRef = Guid.CreateVersion7(), checkpoint = "PRE_BIND", snapshotRef = "seed", productCode = MotorProduct, effectiveDate = "2026-11-01", riskSnapshot = RiskTree(),
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
    }

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

    private static object Patch(string op, object? rule = null, object? testCase = null, string? ruleId = null, string? column = null, string? value = null) =>
        new { op, rule, testCase, ruleId, column, value };

    private static HashSet<string> Codes(JsonNode? validation) =>
        [.. validation!["issues"]!.AsArray().Select(i => i!["code"]!.GetValue<string>())];

    // REQ-UW-030, -036, -038, -040: a Draft is built cell by cell; an uncovered rule is reported (and refused at the activation gate).
    [Fact]
    public async Task REQ_UW_038_a_draft_is_authored_and_an_uncovered_rule_is_refused()
    {
        var (created, body) = await PostAsync(string.Empty, new { ruleSetCode = "UW-TEST-A", productCode = MotorProduct, checkpoint = "PRE_BIND", effectiveFrom = "2027-01-01" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        body.Text("version.status").ShouldBe("Draft");
        body.Text("version.version").ShouldBe("1.0");
        body!["validation"]!["valid"]!.GetValue<bool>().ShouldBeFalse(); // no rules yet

        var (edited, editBody) = await PostAsync("/edit", new
        {
            ruleSetCode = "UW-TEST-A", version = "1.0",
            patches = new[] { Patch("ADD_RULE", rule: ClaimsRule()), Patch("ADD_TEST_CASE", testCase: Case("three-claims", 3, "REFER-CLAIMS")), Patch("ADD_TEST_CASE", testCase: Case("no-claims", 0)) },
        });
        edited.StatusCode.ShouldBe(HttpStatusCode.OK, editBody?.ToJsonString());
        editBody!["validation"]!["valid"]!.GetValue<bool>().ShouldBeTrue(editBody.ToJsonString());

        // A second rule without a test case: reported as untested, and the activation gate (CompiledRuleSet.Compile) refuses it.
        var (second, secondBody) = await PostAsync("/edit", new
        {
            ruleSetCode = "UW-TEST-A", version = "1.0", patches = new[] { Patch("ADD_RULE", rule: ClaimsRule("REFER-CLAIMS-2")) },
        });
        second.StatusCode.ShouldBe(HttpStatusCode.OK, secondBody?.ToJsonString());
        var validation = secondBody!["validation"];
        validation!["valid"]!.GetValue<bool>().ShouldBeFalse();
        Codes(validation).ShouldContain("UW-ERR-RULE-UNTESTED");
        validation["uncoveredRuleIds"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["REFER-CLAIMS-2"]);

        var (validate, validateBody) = await PostAsync("/validate", new { ruleSetCode = "UW-TEST-A", version = "1.0" });
        validate.StatusCode.ShouldBe(HttpStatusCode.OK, validateBody?.ToJsonString());
        Codes(validateBody).ShouldContain("UW-ERR-RULE-UNTESTED");
        var dto = RuleSetJson.Deserialize(secondBody!["version"]!["definition"]!.ToJsonString());
        Should.Throw<CoreIns.Platform.Errors.DomainException>(() => CompiledRuleSet.Compile(dto));

        // Fix a cell: the covered rule keeps working, then test reports every case passing.
        var (fixedCell, fixedBody) = await PostAsync("/edit", new
        {
            ruleSetCode = "UW-TEST-A", version = "1.0",
            patches = new[] { Patch("REMOVE_RULE", ruleId: "REFER-CLAIMS-2"), Patch("SET_CELL", ruleId: "REFER-CLAIMS", column: "claimsIn", value: ">= 4") },
        });
        fixedCell.StatusCode.ShouldBe(HttpStatusCode.OK, fixedBody?.ToJsonString());
        Codes(fixedBody!["validation"]).ShouldContain("UW-ERR-VALIDATION"); // the three-claims case no longer hits
        var (test, testBody) = await PostAsync("/test", new { ruleSetCode = "UW-TEST-A", version = "1.0" });
        test.StatusCode.ShouldBe(HttpStatusCode.OK);
        testBody!["testResults"]!.AsArray().Count(r => !r!["passed"]!.GetValue<bool>()).ShouldBe(1);
    }

    // REQ-UW-036: a missing translation is a finding; REQ-UW-032: only a Draft can be edited.
    [Fact]
    public async Task REQ_UW_032_a_missing_translation_is_reported_and_an_active_version_cannot_be_edited()
    {
        await SeedAsync();
        var (response, body) = await PostAsync("/edit", new { ruleSetCode = Bind, version = "1.0", patches = new[] { Patch("SET_CELL", ruleId: "REFER-HIGH-VALUE", column: "messageEl", value: "\"\"") } });
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, body?.ToJsonString());

        var (draft, draftBody) = await PostAsync("/draft", new { ruleSetCode = Bind, fromVersion = "1.0" });
        draft.StatusCode.ShouldBe(HttpStatusCode.OK, draftBody?.ToJsonString());
        draftBody.Text("version.status").ShouldBe("Draft");
        var version = draftBody.Text("version.version");
        draftBody!["validation"]!["valid"]!.GetValue<bool>().ShouldBeTrue(draftBody.ToJsonString());

        var (edited, editBody) = await PostAsync("/edit", new { ruleSetCode = Bind, version, patches = new[] { Patch("SET_CELL", ruleId: "REFER-HIGH-VALUE", column: "messageEl", value: "\"\"") } });
        edited.StatusCode.ShouldBe(HttpStatusCode.OK, editBody?.ToJsonString());
        Codes(editBody!["validation"]).ShouldContain("UW-ERR-TRANSLATION-MISSING");

        var (bad, badBody) = await PostAsync("/edit", new { ruleSetCode = Bind, version, patches = new[] { Patch("SET_CELL", ruleId: "NOPE", column: "claimsIn", value: "> 1") } });
        bad.IsSuccessStatusCode.ShouldBeFalse(badBody?.ToJsonString());
    }

    // The simulation of a draft against stored evaluations shows the new referral hits and writes nothing.
    [Fact]
    public async Task REQ_UW_040_simulate_shows_new_referral_hits_without_writing()
    {
        await SeedAsync();
        var job = Guid.CreateVersion7();
        var evaluation = await UwTestSupport.EvaluateInProcessAsync(_factory.Services, new
        {
            jobRef = job, checkpoint = "PRE_BIND", snapshotRef = $"snapshot-{job:N}", productCode = MotorProduct, effectiveDate = "2026-11-01", riskSnapshot = RiskTree(claims: 3),
        });
        evaluation.Text("outcome").ShouldBe("ACCEPT");
        var evaluationId = evaluation.Text("evaluationId");

        var (draft, draftBody) = await PostAsync("/draft", new { ruleSetCode = Bind, fromVersion = "1.0" });
        draft.StatusCode.ShouldBe(HttpStatusCode.OK, draftBody?.ToJsonString());
        var version = draftBody.Text("version.version");
        var (edited, editBody) = await PostAsync("/edit", new
        {
            ruleSetCode = Bind, version,
            patches = new[] { Patch("ADD_RULE", rule: ClaimsRule()), Patch("ADD_TEST_CASE", testCase: Case("three-claims", 3, "REFER-CLAIMS")) },
        });
        edited.StatusCode.ShouldBe(HttpStatusCode.OK, editBody?.ToJsonString());
        editBody!["validation"]!["valid"]!.GetValue<bool>().ShouldBeTrue(editBody.ToJsonString());

        var before = await ScalarAsync("SELECT count(*) FROM uw.evaluation") ;
        var (simulate, simBody) = await PostAsync("/simulate", new { ruleSetCode = Bind, version });
        simulate.StatusCode.ShouldBe(HttpStatusCode.OK, simBody?.ToJsonString());
        var item = simBody!["items"]!.AsArray().Single(i => i!["evaluationId"]!.GetValue<string>() == evaluationId)!;
        item["addedRuleIds"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["REFER-CLAIMS"]);
        item["recordedOutcome"]!.GetValue<string>().ShouldBe("ACCEPT");
        item["simulatedOutcome"]!.GetValue<string>().ShouldBe("REFER");
        simBody["newReferrals"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(1);
        (await ScalarAsync("SELECT count(*) FROM uw.evaluation")).ShouldBe(before);
    }

    // REQ-UW-032: export and import round trip; the definition hash is stable, a tampered document is refused.
    [Fact]
    public async Task REQ_UW_032_export_import_round_trip_keeps_the_hash()
    {
        await SeedAsync();
        var (export, exportBody) = await PostAsync("/export", new { ruleSetCode = Bind, version = "1.0" });
        export.StatusCode.ShouldBe(HttpStatusCode.OK, exportBody?.ToJsonString());
        var document = exportBody!["document"]!;
        document.Text("format").ShouldBe("coreins.uw.rule-set/1");
        var hash = document.Text("definitionHash");

        var (import, importBody) = await PostAsync("/import", new { document = JsonNode.Parse(document.ToJsonString()) });
        import.StatusCode.ShouldBe(HttpStatusCode.OK, importBody?.ToJsonString());
        importBody.Text("version.status").ShouldBe("Draft");
        importBody.Text("version.definitionHash").ShouldBe(hash);
        importBody!["validation"]!["valid"]!.GetValue<bool>().ShouldBeTrue(importBody.ToJsonString());
        var imported = importBody.Text("version.version");
        imported.ShouldNotBe("1.0");

        var (again, againBody) = await PostAsync("/export", new { ruleSetCode = Bind, version = imported });
        again.StatusCode.ShouldBe(HttpStatusCode.OK, againBody?.ToJsonString());
        againBody.Text("document.definitionHash").ShouldBe(hash);

        var tampered = JsonNode.Parse(document.ToJsonString())!;
        tampered["definition"]!["rules"]![0]!["id"] = "CHANGED";
        var (refused, refusedBody) = await PostAsync("/import", new { document = tampered });
        refused.IsSuccessStatusCode.ShouldBeFalse(refusedBody?.ToJsonString());
    }

    // The authoring operations are for underwriting managers and administrators only.
    [Fact]
    public async Task Authoring_needs_the_manager_role()
    {
        var (response, _) = await PostAsync(string.Empty, new { ruleSetCode = "UW-TEST-DENY", productCode = MotorProduct, checkpoint = "PRE_BIND" }, roles: "Staff.Underwriter");
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var dataSource = Npgsql.NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
