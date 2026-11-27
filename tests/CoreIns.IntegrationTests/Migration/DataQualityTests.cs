using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Testing.Contracts;
using Npgsql;

namespace CoreIns.IntegrationTests.Migration;

/// <summary>
/// BO-MIG-03: the DQ rule library, profiling and waivers over staged snapshots. The legacy simulator is the oracle: every planted defect is found by the
/// matching rule and a clean extract has no ERROR findings. Staging is only read; runs, findings and waivers are insert-only. All data is synthetic.
/// </summary>
public sealed class DataQualityTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Manager = "Staff.MigrationManager";
    private const string Steward = "Staff.DataSteward";
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

    [Fact]
    public async Task A_clean_extract_has_no_error_findings_and_is_profiled()
    {
        var extract = LegacyExtractSimulator.Generate("DQ1", new LegacySimulatorOptions { Seed = 21, Parties = 30, Policies = 30, Claims = 12, Treaties = 3 });
        var (source, label) = await StageAsync(extract.ToJson());
        var run = await RunAsync(source, label);
        run["errorCount"]!.GetValue<int>().ShouldBe(0, run.ToJsonString());
        run["warningCount"]!.GetValue<int>().ShouldBe(0, run.ToJsonString());
        run["ruleSetVersion"]!.GetValue<int>().ShouldBe(1);
        run["recordCount"]!.GetValue<int>().ShouldBe(75);

        var profile = run["profile"]!.AsArray();
        var person = profile.Single(p => p!["objectType"]!.GetValue<string>() == "Person")!;
        person["recordCount"]!.GetValue<int>().ShouldBe(30);
        var afm = person["fields"]!.AsArray().Single(f => f!["field"]!.GetValue<string>() == "afm")!;
        afm["distinct"]!.GetValue<int>().ShouldBe(30);
        afm["nullCount"]!.GetValue<int>().ShouldBe(0);
        afm["nullRate"]!.GetValue<string>().ShouldBe("0.0000");
        var policy = profile.Single(p => p!["objectType"]!.GetValue<string>() == "Policy")!;
        var start = policy["fields"]!.AsArray().Single(f => f!["field"]!.GetValue<string>() == "startDate")!;
        start["min"]!.GetValue<string>().ShouldStartWith("2026-");
        string.CompareOrdinal(start["min"]!.GetValue<string>(), start["max"]!.GetValue<string>()).ShouldBeLessThanOrEqualTo(0);
        var total = policy["fields"]!.AsArray().Single(f => f!["field"]!.GetValue<string>() == "totalPremium")!;
        decimal.Parse(total["min"]!.GetValue<string>(), CultureInfo.InvariantCulture).ShouldBeLessThanOrEqualTo(decimal.Parse(total["max"]!.GetValue<string>(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Every_planted_defect_is_found_by_the_matching_rule()
    {
        var extract = LegacyExtractSimulator.Generate("DQ2", new LegacySimulatorOptions
        {
            Seed = 5, Parties = 40, Policies = 40, Claims = 15, Defects = new LegacyDefects { MalformedRows = 9, DuplicateRows = 3, SumMismatches = 4 },
        });
        extract.PlantedDefects.Count.ShouldBe(16);
        var (source, label) = await StageAsync(extract.ToJson());
        var run = await RunAsync(source, label);
        var findings = await FindingsAsync(("runId", run["runId"]!.GetValue<string>()), ("limit", "1000"));
        var found = findings["items"]!.AsArray().Select(f => $"{f!["ruleCode"]!.GetValue<string>()}:{f["legacyKey"]!.GetValue<string>()}").ToHashSet();

        foreach (var planted in extract.PlantedDefects)
        {
            var parts = planted.Split(':');
            var expected = parts[0] switch
            {
                "MISSING_FAMILY_NAME" or "MISSING_START_DATE" => $"REQ.MANDATORY:{parts[1]}",
                "BAD_AFM" => $"FMT.AFM:{parts[1]}",
                "BAD_IBAN" => $"FMT.IBAN:{parts[1]}",
                "BAD_DATE" => $"FMT.DATE:{parts[1]}",
                "BAD_AMOUNT" => $"FMT.AMOUNT:{parts[1]}",
                "SUM_MISMATCH" => $"SUM.PREMIUM:{parts[1]}",
                "DUPLICATE_OF" => $"DUP.AFM:{parts[2]}",
                _ => throw new InvalidOperationException(planted),
            };
            found.ShouldContain(expected, $"planted {planted}");
        }

        // The planted defects are errors, so the run is not clean; nothing is reported for the records that were not touched.
        run["errorCount"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(extract.PlantedDefects.Count);
        findings["run"]!["runId"]!.GetValue<string>().ShouldBe(run["runId"]!.GetValue<string>());
        findings["total"]!.GetValue<int>().ShouldBe(findings["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task Referential_range_plate_and_duplicate_key_rules_report_hand_made_bad_rows()
    {
        var records = new JsonArray
        {
            Rec("Person", "P1", new JsonObject { ["givenNames"] = "Test", ["familyName"] = "One", ["birthDate"] = "1980-02-03", ["afm"] = LegacyExtractSimulator.Afm(new LegacyExtractSimulator.SplitMix(1)) }),
            Rec("Person", "P1", new JsonObject { ["givenNames"] = "Test", ["familyName"] = "Again", ["birthDate"] = "2999-01-01", ["afm"] = LegacyExtractSimulator.Afm(new LegacyExtractSimulator.SplitMix(2)) }),
            Rec("Policy", "POL1", new JsonObject
            {
                ["partyKey"] = "P404", ["plate"] = "12-34", ["startDate"] = "2026-03-01", ["endDate"] = "2026-01-01", ["netPremium"] = "-5.00", ["taxes"] = "0.00", ["fees"] = "0.00", ["totalPremium"] = "-5.00",
            }),
            Rec("Claim", "CLM1", new JsonObject { ["policyKey"] = "POL404", ["lossDate"] = "1850-01-01", ["reportedAmount"] = "10.00" }),
            Rec("Treaty", "TRT1", new JsonObject { ["treatyType"] = "QUOTA_SHARE", ["cessionPercent"] = "150" }),
            Rec("Treaty", "TRT2", new JsonObject { ["treatyType"] = "QUOTA_SHARE", ["cessionPercent"] = "0" }),
        };
        var (source, label) = await StageAsync(new JsonObject { ["records"] = records }.ToJsonString());
        var run = await RunAsync(source, label);
        var findings = await FindingsAsync(("runId", run["runId"]!.GetValue<string>()), ("limit", "1000"));
        var codes = findings["items"]!.AsArray().Select(f => $"{f!["ruleCode"]!.GetValue<string>()}:{f["legacyKey"]!.GetValue<string>()}").ToHashSet();
        foreach (var expected in new[]
        {
            "DUP.KEY:P1", "RNG.DATE:P1", "REF.POLICY_PARTY:POL1", "FMT.PLATE:POL1", "RNG.DATE:POL1", "RNG.AMOUNT:POL1", "REF.CLAIM_POLICY:CLM1", "RNG.DATE:CLM1", "RNG.PERCENT:TRT1", "RNG.PERCENT:TRT2",
        })
        {
            codes.ShouldContain(expected, string.Join(", ", codes));
        }

        // The plate is only a warning; the severity filter and the rule filter narrow the list.
        var warnings = await FindingsAsync(("runId", run["runId"]!.GetValue<string>()), ("severity", "WARNING"));
        warnings["items"]!.AsArray().Select(f => f!["ruleCode"]!.GetValue<string>()).ShouldBe(["FMT.PLATE"]);
        var one = await FindingsAsync(("runId", run["runId"]!.GetValue<string>()), ("ruleCode", "RNG.PERCENT"), ("objectType", "Treaty"));
        one["total"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public async Task A_run_can_be_repeated_without_touching_staging_and_the_latest_run_of_a_snapshot_is_the_default()
    {
        var extract = LegacyExtractSimulator.Generate("DQ3", new LegacySimulatorOptions { Seed = 9, Parties = 10, Policies = 10, Claims = 3, Defects = new LegacyDefects { MalformedRows = 3 } });
        var (source, label) = await StageAsync(extract.ToJson());
        var before = await ScalarAsync("SELECT count(*) FROM mig.staging_record r JOIN mig.staging_snapshot s USING (snapshot_id) WHERE s.snapshot = @p", label);
        var first = await RunAsync(source, label);
        var second = await RunAsync(source, label);
        second["runId"]!.GetValue<string>().ShouldNotBe(first["runId"]!.GetValue<string>());
        second["errorCount"]!.GetValue<int>().ShouldBe(first["errorCount"]!.GetValue<int>());
        (await ScalarAsync("SELECT count(*) FROM mig.staging_record r JOIN mig.staging_snapshot s USING (snapshot_id) WHERE s.snapshot = @p", label)).ShouldBe(before);

        // Both runs keep their findings; asking by snapshot returns the latest run.
        (await ScalarAsync("SELECT count(*) FROM mig.dq_run r JOIN mig.staging_snapshot s USING (snapshot_id) WHERE s.snapshot = @p", label)).ShouldBe(2);
        (await ScalarAsync("SELECT count(*) FROM mig.dq_finding f JOIN mig.staging_snapshot s USING (snapshot_id) WHERE s.snapshot = @p", label)).ShouldBe(2 * first["errorCount"]!.GetValue<int>() + 2 * first["warningCount"]!.GetValue<int>());
        var latest = await FindingsAsync(("snapshotId", first["snapshotId"]!.GetValue<string>()));
        latest["run"]!["runId"]!.GetValue<string>().ShouldBe(second["runId"]!.GetValue<string>());

        // The rules used are recorded once per version.
        (await ScalarAsync("SELECT count(*) FROM mig.dq_rule WHERE rule_set_version = 1")).ShouldBeGreaterThan(10);

        // A reload that supersedes the snapshot: running by source and label uses the new active snapshot.
        var changed = LegacyExtractSimulator.Generate("DQ3", new LegacySimulatorOptions { Seed = 10, Parties = 12, Policies = 10, Claims = 3 });
        var (reloadResponse, _) = await LoadAsync(source, label, "JSON", changed.ToJson());
        reloadResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var third = await RunAsync(source, label);
        third["snapshotId"]!.GetValue<string>().ShouldNotBe(first["snapshotId"]!.GetValue<string>());
        third["errorCount"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public async Task A_data_steward_waives_a_finding_with_an_expiry_and_the_finding_reopens_when_it_passes()
    {
        var extract = LegacyExtractSimulator.Generate("DQ4", new LegacySimulatorOptions { Seed = 4, Parties = 10, Policies = 10, Claims = 0, Defects = new LegacyDefects { SumMismatches = 1 } });
        var (source, label) = await StageAsync(extract.ToJson());
        var run = await RunAsync(source, label);
        var runId = run["runId"]!.GetValue<string>();
        var finding = (await FindingsAsync(("runId", runId)))["items"]!.AsArray().Single()!;
        finding["ruleCode"]!.GetValue<string>().ShouldBe("SUM.PREMIUM");
        finding["status"]!.GetValue<string>().ShouldBe("OPEN");
        var findingId = finding["findingId"]!.GetValue<string>();
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Only a data steward decides; the migration manager cannot, and neither can the person who ran the check.
        (await WaiveAsync(findingId, "Known legacy rounding", tomorrow, Manager, "sam")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await WaiveAsync(findingId, "Known legacy rounding", tomorrow, Steward, "mia")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var farAway = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(400).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        (await WaiveAsync(findingId, "Known legacy rounding", yesterday, Steward, "sam")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await WaiveAsync(findingId, "Known legacy rounding", farAway, Steward, "sam")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await WaiveAsync(findingId, "", tomorrow, Steward, "sam")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);

        var waived = await WaiveAsync(findingId, "Known legacy rounding", tomorrow, Steward, "sam");
        waived.StatusCode.ShouldBe(HttpStatusCode.OK);
        var waiver = JsonNode.Parse(await waived.Content.ReadAsStringAsync(Ct))!;
        waiver["expiresOn"]!.GetValue<string>().ShouldBe(tomorrow);
        waiver["waivedBy"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        (await WaiveAsync(findingId, "Again", tomorrow, Steward, "sam")).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var after = await FindingsAsync(("runId", runId));
        after["items"]!.AsArray().Single()!["status"]!.GetValue<string>().ShouldBe("WAIVED");
        (await FindingsAsync(("runId", runId), ("includeWaived", "false")))["total"]!.GetValue<int>().ShouldBe(0);

        // A later run of the same snapshot still sees the waiver on the same record and rule.
        var rerun = await RunAsync(source, label);
        (await FindingsAsync(("runId", rerun["runId"]!.GetValue<string>())))["items"]!.AsArray().Single()!["status"]!.GetValue<string>().ShouldBe("WAIVED");

        // The waiver passes its expiry: the finding is open again, in the old run and in the new one. Waivers are never edited through the API.
        (await ScalarAsync("UPDATE mig.dq_waiver SET expires_on = CURRENT_DATE - 1 WHERE finding_id = @p::uuid", findingId)).ShouldBe(1);
        (await FindingsAsync(("runId", runId)))["items"]!.AsArray().Single()!["status"]!.GetValue<string>().ShouldBe("OPEN");
        (await FindingsAsync(("runId", runId), ("includeWaived", "false")))["total"]!.GetValue<int>().ShouldBe(1);

        // Once expired, the finding can be waived again (a second waiver row).
        (await WaiveAsync(findingId, "Reviewed again", tomorrow, Steward, "sam")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ScalarAsync("SELECT count(*) FROM mig.dq_waiver WHERE finding_id = @p::uuid", findingId)).ShouldBe(2);
    }

    [Fact]
    public async Task Bad_requests_and_missing_roles_are_refused()
    {
        (await RunRawAsync(new { source = "NOPE", snapshot = "none" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RunRawAsync(new { })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await RunRawAsync(new { source = "DQ1", snapshot = "x" }, "Staff.Finance")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var noRun = Request(HttpMethod.Get, "/api/mig/v1/quality/findings", null);
        (await _client.SendAsync(noRun, Ct)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        using var forbidden = Request(HttpMethod.Get, $"/api/mig/v1/quality/findings?runId={Guid.NewGuid()}", null, "Staff.Finance");
        (await _client.SendAsync(forbidden, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var unknown = Request(HttpMethod.Get, $"/api/mig/v1/quality/findings?runId={Guid.NewGuid()}", null);
        (await _client.SendAsync(unknown, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await WaiveAsync(Guid.NewGuid().ToString(), "x", "2999-01-01", Steward, "sam")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await WaiveAsync(Guid.NewGuid().ToString(), "x", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Steward, "sam")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ten_thousand_staged_rows_are_profiled_and_checked_in_under_five_seconds()
    {
        var extract = LegacyExtractSimulator.Generate("DQ5", new LegacySimulatorOptions { Seed = 31, Parties = 4000, Policies = 4000, Claims = 1990, Treaties = 10, Defects = new LegacyDefects { MalformedRows = 50, SumMismatches = 20 } });
        extract.Records.Count.ShouldBe(10_000);
        var (source, label) = await StageAsync(extract.ToJson());
        var timer = Stopwatch.StartNew();
        var run = await RunAsync(source, label);
        timer.Stop();
        run["recordCount"]!.GetValue<int>().ShouldBe(10_000);
        run["errorCount"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(70);
        timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    private static JsonObject Rec(string type, string key, JsonObject data) => new() { ["objectType"] = type, ["legacyKey"] = key, ["data"] = data };

    private static string Label() => "Q-" + Guid.NewGuid().ToString("N")[..12];

    private async Task<(string Source, string Label)> StageAsync(string json)
    {
        var label = Label();
        var (response, body) = await LoadAsync("D" + Guid.NewGuid().ToString("N")[..7], label, "JSON", json);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return (body!["source"]!.GetValue<string>(), label);
    }

    private async Task<JsonNode> RunAsync(string source, string snapshot)
    {
        var response = await RunRawAsync(new { source, snapshot });
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        return JsonNode.Parse(text)!;
    }

    private async Task<HttpResponseMessage> RunRawAsync(object body, string roles = Manager)
    {
        using var request = Request(HttpMethod.Post, "/api/mig/v1/quality/profile/run", body, roles);
        return await _client.SendAsync(request, Ct);
    }

    private async Task<JsonNode> FindingsAsync(params (string Key, string Value)[] query)
    {
        var qs = string.Join("&", query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value)}"));
        using var request = Request(HttpMethod.Get, "/api/mig/v1/quality/findings?" + qs, null);
        var response = await _client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        return JsonNode.Parse(text)!;
    }

    private async Task<HttpResponseMessage> WaiveAsync(string findingId, string reason, string expiresOn, string roles, string user)
    {
        using var request = Request(HttpMethod.Post, "/api/mig/v1/quality/waive", new { findingId, reason, expiresOn }, roles, user);
        return await _client.SendAsync(request, Ct);
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> LoadAsync(string source, string snapshot, string format, string content)
    {
        using var request = Request(HttpMethod.Post, "/api/mig/v1/staging/load", new { source, snapshot, format, content });
        var response = await _client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, object? body, string roles = Manager, string user = "mia")
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
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

        return request;
    }

    /// <summary>Runs one statement as the database owner (the API role cannot update waivers) and returns its single number or affected row count.</summary>
    private async Task<long> ScalarAsync(string sql, string? parameter = null)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand(sql);
        if (parameter is not null)
        {
            command.Parameters.AddWithValue("p", parameter);
        }

        if (sql.StartsWith("UPDATE", StringComparison.Ordinal))
        {
            return await command.ExecuteNonQueryAsync(Ct);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }
}
