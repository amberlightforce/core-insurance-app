using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Rating.Contracts;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Modules.Rating.Domain;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Json;
using CoreIns.Testing.Contracts.Fakes.Market;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Rating.RatingTestSupport;

namespace CoreIns.IntegrationTests.Rating;

/// <summary>
/// BO-RAT-02: the read side of a stored worksheet (explain, reperform, export, attach) over HTTP on a real PostgreSQL with the MKT double.
/// Rates are illustrative test data (D-SLC-04).
/// </summary>
public sealed class WorksheetTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Maker = "dev:maker";
    private const string MakerRole = "Staff.UnderwritingManager";
    private const string Checker = "dev:checker";
    private const string CheckerRole = "Platform.DesignAuthority";

    private ApiHostFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _baseFactory = new ApiHostFactory(database.AppConnectionString, settings: new Dictionary<string, string?> { [ClockConfiguration.ModeKey] = "Shiftable" });
        _factory = WithMarket(_baseFactory, MarketFake());
        _client = _factory.CreateClient();
        ((ShiftableClock)_factory.Services.GetRequiredService<IClock>()).Freeze(Instant.Parse("2026-11-02T09:00:00Z"));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _baseFactory.DisposeAsync();
    }

    private async Task<string> RateAsync(RateRateRequest? request = null)
    {
        await using var scope = Scope(_factory.Services);
        var response = await scope.ServiceProvider.GetRequiredService<IRatingRateService>().RateAsync(request ?? RateRequest(quoteId: Guid.CreateVersion7()), Ct);
        return response.WorksheetId!.Value.ToString();
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var value = await dataSource.CreateCommand(sql).ExecuteScalarAsync(Ct);
        return value is null or DBNull ? default : (T)value;
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> ReperformAsync(string id) =>
        SendAsync(_client, HttpMethod.Post, "/api/rat/v1/worksheets/reperform", new { worksheetId = id });

    // REQ-RAT-003: a stored worksheet re-rates to the identical hash.
    [Fact]
    public async Task Reperform_returns_the_identical_hash_and_no_drift()
    {
        var id = await RateAsync();

        var (response, body) = await ReperformAsync(id);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("matchResult.match").ShouldBe("true", body!["matchResult"]!.ToJsonString());
        body.Text("matchResult.storedHash").ShouldBe(id);
        body.Text("matchResult.reperformedHash").ShouldBe(id);
        body!["matchResult"]!["drift"]!.AsArray().Count.ShouldBe(0);
        CanonicalJson.Hash(body["worksheet"]).Value.ShouldBe(id);
    }

    // REQ-RAT-049: reperformance is under the artefact the worksheet pinned, not the one active now.
    [Fact]
    public async Task Reperform_after_a_new_artefact_activation_still_uses_the_pinned_artefact()
    {
        var id = await RateAsync();
        var pinned = (await SendAsync(_client, HttpMethod.Get, $"/api/rat/v1/worksheets/{id}")).Body.Text("worksheet.header.ratingArtefactHash");

        var baseHash = (await ScalarAsync<string>("SELECT artefact_hash FROM rat.rating_artifact WHERE product_code = 'MOTOR-GR' AND product_version = '1.0'"))!;
        var (created, change) = await SendAsUserAsync(_client, HttpMethod.Post, "/api/rat/v1/rating-change-sets", new { productCode = MotorProduct, productVersion = "7.900", baseArtefactHash = baseHash, reason = "Test change" }, MakerRole, Maker);
        created.StatusCode.ShouldBe(HttpStatusCode.OK, change?.ToJsonString());
        var (built, artefact) = await SendAsUserAsync(_client, HttpMethod.Post, "/api/rat/v1/rating-artifacts/build", new { changeSetId = change.Text("changeSetId"), dataStatus = "ILLUSTRATIVE_TEST_DATA" }, MakerRole, Maker);
        built.StatusCode.ShouldBe(HttpStatusCode.OK, artefact?.ToJsonString());
        var newHash = artefact.Text("artefactHash");
        (await SendAsUserAsync(_client, HttpMethod.Post, "/api/rat/v1/rating-artifacts/submit", new { artefactHash = newHash, reason = "Ready" }, MakerRole, Maker)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (scheduled, active) = await SendAsUserAsync(_client, HttpMethod.Post, "/api/rat/v1/rating-artifacts/schedule", new { artefactHash = newHash, reason = "Approved" }, CheckerRole, Checker);
        scheduled.StatusCode.ShouldBe(HttpStatusCode.OK, active?.ToJsonString());
        active.Text("artefactStatus").ShouldBe("ACTIVE");
        newHash.ShouldNotBe(pinned);

        var (response, body) = await ReperformAsync(id);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("matchResult.match").ShouldBe("true", body!["matchResult"]!.ToJsonString());
        body.Text("matchResult.ratingArtefactHash").ShouldBe(pinned);
        body.Text("worksheet.header.ratingArtefactHash").ShouldBe(pinned);
    }

    [Fact]
    public async Task Reperform_reports_drift_when_the_stored_worksheet_no_longer_matches_a_rerating()
    {
        var id = await RateAsync(RateRequest(RiskTree(value: "12345.00"))); // its own input: the worksheet is content-addressed and this test corrupts it
        await using (var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString))
        {
            await using var update = dataSource.CreateCommand(
                "UPDATE rat.worksheet SET body = jsonb_set(body, '{lines,0,annualPremium}', '\"999.99\"') WHERE worksheet_id = @id");
            update.Parameters.AddWithValue("id", id);
            (await update.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
        }

        var (response, body) = await ReperformAsync(id);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("matchResult.match").ShouldBe("false");
        body.Text("matchResult.storedIntact").ShouldBe("false");
        body!["matchResult"]!["drift"]!.AsArray().Select(d => d!["path"]!.GetValue<string>()).ShouldContain("$.lines[0].annualPremium");
        (await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/worksheets/reperform", new { worksheetId = new string('f', 64) })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // REQ-RAT-003, -233: the explanation is the step descriptions, in Greek or English.
    [Fact]
    public async Task Explain_gives_plain_language_text_per_step_in_greek_and_english()
    {
        var id = await RateAsync();

        var (en, english) = await SendAsync(_client, HttpMethod.Get, $"/api/rat/v1/worksheets/explain?worksheetId={id}");
        var (el, greek) = await SendAsync(_client, HttpMethod.Get, $"/api/rat/v1/worksheets/explain?worksheetId={id}&language=el");

        en.StatusCode.ShouldBe(HttpStatusCode.OK, english?.ToJsonString());
        el.StatusCode.ShouldBe(HttpStatusCode.OK, greek?.ToJsonString());
        english.Text("explanation.language").ShouldBe("en");
        greek.Text("explanation.language").ShouldBe("el");
        var lines = english!["explanation"]!["lines"]!.AsArray();
        lines.Count.ShouldBe(3);
        var steps = lines[0]!["steps"]!.AsArray();
        steps.Count.ShouldBeGreaterThan(1);
        steps.ShouldAllBe(s => s!["text"]!.GetValue<string>().Length > 0);
        var englishText = steps.Select(s => s!["text"]!.GetValue<string>()).ToList();
        var greekText = greek!["explanation"]!["lines"]![0]!["steps"]!.AsArray().Select(s => s!["text"]!.GetValue<string>()).ToList();
        greekText.Count.ShouldBe(englishText.Count);
        greekText.ShouldNotBe(englishText);
        english["explanation"]!["taxes"]!.AsArray().Count.ShouldBe(3);
        english.Text("explanation.totals.gross").ShouldBe(english.Text("worksheet.totals.gross"));
        (await SendAsync(_client, HttpMethod.Get, $"/api/rat/v1/worksheets/explain?worksheetId={new string('e', 64)}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // REQ-RAT-243: the CSV has fixed columns and one row per line, step, tax and total; JSON is the worksheet itself.
    [Fact]
    public async Task Export_csv_has_the_documented_columns_and_json_is_the_hashed_worksheet()
    {
        var id = await RateAsync();

        var (csvResponse, csv) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/worksheets/export", new { worksheetId = id, format = "CSV", language = "en" });
        var (jsonResponse, json) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/worksheets/export", new { worksheetId = id, format = "JSON" });

        csvResponse.StatusCode.ShouldBe(HttpStatusCode.OK, csv?.ToJsonString());
        csv.Text("document.contentType").ShouldStartWith("text/csv");
        var rows = csv.Text("document.content").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        rows[0].ShouldBe(string.Join(',', WorksheetReader.CsvColumns));
        rows.Count(r => r.StartsWith("LINE,", StringComparison.Ordinal)).ShouldBe(3);
        rows.Count(r => r.StartsWith("TAX,", StringComparison.Ordinal)).ShouldBe(3);
        rows.Count(r => r.StartsWith("TOTAL,", StringComparison.Ordinal)).ShouldBe(3);
        rows.Count(r => r.StartsWith("STEP,", StringComparison.Ordinal)).ShouldBeGreaterThanOrEqualTo(6);
        rows.Where(r => !r.StartsWith("STEP,", StringComparison.Ordinal)).ShouldAllBe(r => r.Length > 0);
        csv!["document"]!["columns"]!.AsArray().Count.ShouldBe(WorksheetReader.CsvColumns.Length);

        jsonResponse.StatusCode.ShouldBe(HttpStatusCode.OK, json?.ToJsonString());
        CanonicalJson.Hash(JsonNode.Parse(json.Text("document.content"))).Value.ShouldBe(id);
        (await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/worksheets/export", new { worksheetId = id, format = "XLSX" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
    }

    // REQ-RAT-229: attach links a worksheet to a transaction through worksheet_index; a replay changes nothing.
    [Fact]
    public async Task Attach_links_worksheets_to_a_transaction_and_a_replay_is_a_no_op()
    {
        var quote = Guid.CreateVersion7();
        var id = await RateAsync(RateRequest(quoteId: quote));
        var transaction = Guid.CreateVersion7();
        var body = new { worksheetIds = new[] { id }, transactionRef = new { transactionId = transaction } };

        var (first, attached) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/worksheets/attach", body);
        var (replay, again) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/worksheets/attach", body);

        first.StatusCode.ShouldBe(HttpStatusCode.OK, attached?.ToJsonString());
        attached!["attachmentResult"]!["attached"]!.AsArray().Count.ShouldBe(1);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK, again?.ToJsonString());
        again!["attachmentResult"]!["alreadyAttached"]!.AsArray().Count.ShouldBe(1);
        (await ScalarAsync<long>($"SELECT count(*) FROM rat.worksheet_index WHERE worksheet_id = '{id}' AND transaction_id = '{transaction}'")).ShouldBe(1);
        (await ScalarAsync<string>($"SELECT retention_state FROM rat.worksheet_index WHERE worksheet_id = '{id}' AND transaction_id = '{transaction}'")).ShouldBe("ATTACHED");
        (await ScalarAsync<Guid>($"SELECT quote_id FROM rat.worksheet_index WHERE worksheet_id = '{id}' AND transaction_id = '{transaction}'")).ShouldBe(quote);

        (await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/worksheets/attach", new { worksheetIds = new[] { new string('d', 64) }, transactionRef = new { transactionId = transaction } }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/worksheets/attach", new { worksheetIds = new[] { id }, transactionRef = "not-a-guid" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
    }

    private static async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsUserAsync(
        HttpClient client, HttpMethod method, string path, object body, string roles, string user)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = System.Net.Http.Json.JsonContent.Create(body);
        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }
}
