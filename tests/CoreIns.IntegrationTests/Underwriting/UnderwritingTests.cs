using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Underwriting.Contracts;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Domain;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Rating.RatingTestSupport;

namespace CoreIns.IntegrationTests.Underwriting;

/// <summary>
/// SL-RAT-UW underwriting: the evaluation runtime on the shared rule engine (illustrative rule set), the decision record
/// and the minimal issue lifecycle, on a real PostgreSQL 17 through the real Host (W3-UW-01/02 subset).
/// </summary>
public sealed class UnderwritingTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
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

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static object EvaluateBody(Guid job, JsonObject? risk = null, string checkpoint = "PRE_BIND", string product = MotorProduct) => new
    {
        jobRef = job,
        checkpoint,
        snapshotRef = $"snapshot-{job:N}",
        productCode = product,
        effectiveDate = "2026-11-01",
        riskSnapshot = risk ?? RiskTree(),
    };

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> EvaluateAsync(object body, Guid? key = null) =>
        await SendAsync(_client, HttpMethod.Post, "/api/uw/v1/rules/evaluate", body, key: key);

    private async Task<bool> BlockedAsync(Guid job, string point)
    {
        var (response, body) = await SendAsync(_client, HttpMethod.Get, $"/api/uw/v1/issues/blocking-status?jobRef={job}&blockingPoint={point}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["blocked"]!.GetValue<bool>();
    }

    // REQ-UW-001, -042, -060: evaluate returns accept with the rule set that decided and records the decision.
    [Fact]
    public async Task REQ_UW_001_a_typical_risk_is_accepted_straight_through_and_the_decision_is_recorded()
    {
        var job = Guid.CreateVersion7();

        var (response, body) = await EvaluateAsync(EvaluateBody(job));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("outcome").ShouldBe("ACCEPT");
        body.Text("lane").ShouldBe("STRAIGHT_THROUGH");
        body!["issues"]!.AsArray().Count.ShouldBe(0);
        body.Text("ruleSetCode").ShouldBe("UW-MOTOR-GR-B");
        body.Text("ruleSetVersion").ShouldBe("1.0");
        var hash = body.Text("ruleSetHash");
        hash.Length.ShouldBe(64);
        (await ScalarAsync<string>($"SELECT outcome FROM uw.evaluation WHERE job_id = '{job}'")).ShouldBe("ACCEPT");
        (await ScalarAsync<string>($"SELECT rule_set_hash FROM uw.evaluation WHERE job_id = '{job}'")).ShouldBe(hash);
        (await ScalarAsync<string>($"SELECT data_status FROM uw.rule_set_version WHERE content_hash = '{hash}'")).ShouldBe("ILLUSTRATIVE_TEST_DATA");
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = 'uw.Rules.evaluate' AND object_id = '{job}' AND outcome = 'Succeeded'")).ShouldBe(1);
        (await BlockedAsync(job, "PRE_BIND")).ShouldBeFalse();
        (await BlockedAsync(job, "PRE_ISSUE")).ShouldBeFalse();
    }

    // REQ-UW-060, -061: a referral rule hits, the issue blocks bind (not the quote), and the reason is explained in both languages.
    [Fact]
    public async Task REQ_UW_060_a_young_driver_is_referred_the_issue_blocks_bind_but_not_the_quote()
    {
        var job = Guid.CreateVersion7();

        var (response, body) = await EvaluateAsync(EvaluateBody(job, RiskTree(birthDate: "2007-06-15")));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("outcome").ShouldBe("REFER");
        body.Text("lane").ShouldBe("ASSISTED");
        body.Text("issues.0.issueType").ShouldBe("DRIVER_AGE_REFERRAL");
        body.Text("issues.0.blockingPoint").ShouldBe("PRE_BIND");
        body.Text("issues.0.issueKey").ShouldBe("DRIVER_AGE_REFERRAL:veh-1");
        body.Text("issues.0.approvalStatus").ShouldBe("Open");
        body.Text("reasons.0.ruleId").ShouldBe("REFER-YOUNG-DRIVER");
        body.Text("reasons.0.messageEn").ShouldContain("under 21");
        body.Text("reasons.0.messageEl").ShouldContain("21");
        (await BlockedAsync(job, "PRE_QUOTE")).ShouldBeFalse();
        (await BlockedAsync(job, "PRE_BIND")).ShouldBeTrue();
        (await BlockedAsync(job, "PRE_ISSUE")).ShouldBeTrue();
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'UWIssueRaised' AND business_keys->>'jobId' = '{job}'")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_UW_059_re_evaluating_the_same_hit_keeps_the_issue_and_a_cleared_hit_closes_it()
    {
        var job = Guid.CreateVersion7();
        var (_, first) = await EvaluateAsync(EvaluateBody(job, RiskTree(birthDate: "2007-06-15")));
        var (_, again) = await EvaluateAsync(EvaluateBody(job, RiskTree(birthDate: "2007-06-15")));

        again.Text("issues.0.issueId").ShouldBe(first.Text("issues.0.issueId"));
        (await ScalarAsync<long>($"SELECT count(*) FROM uw.issue WHERE job_id = '{job}'")).ShouldBe(1);
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'UWIssueRaised' AND business_keys->>'jobId' = '{job}'")).ShouldBe(1);

        var (_, fixedRisk) = await EvaluateAsync(EvaluateBody(job, RiskTree(birthDate: "1985-06-15")));

        fixedRisk.Text("outcome").ShouldBe("ACCEPT");
        (await ScalarAsync<string>($"SELECT status FROM uw.issue WHERE job_id = '{job}'")).ShouldBe("Closed");
        (await ScalarAsync<string>($"SELECT close_reason FROM uw.issue WHERE job_id = '{job}'")).ShouldBe("RULE_NO_LONGER_HITS");
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'UWIssueClosed' AND business_keys->>'jobId' = '{job}'")).ShouldBe(1);
        (await BlockedAsync(job, "PRE_BIND")).ShouldBeFalse();
    }

    // REQ-UW-060: a decline rule blocks from the quote on; decline beats refer.
    [Fact]
    public async Task REQ_UW_060_a_decline_rule_declines_blocks_from_the_quote_and_beats_a_referral()
    {
        var job = Guid.CreateVersion7();

        var (response, body) = await EvaluateAsync(EvaluateBody(job, RiskTree(usage: "BUSINESS", birthDate: "2007-06-15")));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("outcome").ShouldBe("DECLINE");
        body.Text("lane").ShouldBe("EXPERT");
        body!["reasons"]!.AsArray().Select(r => r!["ruleId"]!.GetValue<string>()).ShouldBe(["DECLINE-USAGE", "REFER-YOUNG-DRIVER"], ignoreOrder: true);
        body["reasons"]!.AsArray().Single(r => r!["ruleId"]!.GetValue<string>() == "DECLINE-USAGE")!["outcome"]!.GetValue<string>().ShouldBe("DECLINE");
        (await BlockedAsync(job, "PRE_QUOTE")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("2009-06-15", 0, "PRIVATE", "2020", "15000.00", "DECLINE-UNDERAGE-DRIVER")]
    [InlineData("1985-06-15", 5, "PRIVATE", "2020", "15000.00", "DECLINE-CLAIMS-HISTORY")]
    [InlineData("1985-06-15", 0, "PRIVATE", "2001", "3000.00", "REFER-OLD-VEHICLE")]
    [InlineData("1985-06-15", 0, "PRIVATE", "2025", "120000.00", "REFER-HIGH-VALUE")]
    public async Task REQ_UW_032_each_rule_of_the_decision_table_hits_on_its_condition(string birth, int claims, string usage, string firstRegistration, string value, string expectedRule)
    {
        var (response, body) = await EvaluateAsync(EvaluateBody(Guid.CreateVersion7(), RiskTree(firstRegistration, value, 1400, usage, birth, claims)));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["reasons"]!.AsArray().Select(r => r!["ruleId"]!.GetValue<string>()).ShouldBe([expectedRule]);
    }

    // m5/m7/m8: no exact age in the trace, a bad amount is a snapshot error, and the response says the rules are illustrative.
    [Fact]
    public async Task The_trace_keeps_rule_ids_not_the_exact_age_and_the_response_marks_the_rules_as_illustrative()
    {
        var job = Guid.CreateVersion7();
        var (_, body) = await EvaluateAsync(EvaluateBody(job, RiskTree(birthDate: "1985-06-15")));

        body.Text("dataStatus").ShouldBe("ILLUSTRATIVE_TEST_DATA");
        body!["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ShouldContain("UW-WARN-ILLUSTRATIVE-RULES");
        var trace = await ScalarAsync<string>($"SELECT trace::text FROM uw.evaluation WHERE job_id = '{job}'");
        trace.ShouldContain("matchedRules");
        trace.ShouldNotContain("driverAgeIn");
        trace.ShouldNotContain("1985");
    }

    [Theory]
    [InlineData("79228162514264337593543950336")]
    [InlineData("12.345")]
    public async Task A_vehicle_value_that_cannot_be_held_exactly_is_UW_ERR_SNAPSHOT_not_a_server_error(string value)
    {
        var risk = RiskTree();
        risk["vehicle"]!["vehicleValue"] = JsonNode.Parse(value);

        var (response, body) = await EvaluateAsync(EvaluateBody(Guid.CreateVersion7(), risk));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body?.ToJsonString());
        body.Text("code").ShouldBe("UW-ERR-SNAPSHOT");
    }

    [Fact]
    public async Task When_no_effective_date_is_given_the_athens_business_date_is_used()
    {
        var job = Guid.CreateVersion7();
        var (response, body) = await SendAsync(_client, HttpMethod.Post, "/api/uw/v1/rules/evaluate",
            new { jobRef = job, checkpoint = "PRE_BIND", snapshotRef = "s", productCode = MotorProduct, riskSnapshot = RiskTree() });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var athens = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens")));
        (await ScalarAsync<string>($"SELECT trace->>'effectiveDate' FROM uw.evaluation WHERE job_id = '{job}'"))
            .ShouldBeOneOf(athens.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), athens.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task The_rule_set_boundary_is_the_21st_birthday_on_the_effective_date()
    {
        var (_, onBirthday) = await EvaluateAsync(EvaluateBody(Guid.CreateVersion7(), RiskTree(birthDate: "2005-11-01"))); // 21 on 2026-11-01
        var (_, dayBefore) = await EvaluateAsync(EvaluateBody(Guid.CreateVersion7(), RiskTree(birthDate: "2005-11-02"))); // 20

        onBirthday.Text("outcome").ShouldBe("ACCEPT");
        dayBefore.Text("outcome").ShouldBe("REFER");
    }

    // Contract 3.5.3: evaluate is a command with a required, replayable Idempotency-Key.
    [Fact]
    public async Task Evaluate_is_idempotent_by_key_a_changed_body_is_a_mismatch_and_a_missing_key_is_refused()
    {
        var job = Guid.CreateVersion7();
        var key = Guid.NewGuid();
        var body = EvaluateBody(job, RiskTree(birthDate: "2007-06-15"));

        var (_, first) = await EvaluateAsync(body, key);
        var (replayResponse, replay) = await EvaluateAsync(body, key);

        replayResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        replay.Text("evaluationId").ShouldBe(first.Text("evaluationId"));
        (await ScalarAsync<long>($"SELECT count(*) FROM uw.evaluation WHERE job_id = '{job}'")).ShouldBe(1);

        var (mismatch, problem) = await EvaluateAsync(EvaluateBody(job, RiskTree(birthDate: "1985-06-15")), key);
        mismatch.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        problem.Text("code").ShouldBe("UW-ERR-IDEMPOTENCY-MISMATCH");

        var (noKey, noKeyBody) = await SendAsync(_client, HttpMethod.Post, "/api/uw/v1/rules/evaluate", body, withKey: false);
        noKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest, noKeyBody?.ToJsonString());
    }

    // REQ-UW-058: dry-run returns the decision and leaves nothing behind.
    [Fact]
    public async Task REQ_UW_058_a_dry_run_returns_the_decision_without_recording_anything()
    {
        var job = Guid.CreateVersion7();
        var request = JsonSerializer.Deserialize<RulesEvaluateRequest>(JsonSerializer.Serialize(EvaluateBody(job, RiskTree(birthDate: "2007-06-15")), SharedKernelJson.Options), SharedKernelJson.Options)!;
        await using var scope = CoreIns.IntegrationTests.Rating.RatingTestSupport.Scope(_factory.Services);
        var service = scope.ServiceProvider.GetRequiredService<IUnderwritingRulesService>();

        var response = await service.EvaluateAsync(request, CommandOptions.New() with { DryRun = true }, TestContext.Current.CancellationToken);

        response.Outcome.ShouldBe(RulesEvaluateResponse.OutcomeValue.Refer);
        response.Issues.Count.ShouldBe(1);
        (await ScalarAsync<long>($"SELECT count(*) FROM uw.evaluation WHERE job_id = '{job}'")).ShouldBe(0);
        (await ScalarAsync<long>($"SELECT count(*) FROM uw.issue WHERE job_id = '{job}'")).ShouldBe(0);
    }

    [Fact]
    public async Task The_in_process_contract_evaluates_with_the_callers_key_and_the_gate_answers_for_the_job()
    {
        var job = Guid.CreateVersion7();
        var request = JsonSerializer.Deserialize<RulesEvaluateRequest>(JsonSerializer.Serialize(EvaluateBody(job, RiskTree(birthDate: "2007-06-15")), SharedKernelJson.Options), SharedKernelJson.Options)!;
        var options = CommandOptions.New();
        await using var scope = CoreIns.IntegrationTests.Rating.RatingTestSupport.Scope(_factory.Services);
        var rules = scope.ServiceProvider.GetRequiredService<IUnderwritingRulesService>();
        var issues = scope.ServiceProvider.GetRequiredService<IUnderwritingIssueService>();

        var first = await rules.EvaluateAsync(request, options, TestContext.Current.CancellationToken);
        var replay = await rules.EvaluateAsync(request, options, TestContext.Current.CancellationToken);
        var gate = await issues.BlockingStatusAsync(JobId.From(job), BlockingPoint.PreBind, TestContext.Current.CancellationToken);

        replay.EvaluationId.ShouldBe(first.EvaluationId);
        gate.Blocked.ShouldBeTrue();
        gate.Issues.Single().IssueType.ShouldBe("DRIVER_AGE_REFERRAL");
        (await issues.BlockingStatusAsync(JobId.From(job), BlockingPoint.PreQuote, TestContext.Current.CancellationToken)).Blocked.ShouldBeFalse();
    }

    [Fact]
    public async Task Evaluation_errors_use_the_UW_ERR_codes_of_the_contract()
    {
        var (noSnapshot, noSnapshotBody) = await EvaluateAsync(new
        {
            jobRef = Guid.CreateVersion7(), checkpoint = "PRE_BIND", snapshotRef = "s", productCode = MotorProduct, effectiveDate = "2026-11-01",
        });
        noSnapshot.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        noSnapshotBody.Text("code").ShouldBe("UW-ERR-SNAPSHOT");

        var broken = RiskTree();
        broken.Remove("driver");
        var (empty, emptyBody) = await EvaluateAsync(EvaluateBody(Guid.CreateVersion7(), broken));
        empty.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        emptyBody.Text("code").ShouldBe("UW-ERR-SNAPSHOT");

        var (unknown, unknownBody) = await EvaluateAsync(EvaluateBody(Guid.CreateVersion7(), product: "NO_SUCH_PRODUCT"));
        unknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        unknownBody.Text("code").ShouldBe("UW-ERR-RULESET-UNRESOLVED");

        var (invalid, invalidBody) = await EvaluateAsync(new
        {
            jobRef = Guid.CreateVersion7(), checkpoint = "PRE_BIND", snapshotRef = "s", effectiveDate = "2026-11-01", riskSnapshot = RiskTree(),
        });
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest, invalidBody?.ToJsonString());
        invalidBody.Text("code").ShouldBe("UW-ERR-VALIDATION");
    }

    // The product names two rule sets (PFC uwRuleSets): -Q at PRE_QUOTE (declines), -B at PRE_BIND (declines and referrals).
    [Fact]
    public async Task REQ_UW_043_the_pre_quote_checkpoint_uses_the_quote_rule_set_and_does_not_close_a_bind_referral()
    {
        var job = Guid.CreateVersion7();
        var young = RiskTree(birthDate: "2007-06-15");
        var (_, atBind) = await EvaluateAsync(EvaluateBody(job, young, "PRE_BIND"));
        var (_, atQuote) = await EvaluateAsync(EvaluateBody(job, young, "PRE_QUOTE"));

        atBind.Text("outcome").ShouldBe("REFER");
        atBind.Text("ruleSetCode").ShouldBe("UW-MOTOR-GR-B");
        atQuote.Text("outcome").ShouldBe("ACCEPT"); // referrals are a bind-time rule
        atQuote.Text("ruleSetCode").ShouldBe("UW-MOTOR-GR-Q");
        (await ScalarAsync<string>($"SELECT status FROM uw.issue WHERE job_id = '{job}'")).ShouldBe("Open");
        (await BlockedAsync(job, "PRE_BIND")).ShouldBeTrue();

        var (_, declined) = await EvaluateAsync(EvaluateBody(Guid.CreateVersion7(), RiskTree(birthDate: "2009-06-15"), "PRE_QUOTE"));
        declined.Text("outcome").ShouldBe("DECLINE");
    }

    [Fact]
    public async Task The_underwriting_operations_need_a_granted_role()
    {
        var (denied, _) = await SendAsync(_client, HttpMethod.Post, "/api/uw/v1/rules/evaluate", EvaluateBody(Guid.CreateVersion7()), roles: "Staff.Unrelated");
        var (gateDenied, _) = await SendAsync(_client, HttpMethod.Get, $"/api/uw/v1/issues/blocking-status?jobRef={Guid.CreateVersion7()}&blockingPoint=PRE_BIND", roles: "Staff.Unrelated");

        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        gateDenied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_app_role_can_read_and_insert_underwriting_tables_but_never_delete()
    {
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        foreach (var table in new[] { "rule_set_version", "evaluation", "issue" })
        {
            foreach (var (privilege, expected) in new[] { ("SELECT", true), ("INSERT", true), ("DELETE", false), ("TRUNCATE", false) })
            {
                await using var command = dataSource.CreateCommand($"SELECT has_table_privilege('app', 'uw.{table}', '{privilege}')");
                ((bool)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(expected, $"{privilege} on uw.{table}");
            }
        }
    }
}

/// <summary>The rule set on the shared rule engine without a database (REQ-UW-030..040 subset).</summary>
public sealed class RuleSetTests
{
    // REQ-UW-038, -040: a version only compiles for activation when its test cases pass and cover every rule.
    [Fact]
    public void REQ_UW_038_the_built_in_rule_set_passes_its_own_test_cases_and_covers_every_rule()
    {
        var compiled = CompiledRuleSet.Compile(BuiltInRuleSets.Bind("MOTOR-GR"));

        compiled.Hash.Length.ShouldBe(64);
        compiled.Dto.TestCases.SelectMany(c => c.ExpectedRuleIds).Distinct().Order().ShouldBe(compiled.Dto.Rules.Select(r => r.Id).Order());
        compiled.Dto.DataStatus.ShouldBe("ILLUSTRATIVE_TEST_DATA");
        compiled.Dto.Note.ShouldContain("not an approved underwriting guideline");
    }

    [Fact]
    public void REQ_UW_038_a_version_with_a_wrong_test_expectation_is_refused()
    {
        var dto = BuiltInRuleSets.Bind("MOTOR-GR");
        var cases = dto.TestCases.ToList();
        cases[1] = cases[1] with { ExpectedRuleIds = ["REFER-HIGH-VALUE"] };

        var ex = Should.Throw<DomainException>(() => CompiledRuleSet.Compile(dto with { TestCases = cases }));

        ex.Error.Code.Value.ShouldBe("UW-ERR-RULESET-UNRESOLVED");
    }

    [Fact]
    public void REQ_UW_038_a_version_with_a_rule_no_test_covers_is_refused()
    {
        var dto = BuiltInRuleSets.Bind("MOTOR-GR");

        Should.Throw<DomainException>(() => CompiledRuleSet.Compile(dto with { TestCases = [.. dto.TestCases.Where(c => c.Name != "refer-high-value")] }));
    }

    [Fact]
    public void REQ_UW_036_a_rule_that_does_not_type_check_is_refused_on_compile()
    {
        var dto = BuiltInRuleSets.Bind("MOTOR-GR");
        var rules = dto.Rules.ToList();
        rules[0] = rules[0] with { Conditions = ["< \"eighteen\"", "-", "-", "-", "-"] };

        Should.Throw<DomainException>(() => CompiledRuleSet.Compile(dto with { Rules = rules }));
    }

    [Fact]
    public void REQ_UW_042_the_content_hash_is_stable_and_changes_with_a_threshold()
    {
        var a = CompiledRuleSet.Compile(BuiltInRuleSets.Bind("MOTOR-GR")).Hash;
        var b = CompiledRuleSet.Compile(BuiltInRuleSets.Bind("MOTOR-GR")).Hash;
        var dto = BuiltInRuleSets.Bind("MOTOR-GR");
        var rules = dto.Rules.ToList();
        var index = rules.FindIndex(r => r.Id == "REFER-HIGH-VALUE");
        rules[index] = rules[index] with { Conditions = ["-", "-", "> 90000", "-", "-"] };
        var changed = dto with { Rules = rules };

        b.ShouldBe(a);
        CompiledRuleSet.Compile(changed).Hash.ShouldNotBe(a);
    }
}
