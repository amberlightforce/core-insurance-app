using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json.Nodes;
using CoreIns.Testing.Contracts;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Migration;

/// <summary>
/// BO-MIG-02: legacy staging and the synthetic legacy simulator. Staging is raw and append-only; a reload of the same source and snapshot supersedes
/// the earlier version; nothing is imported. The simulator is deterministic and plants defects on request. All data is synthetic.
/// </summary>
public sealed class LegacyStagingTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Manager = "Staff.MigrationManager";
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
    public void The_simulator_is_deterministic_and_uses_only_checksum_valid_synthetic_identifiers()
    {
        var options = new LegacySimulatorOptions { Seed = 7, Parties = 15, Policies = 20, Claims = 8, Treaties = 2 };
        var one = LegacyExtractSimulator.Generate("GR1", options);
        var two = LegacyExtractSimulator.Generate("GR1", options);
        one.ToJson().ShouldBe(two.ToJson());
        LegacyExtractSimulator.Generate("GR1", options with { Seed = 8 }).ToJson().ShouldNotBe(one.ToJson());

        one.Records.Count(r => r.ObjectType == "Person").ShouldBe(15);
        one.Records.Count(r => r.ObjectType == "Policy").ShouldBe(20);
        one.Records.Count(r => r.ObjectType == "Claim").ShouldBe(8);
        one.Records.Count(r => r.ObjectType == "Treaty").ShouldBe(2);
        one.PlantedDefects.ShouldBeEmpty();
        foreach (var person in one.Records.Where(r => r.ObjectType == "Person"))
        {
            AfmValid(person.Data["afm"]!.GetValue<string>()).ShouldBeTrue();
            IbanValid(person.Data["iban"]!.GetValue<string>()).ShouldBeTrue();
            person.Data["iban"]!.GetValue<string>().ShouldStartWith("GR");
            person.Data["email"]!.GetValue<string>().ShouldEndWith("@example.invalid");
        }

        // A clean extract matches its control totals.
        foreach (var total in one.ControlTotals.Where(t => t.ObjectType == "Policy"))
        {
            one.Records.Where(r => r.ObjectType == "Policy").Sum(r => decimal.Parse(r.Data["totalPremium"]!.GetValue<string>(), CultureInfo.InvariantCulture)).ShouldBe(total.Amount);
        }
    }

    [Fact]
    public void Defects_are_planted_on_request_and_break_the_matching_rule()
    {
        var clean = LegacyExtractSimulator.Generate("GR1", new LegacySimulatorOptions { Seed = 3, Parties = 20, Policies = 20, Claims = 10 });
        var dirty = LegacyExtractSimulator.Generate("GR1", new LegacySimulatorOptions
        {
            Seed = 3, Parties = 20, Policies = 20, Claims = 10, Defects = new LegacyDefects { MalformedRows = 6, DuplicateRows = 2, SumMismatches = 3 },
        });
        dirty.PlantedDefects.Count.ShouldBe(11);
        dirty.Records.Count(r => r.ObjectType == "Person").ShouldBe(22);
        clean.Records.Count(r => r.ObjectType == "Person").ShouldBe(20);

        var persons = dirty.Records.Where(r => r.ObjectType == "Person").ToList();
        persons.GroupBy(p => p.Data["afm"]?.GetValue<string>()).Count(g => g.Count() > 1).ShouldBeGreaterThanOrEqualTo(2);

        var policies = dirty.Records.Where(r => r.ObjectType == "Policy").ToList();
        var mismatches = policies.Count(p => Decimal(p, "netPremium") + Decimal(p, "taxes") + Decimal(p, "fees") != Decimal(p, "totalPremium"));
        mismatches.ShouldBe(3);
        policies.Sum(p => Decimal(p, "totalPremium")).ShouldNotBe(dirty.ControlTotals.Single(t => t.ObjectType == "Policy").Amount);
        dirty.PlantedDefects.Count(d => d.StartsWith("BAD_", StringComparison.Ordinal) || d.StartsWith("MISSING_", StringComparison.Ordinal)).ShouldBe(6);
    }

    [Fact]
    public async Task A_json_extract_is_staged_with_hash_provenance_and_counts()
    {
        var extract = LegacyExtractSimulator.Generate("GR1", new LegacySimulatorOptions { Seed = 11, Parties = 12, Policies = 15, Claims = 5, Treaties = 2 });
        var (response, body) = await LoadAsync("GR1", Label(), "JSON", extract.ToJson());
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["version"]!.GetValue<int>().ShouldBe(1);
        body["status"]!.GetValue<string>().ShouldBe("ACTIVE");
        body["recordCount"]!.GetValue<int>().ShouldBe(34);
        body["counts"]!["Person"]!.GetValue<int>().ShouldBe(12);
        body["counts"]!["Policy"]!.GetValue<int>().ShouldBe(15);
        body["contentHash"]!.GetValue<string>().Length.ShouldBe(64);
        body["loadedBy"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Reloading_the_same_snapshot_replaces_it_and_keeps_the_earlier_version_for_provenance()
    {
        var label = Label();
        var first = LegacyExtractSimulator.Generate("GR2", new LegacySimulatorOptions { Seed = 1, Parties = 5, Policies = 5, Claims = 0, Treaties = 0 }).ToJson();
        var second = LegacyExtractSimulator.Generate("GR2", new LegacySimulatorOptions { Seed = 2, Parties = 8, Policies = 5, Claims = 0, Treaties = 0 }).ToJson();

        var one = (await LoadAsync("GR2", label, "JSON", first)).Body!;

        // The same content again is a no-op.
        var again = (await LoadAsync("GR2", label, "JSON", first)).Body!;
        again["snapshotId"]!.GetValue<string>().ShouldBe(one["snapshotId"]!.GetValue<string>());
        again["unchanged"]!.GetValue<bool>().ShouldBeTrue();

        // Changed content is version 2; version 1 is superseded, not deleted.
        var two = (await LoadAsync("GR2", label, "JSON", second)).Body!;
        two["version"]!.GetValue<int>().ShouldBe(2);
        two["status"]!.GetValue<string>().ShouldBe("ACTIVE");
        two["supersededSnapshotId"]!.GetValue<string>().ShouldBe(one["snapshotId"]!.GetValue<string>());
        two["counts"]!["Person"]!.GetValue<int>().ShouldBe(8);

        var list = await GetSnapshotsAsync("GR2");
        var mine = list.Where(s => s!["snapshot"]!.GetValue<string>() == label).ToList();
        mine.Count.ShouldBe(2);
        mine.Single(s => s!["version"]!.GetValue<int>() == 1)!["status"]!.GetValue<string>().ShouldBe("SUPERSEDED");
        mine.Single(s => s!["version"]!.GetValue<int>() == 1)!["counts"]!["Person"]!.GetValue<int>().ShouldBe(5);
        mine.Count(s => s!["status"]!.GetValue<string>() == "ACTIVE").ShouldBe(1);

        // A different snapshot label of the same source is independent.
        var other = (await LoadAsync("GR2", label + "-b", "JSON", first)).Body!;
        other["version"]!.GetValue<int>().ShouldBe(1);
        other["status"]!.GetValue<string>().ShouldBe("ACTIVE");
    }

    [Fact]
    public async Task A_csv_extract_and_the_gr1_scenario_fixture_stage_too()
    {
        var extract = LegacyExtractSimulator.Generate("GR3", new LegacySimulatorOptions { Seed = 5, Parties = 6, Policies = 4, Claims = 0, Treaties = 0 });
        var csv = (await LoadAsync("GR3", Label(), "CSV", extract.ToCsv("Person"))).Body!;
        csv["recordCount"]!.GetValue<int>().ShouldBe(6);
        csv["counts"]!["Person"]!.GetValue<int>().ShouldBe(6);
        csv["format"]!.GetValue<string>().ShouldBe("CSV");

        var fixture = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "tests", "e2e", "fixtures", "legacy-gr1.json"));
        var scenario = (await LoadAsync("GR1", Label(), "JSON", fixture)).Body!;
        scenario["counts"]!["SCENARIO"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task Structurally_bad_extracts_are_refused_and_the_load_needs_the_migration_role()
    {
        foreach (var (format, content) in new[] { ("JSON", "{not json"), ("JSON", "{\"records\":[{\"objectType\":\"Person\"}]}"), ("CSV", "name,city\nx,y\n"), ("XML", "<a/>") })
        {
            var (response, body) = await LoadAsync("GR1", Label(), format, content);
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, body?.ToJsonString());
        }

        var (tooLong, _) = await LoadAsync("TOOLONGSRC", Label(), "JSON", "[]");
        tooLong.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);

        var (forbidden, _) = await LoadAsync("GR1", Label(), "JSON", "[]", roles: "Staff.Finance");
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static string Label() => "S-" + Guid.NewGuid().ToString("N")[..12];

    private static decimal Decimal(SimRecord record, string field) => decimal.Parse(record.Data[field]!.GetValue<string>(), CultureInfo.InvariantCulture);

    private static bool AfmValid(string afm)
    {
        if (afm.Length != 9 || !afm.All(char.IsAsciiDigit))
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 8; i++)
        {
            sum += (afm[i] - '0') * (1 << (8 - i));
        }

        return sum % 11 % 10 == afm[8] - '0';
    }

    private static bool IbanValid(string iban)
    {
        if (iban.Length != 27)
        {
            return false;
        }

        var rearranged = iban[4..] + iban[..4];
        var digits = string.Concat(rearranged.Select(c => char.IsAsciiLetter(c) ? (c - 'A' + 10).ToString(CultureInfo.InvariantCulture) : c.ToString()));
        return BigInteger.Parse(digits, CultureInfo.InvariantCulture) % 97 == 1;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, object? body, string roles = Manager)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, "mia");
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

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> LoadAsync(string source, string snapshot, string format, string content, string roles = Manager)
    {
        using var request = Request(HttpMethod.Post, "/api/mig/v1/staging/load", new { source, snapshot, format, content }, roles);
        var response = await _client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<JsonArray> GetSnapshotsAsync(string source)
    {
        using var request = Request(HttpMethod.Get, $"/api/mig/v1/staging/snapshots?source={source}", null);
        var response = await _client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["items"]!.AsArray();
    }
}
