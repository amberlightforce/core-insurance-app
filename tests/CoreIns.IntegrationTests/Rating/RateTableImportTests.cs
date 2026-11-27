using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Rating.RatingTestSupport;

namespace CoreIns.IntegrationTests.Rating;

/// <summary>BO-RAT-05: rate-table import, validate and diff through the REST facade on a real PostgreSQL. Synthetic tables only.</summary>
public sealed class RateTableImportTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Manager = "Staff.UnderwritingManager";
    private ApiHostFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _baseFactory = new ApiHostFactory(database.AppConnectionString);
        _factory = WithMarket(_baseFactory, MarketFake());
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _baseFactory.DisposeAsync();
    }

    private static string NewCode() => "TST_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    private static object Mapping(string code, string? version = null) => new
    {
        code,
        version,
        hitPolicy = "First",
        effectiveFrom = "2027-01-01",
        description = "Synthetic test table",
        inputs = new[] { new { name = "coverageCode", type = "string", expression = "coverage" }, new { name = "displacement", type = "int", expression = "engineCc" } },
        outputs = new[] { new { name = "value", type = "decimal" } },
    };

    private static object[] Rows(string secondBand = "[1200..1599]", string firstValue = "110.00") =>
    [
        new { id = "B1", coverageCode = "\"MTPL\"", displacement = "[0..1199]", value = firstValue },
        new { id = "B2", coverageCode = "\"MTPL\"", displacement = secondBand, value = "135.00" },
        new { id = "B3", coverageCode = "\"MTPL\"", displacement = ">= 1600", value = "165.00" },
    ];

    private async Task<long> CountAsync(string code)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand("SELECT count(*) FROM rat.rate_table_version WHERE table_code = @c");
        command.Parameters.AddWithValue("c", code);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static IEnumerable<string> Codes(JsonNode? list) => list!.AsArray().Select(e => e!["code"]!.GetValue<string>());

    [Fact]
    public async Task Overlapping_age_bands_are_reported_by_validate_and_refused_by_import()
    {
        var code = NewCode();
        var body = new { rows = Rows(secondBand: "[1100..1599]"), mapping = Mapping(code) };
        var (validate, report) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/rate-tables/validate", body, Manager, withKey: false);
        validate.StatusCode.ShouldBe(HttpStatusCode.OK, report?.ToJsonString());
        report.Text("validationReport.valid").ShouldBe("false");
        Codes(report!["validationReport"]!["errors"]).ShouldContain("BAND-OVERLAP");

        var (import, problem) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/rate-tables/import", body, Manager);
        import.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        problem.Text("code").ShouldBe("RAT-ERR-IMPORT-INVALID");
        (await CountAsync(code)).ShouldBe(0);
    }

    [Fact]
    public async Task A_gap_between_bands_is_a_warning_and_a_bad_cell_is_an_error()
    {
        var gap = new { rows = Rows(secondBand: "[1300..1599]"), mapping = Mapping(NewCode()) };
        var (_, gapReport) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/rate-tables/validate", gap, Manager, withKey: false);
        gapReport.Text("validationReport.valid").ShouldBe("true");
        Codes(gapReport!["validationReport"]!["warnings"]).ShouldContain("BAND-GAP");

        var bad = new
        {
            rows = new object[]
            {
                new { id = "B1", coverageCode = "\"MTPL\"", displacement = "[0..1199]", value = "\"not a number\"" },
                new { id = "B2", coverageCode = "\"MTPL\"", displacement = ">= 1200", value = "1.00" },
            },
            mapping = Mapping(NewCode()),
        };
        var (_, badReport) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/rate-tables/validate", bad, Manager, withKey: false);
        badReport.Text("validationReport.valid").ShouldBe("false");
        badReport!["validationReport"]!["errors"]!.AsArray().Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Import_writes_a_content_hashed_version_once_and_the_hash_is_stable()
    {
        var code = NewCode();
        var body = new { rows = Rows(), mapping = Mapping(code, "1") };
        var (first, one) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/rate-tables/import", body, Manager);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, one?.ToJsonString());
        one.Text("tableVersion.created").ShouldBe("true");
        var hash = one.Text("tableVersion.hash");
        hash.Length.ShouldBe(64);

        var (_, two) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/rate-tables/import", body, Manager);
        two.Text("tableVersion.hash").ShouldBe(hash);
        two.Text("tableVersion.created").ShouldBe("false");
        (await CountAsync(code)).ShouldBe(1);

        var changed = new { rows = Rows(firstValue: "111.00"), mapping = Mapping(code, "1") };
        var (conflict, problem) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/rate-tables/import", changed, Manager);
        conflict.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        (await CountAsync(code)).ShouldBe(1);
    }

    [Fact]
    public async Task A_dry_run_import_writes_nothing()
    {
        var code = NewCode();
        var (response, body) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/rate-tables/import?dryRun=true", new { rows = Rows(), mapping = Mapping(code, "1") }, Manager);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("tableVersion.created").ShouldBe("false");
        (await CountAsync(code)).ShouldBe(0);
    }

    [Fact]
    public async Task Csv_import_and_the_cell_level_diff_of_two_versions()
    {
        var code = NewCode();
        const string csv = "id,coverageCode,displacement,value\nB1,\"\"\"MTPL\"\"\",[0..1199],110.00\nB2,\"\"\"MTPL\"\"\",>= 1200,135.00\n";
        var (v1, one) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/rate-tables/import", new { file = csv, mapping = Mapping(code) }, Manager);
        v1.StatusCode.ShouldBe(HttpStatusCode.OK, one?.ToJsonString());
        one.Text("tableVersion.version").ShouldBe("1");

        const string csv2 = "id,coverageCode,displacement,value\nB1,\"\"\"MTPL\"\"\",[0..1199],115.00\nB2,\"\"\"MTPL\"\"\",>= 1200,135.00\nB3,\"\"\"OWN-DAMAGE\"\"\",-,0.0210\n";
        var (v2, two) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/rate-tables/import", new { file = csv2, mapping = Mapping(code) }, Manager);
        v2.StatusCode.ShouldBe(HttpStatusCode.OK, two?.ToJsonString());
        two.Text("tableVersion.version").ShouldBe("2");
        two.Text("diff.addedRules.0").ShouldBe("B3");
        two.Text("diff.changedCells.0.ruleId").ShouldBe("B1");
        two.Text("diff.changedCells.0.before").ShouldBe("110.00");
        two.Text("diff.changedCells.0.after").ShouldBe("115.00");

        var (get, diff) = await SendAsync(_client, HttpMethod.Get, $"/api/rat/v1/rate-tables/diff?from={one.Text("tableVersion.hash")}&to={two.Text("tableVersion.hash")}", roles: Manager);
        get.StatusCode.ShouldBe(HttpStatusCode.OK, diff?.ToJsonString());
        diff.Text("diff.changedCells.0.column").ShouldBe("value");
        diff.Text("diff.removedRules").ShouldBe("[]");
    }

    [Fact]
    public async Task Importing_requires_a_pricing_authoring_role()
    {
        var (response, _) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/rate-tables/import", new { rows = Rows(), mapping = Mapping(NewCode()) });
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
