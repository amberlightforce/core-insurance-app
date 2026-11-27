using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Testing.Contracts;
using Npgsql;

namespace CoreIns.IntegrationTests.Migration;

/// <summary>
/// BO-MIG-04: mapping specs, code tables and the preview. A spec version is immutable once put forward, publish is maker-checker through the platform
/// approval, an unmapped code is a finding on the previewed item (never guessed), and the same input and spec always give the same item hashes. All data is synthetic.
/// </summary>
public sealed class MappingSpecTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
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

    private static string Name() => "person-" + Guid.NewGuid().ToString("N")[..10];

    private static object PersonSpec(string name) => new
    {
        name,
        sourceObjectType = "Person",
        targetModule = "PTY",
        targetObjectType = "Person",
        description = "Legacy person to PTY person",
        fields = new object[]
        {
            new { target = "person.familyName", source = "surname", transform = "TRIM", required = true },
            new { target = "person.givenNames", source = "firstName", transform = "UPPER" },
            new { target = "person.birthDate", source = "dob", transform = "DATE", format = "dd/MM/yyyy" },
            new { target = "person.income", source = "income", transform = "DECIMAL", scale = 2 },
            new { target = "person.sex", source = "sex", transform = "ENUM", codeMap = "sex" },
            new { target = "source", transform = "CONSTANT", value = "LEGACY-A" },
        },
        codeMaps = new object[]
        {
            new { name = "sex", entries = new object[] { new { legacy = "M", core = "MALE" }, new { legacy = "F", core = "FEMALE" } } },
        },
    };

    [Fact]
    public async Task A_spec_is_versioned_per_name_and_invalid_specs_are_refused()
    {
        var name = Name();
        var v1 = await CreateAsync(PersonSpec(name));
        v1["version"]!.GetValue<int>().ShouldBe(1);
        v1["status"]!.GetValue<string>().ShouldBe("DRAFT");
        v1["contentHash"]!.GetValue<string>().Length.ShouldBe(64);
        v1["codeMaps"]!.AsArray().Single()!["entries"]!.AsArray().Count.ShouldBe(2);
        var v2 = await CreateAsync(PersonSpec(name));
        v2["version"]!.GetValue<int>().ShouldBe(2);
        v2["contentHash"]!.GetValue<string>().ShouldBe(v1["contentHash"]!.GetValue<string>());
        v2["specId"]!.GetValue<string>().ShouldNotBe(v1["specId"]!.GetValue<string>());

        var list = await SendAsync(HttpMethod.Get, "/api/mig/v1/mapping/specs?name=" + name, null);
        var items = JsonNode.Parse(await list.Content.ReadAsStringAsync(Ct))!["items"]!.AsArray();
        items.Select(i => i!["version"]!.GetValue<int>()).ToArray().ShouldBe([2, 1]);
        (await SendAsync(HttpMethod.Get, "/api/mig/v1/mapping/specs/" + v1["specId"]!.GetValue<string>(), null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, "/api/mig/v1/mapping/specs/" + Guid.NewGuid(), null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Duplicate or overlapping targets, an ENUM without its table, a bad transform, a bad date format and no fields are all refused.
        (await PostAsync("/api/mig/v1/mapping/specs", Spec(Name(), new { target = "a.b", source = "x", transform = "COPY" }, new { target = "a.b.c", source = "y", transform = "COPY" }))).StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await PostAsync("/api/mig/v1/mapping/specs", Spec(Name(), new { target = "a", source = "x", transform = "ENUM", codeMap = "nope" }))).StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await PostAsync("/api/mig/v1/mapping/specs", Spec(Name(), new { target = "a", source = "x", transform = "MAGIC" }))).StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await PostAsync("/api/mig/v1/mapping/specs", Spec(Name(), new { target = "a", source = "x", transform = "DATE", format = "qqqq" }))).StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await PostAsync("/api/mig/v1/mapping/specs", Spec(Name()))).StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await PostAsync("/api/mig/v1/mapping/specs", PersonSpec("bad name!"))).StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await PostAsync("/api/mig/v1/mapping/specs", PersonSpec(Name()), "Staff.Finance")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Preview_maps_staged_rows_reports_unmapped_codes_and_writes_nothing()
    {
        var (source, label) = await StageAsync(People());
        var spec = await CreateAsync(PersonSpec(Name()));
        var specId = spec["specId"]!.GetValue<string>();
        var staged = await Count("SELECT count(*) FROM mig.staging_record");
        var specs = await Count("SELECT count(*) FROM mig.mapping_spec");

        var preview = await PreviewAsync(new { specId, source, snapshot = label });
        preview["sampled"]!.GetValue<int>().ShouldBe(3);
        preview["clean"]!.GetValue<int>().ShouldBe(1);
        preview["withIssues"]!.GetValue<int>().ShouldBe(2);
        var rows = preview["items"]!.AsArray().ToDictionary(i => i!["legacyKey"]!.GetValue<string>(), i => i!);

        var good = rows["P1"]["payload"]!;
        good["person"]!["familyName"]!.GetValue<string>().ShouldBe("Papadopoulos");
        good["person"]!["givenNames"]!.GetValue<string>().ShouldBe("NIKOS");
        good["person"]!["birthDate"]!.GetValue<string>().ShouldBe("1980-02-13");
        good["person"]!["income"]!.GetValue<string>().ShouldBe("12345.60");
        good["person"]!["sex"]!.GetValue<string>().ShouldBe("MALE");
        good["source"]!.GetValue<string>().ShouldBe("LEGACY-A");
        rows["P1"]["objectType"]!.GetValue<string>().ShouldBe("Person");
        rows["P1"]["legacyId"]!.GetValue<string>().ShouldBe("P1");
        rows["P1"]["issues"]!.AsArray().Count.ShouldBe(0);

        // An unknown code is a finding and the field is left out, never guessed.
        var unknown = rows["P2"];
        unknown["payload"]!["person"]!["sex"].ShouldBeNull();
        var issue = unknown["issues"]!.AsArray().Single()!;
        issue["code"]!.GetValue<string>().ShouldBe("UNMAPPED_CODE");
        issue["field"]!.GetValue<string>().ShouldBe("person.sex");
        issue["value"]!.GetValue<string>().ShouldBe("X");
        var unmapped = preview["unmappedCodes"]!.AsArray().Single()!;
        unmapped["codeMap"]!.GetValue<string>().ShouldBe("sex");
        unmapped["value"]!.GetValue<string>().ShouldBe("X");
        unmapped["count"]!.GetValue<int>().ShouldBe(1);

        // A bad date, an amount with too many places and a missing required field are issues too.
        rows["P3"]["issues"]!.AsArray().Select(i => i!["code"]!.GetValue<string>()).Order().ToArray().ShouldBe(["BAD_AMOUNT", "BAD_DATE", "REQUIRED_MISSING"]);

        // The same input and spec give identical items.
        var again = await PreviewAsync(new { specId, source, snapshot = label });
        again["items"]!.AsArray().Select(i => i!["itemHash"]!.GetValue<string>()).ToArray().ShouldBe(preview["items"]!.AsArray().Select(i => i!["itemHash"]!.GetValue<string>()).ToArray());
        rows["P1"]["itemHash"]!.GetValue<string>().Length.ShouldBe(64);
        rows["P1"]["itemHash"]!.GetValue<string>().ShouldNotBe(rows["P2"]["itemHash"]!.GetValue<string>());

        // A second version with the same content hashes the same items; a changed code table changes them.
        var changed = (JsonObject)JsonSerializer(PersonSpec(spec["name"]!.GetValue<string>()));
        changed["codeMaps"]![0]!["entries"]![0]!["core"] = "MAN";
        var v2 = JsonNode.Parse(await (await PostAsync("/api/mig/v1/mapping/specs", changed)).Content.ReadAsStringAsync(Ct))!;
        v2["contentHash"]!.GetValue<string>().ShouldNotBe(spec["contentHash"]!.GetValue<string>());
        var other = await PreviewAsync(new { specId = v2["specId"]!.GetValue<string>(), source, snapshot = label });
        other["items"]!.AsArray().Single(i => i!["legacyKey"]!.GetValue<string>() == "P1")!["itemHash"]!.GetValue<string>().ShouldNotBe(rows["P1"]["itemHash"]!.GetValue<string>());

        // The sample size caps the records and nothing was written to staging.
        (await PreviewAsync(new { specId, source, snapshot = label, sampleSize = 1 }))["sampled"]!.GetValue<int>().ShouldBe(1);
        (await Count("SELECT count(*) FROM mig.staging_record")).ShouldBe(staged);
        (await Count("SELECT count(*) FROM mig.mapping_spec")).ShouldBe(specs + 1);

        (await PostAsync("/api/mig/v1/mapping/preview", new { specId, source, snapshot = label, sampleSize = 5000 })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await PostAsync("/api/mig/v1/mapping/preview", new { specId = Guid.NewGuid().ToString(), source, snapshot = label })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync("/api/mig/v1/mapping/preview", new { specId, source, snapshot = "nope" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync("/api/mig/v1/mapping/preview", new { specId, source, snapshot = label }, "Staff.Finance")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Publish_is_maker_checker_and_an_approved_spec_is_immutable()
    {
        var spec = await CreateAsync(PersonSpec(Name()), user: "mia");
        var specId = spec["specId"]!.GetValue<string>();

        // The maker puts it forward; neither the creator nor the one who put it forward can approve it.
        var pending = await PublishAsync(specId, user: "mia");
        pending["status"]!.GetValue<string>().ShouldBe("PENDING_APPROVAL");
        pending["requestedBy"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        (await PostAsync("/api/mig/v1/mapping/publish", new { specId }, Manager, "mia")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync("/api/mig/v1/mapping/publish", new { specId }, "Staff.Finance", "sam")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var approved = await PublishAsync(specId, user: "sam", roles: Steward);
        approved["status"]!.GetValue<string>().ShouldBe("APPROVED");
        approved["decidedBy"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        approved["contentHash"]!.GetValue<string>().ShouldBe(spec["contentHash"]!.GetValue<string>());

        // An approved spec cannot be published again, and cannot be changed, not even in the database.
        (await PostAsync("/api/mig/v1/mapping/publish", new { specId }, Manager, "kim")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await PostAsync("/api/mig/v1/mapping/publish", new { specId, reject = true }, Manager, "kim")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await ShouldRefuseAsync($"UPDATE mig.mapping_spec SET fields = '[]'::jsonb WHERE spec_id = '{specId}'");
        await ShouldRefuseAsync($"UPDATE mig.mapping_spec SET status = 'DRAFT' WHERE spec_id = '{specId}'");
        await ShouldRefuseAsync($"UPDATE mig.mapping_spec SET content_hash = 'x' WHERE spec_id = '{specId}'");
        await ShouldRefuseAsync($"UPDATE mig.code_map SET core_value = 'X' WHERE spec_id = '{specId}'");
        await ShouldRefuseAsync($"DELETE FROM mig.code_map WHERE spec_id = '{specId}'");
        await ShouldRefuseAsync($"INSERT INTO mig.code_map (entry_id, spec_id, map_name, legacy_value, core_value) VALUES (gen_random_uuid(), '{specId}', 'sex', 'U', 'UNKNOWN')");

        // A change is a new version; the approved one is untouched.
        var v2 = await CreateAsync(PersonSpec(spec["name"]!.GetValue<string>()));
        v2["version"]!.GetValue<int>().ShouldBe(2);
        v2["status"]!.GetValue<string>().ShouldBe("DRAFT");
        var read = JsonNode.Parse(await (await SendAsync(HttpMethod.Get, "/api/mig/v1/mapping/specs/" + specId, null)).Content.ReadAsStringAsync(Ct))!;
        read["status"]!.GetValue<string>().ShouldBe("APPROVED");

        // A rejection is final; a draft cannot be rejected.
        var other = await CreateAsync(PersonSpec(Name()), user: "mia");
        var otherId = other["specId"]!.GetValue<string>();
        (await PostAsync("/api/mig/v1/mapping/publish", new { specId = otherId, reject = true }, Manager, "mia")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await PublishAsync(otherId, user: "mia");
        var rejected = await PublishAsync(otherId, user: "sam", roles: Steward, reject: true, comment: "Wrong sex codes");
        rejected["status"]!.GetValue<string>().ShouldBe("REJECTED");
        rejected["decisionComment"]!.GetValue<string>().ShouldBe("Wrong sex codes");
        (await PostAsync("/api/mig/v1/mapping/publish", new { specId = otherId }, Manager, "kim")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Preview_is_refused_while_the_snapshot_has_open_data_quality_errors_until_they_are_waived()
    {
        var records = new JsonArray
        {
            Rec("Person", "P1", new JsonObject { ["givenNames"] = "Test", ["familyName"] = "One", ["birthDate"] = "1980-02-03", ["afm"] = LegacyExtractSimulator.Afm(new LegacyExtractSimulator.SplitMix(1)), ["surname"] = "One", ["sex"] = "M" }),
            Rec("Person", "P2", new JsonObject { ["givenNames"] = "Test", ["birthDate"] = "1981-02-03", ["afm"] = LegacyExtractSimulator.Afm(new LegacyExtractSimulator.SplitMix(2)), ["surname"] = "Two", ["sex"] = "F" }),
        };
        var (source, label) = await StageAsync(new JsonObject { ["records"] = records }.ToJsonString());
        var specId = (await CreateAsync(PersonSpec(Name())))["specId"]!.GetValue<string>();

        // No DQ run yet: nothing to refuse on.
        (await PostAsync("/api/mig/v1/mapping/preview", new { specId, source, snapshot = label })).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var run = await PostAsync("/api/mig/v1/quality/profile/run", new { source, snapshot = label });
        run.StatusCode.ShouldBe(HttpStatusCode.OK);
        var runBody = JsonNode.Parse(await run.Content.ReadAsStringAsync(Ct))!;
        runBody["errorCount"]!.GetValue<int>().ShouldBeGreaterThan(0);
        var refused = await PostAsync("/api/mig/v1/mapping/preview", new { specId, source, snapshot = label });
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("open error");

        // The caller can still look at it on purpose.
        (await PostAsync("/api/mig/v1/mapping/preview", new { specId, source, snapshot = label, requireCleanDq = false })).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Waiving every error finding (by a data steward who did not run the check) lets the preview run.
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var findings = await SendAsync(HttpMethod.Get, $"/api/mig/v1/quality/findings?runId={runBody["runId"]!.GetValue<string>()}&severity=ERROR", null);
        foreach (var f in JsonNode.Parse(await findings.Content.ReadAsStringAsync(Ct))!["items"]!.AsArray())
        {
            (await PostAsync("/api/mig/v1/quality/waive", new { findingId = f!["findingId"]!.GetValue<string>(), reason = "Known legacy gap", expiresOn = tomorrow }, Steward, "sam")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await PostAsync("/api/mig/v1/mapping/preview", new { specId, source, snapshot = label })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static JsonNode JsonSerializer(object value) => System.Text.Json.JsonSerializer.SerializeToNode(value, Web)!;

    private static object Spec(string name, params object[] fields) => new
    {
        name, sourceObjectType = "Person", targetModule = "PTY", targetObjectType = "Person", fields, codeMaps = Array.Empty<object>(),
    };

    private static JsonObject Rec(string type, string key, JsonObject data) => new() { ["objectType"] = type, ["legacyKey"] = key, ["data"] = data };

    private static string People() => new JsonObject
    {
        ["records"] = new JsonArray
        {
            Rec("Person", "P1", new JsonObject { ["surname"] = "  Papadopoulos ", ["firstName"] = " Nikos", ["dob"] = "13/02/1980", ["income"] = "12345.6", ["sex"] = "M" }),
            Rec("Person", "P2", new JsonObject { ["surname"] = "Ioannou", ["firstName"] = "Maria", ["dob"] = "01/01/1990", ["income"] = "10", ["sex"] = "X" }),
            Rec("Person", "P3", new JsonObject { ["surname"] = " ", ["firstName"] = "Eleni", ["dob"] = "31/31/1990", ["income"] = "7.555", ["sex"] = "F" }),
            Rec("Policy", "POL1", new JsonObject { ["partyKey"] = "P1" }),
        },
    }.ToJsonString();

    private async Task<(string Source, string Label)> StageAsync(string json)
    {
        var label = "M-" + Guid.NewGuid().ToString("N")[..12];
        using var response = await PostAsync("/api/mig/v1/staging/load", new { source = "M" + Guid.NewGuid().ToString("N")[..7], snapshot = label, format = "JSON", content = json });
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        return (JsonNode.Parse(text)!["source"]!.GetValue<string>(), label);
    }

    private async Task<JsonNode> CreateAsync(object body, string user = "mia")
    {
        using var response = await PostAsync("/api/mig/v1/mapping/specs", body, Manager, user);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        return JsonNode.Parse(text)!;
    }

    private async Task<JsonNode> PublishAsync(string specId, string user, string roles = Manager, bool reject = false, string? comment = null)
    {
        using var response = await PostAsync("/api/mig/v1/mapping/publish", new { specId, reject, comment }, roles, user);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        return JsonNode.Parse(text)!;
    }

    private async Task<JsonNode> PreviewAsync(object body)
    {
        using var response = await PostAsync("/api/mig/v1/mapping/preview", body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        return JsonNode.Parse(text)!;
    }

    private Task<HttpResponseMessage> PostAsync(string path, object body, string roles = Manager, string user = "mia") => SendAsync(HttpMethod.Post, path, body, roles, user);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string roles = Manager, string user = "mia")
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

        return await _client.SendAsync(request, Ct);
    }

    private async Task<long> Count(string sql)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    /// <summary>The statement must fail in the database (the guard triggers), even for the database owner.</summary>
    private async Task ShouldRefuseAsync(string sql)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand(sql);
        await Should.ThrowAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync(Ct));
    }
}
