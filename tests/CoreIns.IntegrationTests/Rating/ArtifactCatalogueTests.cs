using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Rating.RatingTestSupport;

namespace CoreIns.IntegrationTests.Rating;

/// <summary>BO-RAT-04: artefact catalogue reads (get, list, compatibility) through the REST facade on a real PostgreSQL. Synthetic artefacts only.</summary>
public sealed class ArtifactCatalogueTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
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

    private static string NewProduct() => "TST" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    private static string NewHash() => Convert.ToHexStringLower(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());

    /// <summary>Inserts a synthetic artefact (and optionally one activation) with the superuser connection.</summary>
    private async Task<string> InsertAsync(string product, string version, string? activation, DateTime createdAt, string? activationTo = null, string dataStatus = "ILLUSTRATIVE_TEST_DATA")
    {
        var hash = NewHash();
        var definition = $$"""
            {"schema":"coreins.rating-artefact/1","code":"{{product}}","label":"Synthetic","productCode":"{{product}}","productVersion":"{{version}}","engineVersion":"1",
             "metadata":{"dataStatus":"{{dataStatus}}","notATariff":true,"note":"test"},"currency":"EUR","annualTermsOnly":true,"steps":[],
             "tables":{"base":"{{NewHash()}}"},"taxPlan":[],"chargeTypes":{}
             }
            """;
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using (var command = dataSource.CreateCommand(
            """
            INSERT INTO rat.rating_artifact (artefact_hash, artefact_code, label, product_code, product_version, engine_version, data_status, definition, created_at, created_by)
            VALUES (@h, @p, 'Synthetic', @p, @v, '1', @ds, @d::jsonb, @at, 'test')
            """))
        {
            command.Parameters.AddWithValue("h", hash);
            command.Parameters.AddWithValue("p", product);
            command.Parameters.AddWithValue("v", version);
            command.Parameters.AddWithValue("ds", dataStatus);
            command.Parameters.AddWithValue("d", definition);
            command.Parameters.AddWithValue("at", createdAt);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        if (activation is not null)
        {
            await using var command = dataSource.CreateCommand(
                """
                INSERT INTO rat.rate_activation (activation_id, artefact_hash, product_code, product_version, effective_from, effective_to, status, created_at, created_by)
                VALUES (@id, @h, @p, @v, @from, @to, 'Active', now(), 'test')
                """);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("h", hash);
            command.Parameters.AddWithValue("p", product);
            command.Parameters.AddWithValue("v", version);
            command.Parameters.AddWithValue("from", DateOnly.Parse(activation));
            command.Parameters.AddWithValue("to", activationTo is null ? DBNull.Value : DateOnly.Parse(activationTo));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return hash;
    }

    private static List<string> Hashes(JsonNode? page) => [.. page!["items"]!.AsArray().Select(i => i!["manifest"]!["artefactHash"]!.GetValue<string>())];

    [Fact]
    public async Task Get_returns_manifest_status_and_compatibility_and_hides_draft_from_non_authoring_roles()
    {
        var product = NewProduct();
        var released = await InsertAsync(product, "1.0", "2020-01-01", DateTime.UtcNow);
        var draft = await InsertAsync(product, "2.0", null, DateTime.UtcNow);

        var (ok, body) = await SendAsync(_client, HttpMethod.Get, $"/api/rat/v1/rating-artifacts/{released}", roles: Underwriter);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("manifest.productCode").ShouldBe(product);
        body.Text("manifest.dataStatus").ShouldBe("ILLUSTRATIVE_TEST_DATA");
        body.Text("status.lifecycle").ShouldBe("ACTIVE");
        body!["compatibilityRecords"]!.AsArray().Count.ShouldBe(1);

        var (hidden, _) = await SendAsync(_client, HttpMethod.Get, $"/api/rat/v1/rating-artifacts/{draft}", roles: Underwriter);
        hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var (seen, draftBody) = await SendAsync(_client, HttpMethod.Get, $"/api/rat/v1/rating-artifacts/{draft}", roles: Manager);
        seen.StatusCode.ShouldBe(HttpStatusCode.OK, draftBody?.ToJsonString());
        draftBody.Text("status.lifecycle").ShouldBe("DRAFT");

        var (unknown, _) = await SendAsync(_client, HttpMethod.Get, "/api/rat/v1/rating-artifacts/" + NewHash(), roles: Manager);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_excludes_draft_for_underwriters_filters_and_pages_with_a_stable_cursor()
    {
        var product = NewProduct();
        var now = DateTime.UtcNow;
        var a = await InsertAsync(product, "1.0", "2020-01-01", now.AddMinutes(-3), "2020-12-31");
        var b = await InsertAsync(product, "1.1", "2020-01-01", now.AddMinutes(-2), dataStatus: "APPROVED");
        var c = await InsertAsync(product, "1.2", "2999-01-01", now.AddMinutes(-1));
        var draft = await InsertAsync(product, "9.0", null, now);

        var (_, uw) = await SendAsync(_client, HttpMethod.Get, $"/api/rat/v1/rating-artifacts?productCode={product}", roles: Underwriter);
        Hashes(uw).ShouldBe([c, b, a]);
        uw!["items"]![2]!["status"]!["lifecycle"]!.GetValue<string>().ShouldBe("SUPERSEDED");
        uw["items"]![0]!["status"]!["lifecycle"]!.GetValue<string>().ShouldBe("SCHEDULED");

        var (_, mgr) = await SendAsync(_client, HttpMethod.Get, $"/api/rat/v1/rating-artifacts?productCode={product}", roles: Manager);
        Hashes(mgr).ShouldBe([draft, c, b, a]);

        var (_, approved) = await SendAsync(_client, HttpMethod.Get, $"/api/rat/v1/rating-artifacts?productCode={product}&dataStatus=APPROVED", roles: Manager);
        Hashes(approved).ShouldBe([b]);
        var (_, drafts) = await SendAsync(_client, HttpMethod.Get, $"/api/rat/v1/rating-artifacts?productCode={product}&status=DRAFT", roles: Underwriter);
        Hashes(drafts).ShouldBeEmpty();

        var seen = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 4; i++)
        {
            var url = $"/api/rat/v1/rating-artifacts?productCode={product}&limit=2" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            var (response, page) = await SendAsync(_client, HttpMethod.Get, url, roles: Manager);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
            seen.AddRange(Hashes(page));
            cursor = page!["nextCursor"]?.GetValue<string>();
            if (cursor is null)
            {
                break;
            }
        }

        seen.ShouldBe([draft, c, b, a]);
        var (bad, _) = await SendAsync(_client, HttpMethod.Get, "/api/rat/v1/rating-artifacts?cursor=%21%21", roles: Manager);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Compatibility_reports_pinned_and_activated_versions_and_flags_others()
    {
        var product = NewProduct();
        var hash = await InsertAsync(product, "1.0", "2020-01-01", DateTime.UtcNow);
        var (ok, same) = await SendAsync(_client, HttpMethod.Get, $"/api/rat/v1/rating-artifacts/compatibility?artefactHash={hash}&productVersion=1.0", roles: Underwriter);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, same?.ToJsonString());
        same!["compatibilityRecords"]!.AsArray().All(r => r!["compatible"]!.GetValue<bool>()).ShouldBeTrue();

        var (_, other) = await SendAsync(_client, HttpMethod.Get, $"/api/rat/v1/rating-artifacts/compatibility?artefactHash={hash}&productVersion=3.0", roles: Underwriter);
        var records = other!["compatibilityRecords"]!.AsArray();
        records.Any(r => r!["productVersion"]!.GetValue<string>() == "3.0" && !r["compatible"]!.GetValue<bool>()).ShouldBeTrue();

        var (missing, _) = await SendAsync(_client, HttpMethod.Get, "/api/rat/v1/rating-artifacts/compatibility", roles: Underwriter);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Roles_without_a_grant_are_refused()
    {
        var (response, _) = await SendAsync(_client, HttpMethod.Get, "/api/rat/v1/rating-artifacts", roles: "Staff.Claims");
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
