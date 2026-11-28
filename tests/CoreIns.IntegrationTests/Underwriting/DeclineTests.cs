using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Rating.RatingTestSupport;

namespace CoreIns.IntegrationTests.Underwriting;

/// <summary>
/// BO-UW-07 on a real PostgreSQL 17 through the real Host: a decline from a rules outcome or by hand (numbered, <c>DeclineIssued</c>
/// published), the review of an automated decline by a second underwriter, and the refusal register query and CSV export.
/// </summary>
public sealed class DeclineTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Underwriter = "Staff.Underwriter";
    private const string Manager = "Staff.UnderwritingManager";
    private const string Anna = "uw-anna";
    private const string Boss = "uw-boss";

    private static readonly string[] AdverseHistory = ["ADVERSE_HISTORY"];
    private static readonly string[] BadCode = ["not a code"];
    private static readonly string[] OutOfAppetite = ["OUT_OF_APPETITE"];
    private static readonly object[] TheftPairs = [new { elementRef = "VEH-1", coverCode = "THEFT" }];

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

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(
        HttpMethod method, string path, string user = Anna, string roles = Underwriter, object? body = null, string? key = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.AcceptLanguage.ParseAdd("en");
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await _client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    /// <summary>A job whose newest evaluation is a DECLINE (business use of the vehicle, DECLINE-USAGE).</summary>
    private async Task<Guid> DeclinedJobAsync()
    {
        var job = Guid.CreateVersion7();
        var result = await UwTestSupport.EvaluateInProcessAsync(
            _factory.Services,
            new { jobRef = job, checkpoint = "PRE_QUOTE", snapshotRef = $"snapshot-{job:N}", productCode = MotorProduct, effectiveDate = "2026-11-01", riskSnapshot = RiskTree(usage: "BUSINESS", birthDate: "1985-06-15") },
            Anna);
        result.Text("outcome").ShouldBe("DECLINE");
        return job;
    }

    private async Task<Guid> AcceptedJobAsync()
    {
        var job = Guid.CreateVersion7();
        var result = await UwTestSupport.EvaluateInProcessAsync(
            _factory.Services,
            new { jobRef = job, checkpoint = "PRE_QUOTE", snapshotRef = $"snapshot-{job:N}", productCode = MotorProduct, effectiveDate = "2026-11-01", riskSnapshot = RiskTree(birthDate: "1985-06-15") },
            Anna);
        result.Text("outcome").ShouldBe("ACCEPT");
        return job;
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<JsonNode> DeclineJobAsync(Guid job)
    {
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/uw/v1/declines", body: new { jobRef = job });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return body!["decline"]!;
    }

    [Fact]
    public async Task An_automated_decline_outcome_becomes_a_numbered_decline_and_publishes_the_event()
    {
        var job = await DeclinedJobAsync();

        var (response, body) = await SendAsync(HttpMethod.Post, "/api/uw/v1/declines", body: new { jobRef = job });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        var decline = body!["decline"]!;
        decline.Text("declineNumber").ShouldStartWith("DCL");
        decline["automated"]!.GetValue<bool>().ShouldBeTrue();
        decline.Text("status").ShouldBe("Issued");
        decline.Text("scope").ShouldBe("FULL");
        decline["reasonCodes"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldContain("DECLINE-USAGE");
        decline.Text("letter.data.humanReviewRoute").ShouldBe("uw.Decline.requestReview");
        body["refusalRequirement"]!["required"]!.GetValue<bool>().ShouldBeTrue();

        var id = decline.Text("id");
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'DeclineIssued' AND business_keys->>'declineId' = '{id}'")).ShouldBe(1);

        var (getResponse, got) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/declines/{id}");
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        got.Text("decline.declineNumber").ShouldBe(decline.Text("declineNumber"));

        // One standing decline per job; an unknown id is a 404.
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/declines", body: new { jobRef = job })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await SendAsync(HttpMethod.Get, $"/api/uw/v1/declines/{Guid.CreateVersion7()}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_manual_decline_needs_reasons_and_a_person_and_cannot_be_sent_for_review()
    {
        var job = await AcceptedJobAsync();

        (await SendAsync(HttpMethod.Post, "/api/uw/v1/declines", body: new { jobRef = job })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/declines", body: new { jobRef = job, reasons = BadCode })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/declines", body: new { jobRef = job, intakeRef = Guid.CreateVersion7(), reasons = AdverseHistory }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/declines", roles: "Staff.Billing", body: new { jobRef = job, reasons = AdverseHistory }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var (response, body) = await SendAsync(
            HttpMethod.Post, "/api/uw/v1/declines", body: new { jobRef = job, reasons = AdverseHistory, wording = "We cannot offer cover." });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        var decline = body!["decline"]!;
        decline["automated"]!.GetValue<bool>().ShouldBeFalse();
        decline.Text("wording").ShouldBe("We cannot offer cover.");
        body["refusalRequirement"]!["required"]!.GetValue<bool>().ShouldBeFalse();

        (await SendAsync(HttpMethod.Post, "/api/uw/v1/declines/request-review", body: new { declineId = decline.Text("id"), requester = "Customer", statement = "Please look again." }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_partial_decline_of_an_intake_names_the_declined_pairs()
    {
        var intake = Guid.CreateVersion7();
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/declines", body: new { intakeRef = intake, scope = "PARTIAL", reasons = OutOfAppetite }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var (response, body) = await SendAsync(
            HttpMethod.Post, "/api/uw/v1/declines",
            body: new { intakeRef = intake, scope = "PARTIAL", reasons = OutOfAppetite, pairs = TheftPairs });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        body!["decline"]!["pairs"]!.AsArray().Single()!["coverCode"]!.GetValue<string>().ShouldBe("THEFT");
        body["decline"]!["intakeRef"]!.GetValue<string>().ShouldBe(intake.ToString());
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'DeclineIssued' AND aggregate_type = 'SubmissionIntake' AND aggregate_id = '{intake}'")).ShouldBe(1);
    }

    [Fact]
    public async Task A_second_underwriter_overturning_the_review_ends_the_decline_and_allows_a_new_one()
    {
        var job = await DeclinedJobAsync();
        var decline = await DeclineJobAsync(job);
        var id = decline.Text("id");

        // Nothing to decide before a review is requested.
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/declines/decide-review", Boss, Manager, new { declineId = id, outcome = "OVERTURNED", reason = "Too early." }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var (requested, requestedBody) = await SendAsync(
            HttpMethod.Post, "/api/uw/v1/declines/request-review", body: new { declineId = id, requester = "Customer", statement = "The vehicle is private." });
        requested.StatusCode.ShouldBe(HttpStatusCode.OK, requestedBody?.ToJsonString());
        requestedBody.Text("reviewReferralRef.status").ShouldBe("ReviewRequested");
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/declines/request-review", body: new { declineId = id, requester = "Customer", statement = "Again." }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // The person who issued the decline cannot review it; the invalid outcome and a missing reason are refused.
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/declines/decide-review", Anna, Manager, new { declineId = id, outcome = "OVERTURNED", reason = "Own work." }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/declines/decide-review", Boss, Manager, new { declineId = id, outcome = "OVERTURNED", reason = " " }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/declines/decide-review", Boss, Underwriter, new { declineId = id, outcome = "OVERTURNED", reason = "Not a manager." }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var (decided, decidedBody) = await SendAsync(
            HttpMethod.Post, "/api/uw/v1/declines/decide-review", Boss, Manager, new { declineId = id, outcome = "OVERTURNED", reason = "Usage is private; the quote form was wrong." });
        decided.StatusCode.ShouldBe(HttpStatusCode.OK, decidedBody?.ToJsonString());
        decidedBody.Text("decline.status").ShouldBe("Overturned");
        decidedBody.Text("decline.review.outcome").ShouldBe("OVERTURNED");
        decidedBody.Text("decline.review.decidedBy").ShouldContain(Boss);
        decidedBody!["refusalRequirement"]!["required"]!.GetValue<bool>().ShouldBeFalse();

        // Final: no second decision, and the job can be declined afresh with a new number.
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/declines/decide-review", Boss, Manager, new { declineId = id, outcome = "UPHELD", reason = "Changed my mind." }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var again = await DeclineJobAsync(job);
        again.Text("declineNumber").ShouldNotBe(decline.Text("declineNumber"));
    }

    [Fact]
    public async Task An_upheld_review_keeps_the_decline_standing()
    {
        var decline = await DeclineJobAsync(await DeclinedJobAsync());
        var id = decline.Text("id");
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/declines/request-review", body: new { declineId = id, requester = "Producer", statement = "Please reconsider." }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (response, body) = await SendAsync(
            HttpMethod.Post, "/api/uw/v1/declines/decide-review", Boss, Manager, new { declineId = id, outcome = "UPHELD", reason = "Business use confirmed." });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("decline.status").ShouldBe("Upheld");
        body!["refusalRequirement"]!["required"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task The_register_lists_declines_with_filters_and_exports_them_as_csv()
    {
        var automated = await DeclineJobAsync(await DeclinedJobAsync());
        var manualJob = await AcceptedJobAsync();
        var (_, manual) = await SendAsync(HttpMethod.Post, "/api/uw/v1/declines", body: new { jobRef = manualJob, reasons = AdverseHistory });
        var year = DateTime.UtcNow.Year.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Staff who are not managers may not read the register.
        (await SendAsync(HttpMethod.Get, $"/api/uw/v1/refusal-registers/query?period={year}")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var (response, page) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/refusal-registers/query?period={year}&limit=200", Boss, Manager);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        var numbers = page!["items"]!.AsArray().Select(i => i.Text("declineNumber")).ToList();
        numbers.ShouldContain(automated.Text("declineNumber"));
        numbers.ShouldContain(manual.Text("decline.declineNumber"));

        var (_, onlyManual) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/refusal-registers/query?period={year}&filters=automated%3Dfalse,reason%3DADVERSE_HISTORY&limit=200", Boss, Manager);
        onlyManual!["items"]!.AsArray().ShouldAllBe(i => i!["automated"]!.GetValue<bool>() == false);
        onlyManual["items"]!.AsArray().Select(i => i.Text("declineNumber")).ShouldContain(manual.Text("decline.declineNumber"));
        onlyManual["items"]!.AsArray().Select(i => i.Text("declineNumber")).ShouldNotContain(automated.Text("declineNumber"));

        var (_, paged) = await SendAsync(HttpMethod.Get, "/api/uw/v1/refusal-registers/query?limit=1", Boss, Manager);
        paged!["items"]!.AsArray().Count.ShouldBe(1);
        paged["nextCursor"].ShouldNotBeNull();
        (await SendAsync(HttpMethod.Get, "/api/uw/v1/refusal-registers/query?period=nonsense", Boss, Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Get, "/api/uw/v1/refusal-registers/query?filters=colour%3Dred", Boss, Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Get, "/api/uw/v1/refusal-registers/query?afm=123456789", Boss, Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);

        var (exported, export) = await SendAsync(
            HttpMethod.Post, "/api/uw/v1/refusal-registers/export", Boss, Manager,
            new { period = year, filters = new { automated = false, reason = "ADVERSE_HISTORY" }, requester = "Compliance", purpose = "Annual refusal return" });
        exported.StatusCode.ShouldBe(HttpStatusCode.OK, export?.ToJsonString());
        var csv = export.Text("fileRef.content");
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldBe("declineNumber,issuedAt,jobRef,intakeRef,scope,reasonCodes,automated,status,reviewOutcome,issuedBy");
        lines.Skip(1).ShouldContain(l => l.StartsWith($"\"{manual.Text("decline.declineNumber")}\",", StringComparison.Ordinal) && l.Contains("\"ADVERSE_HISTORY\"", StringComparison.Ordinal));
        lines.Length.ShouldBe(export!["entries"]!.AsArray().Count + 1);
        export["manifest"]!["rowCount"]!.GetValue<int>().ShouldBe(lines.Length - 1);
        export.Text("manifest.purpose").ShouldBe("Annual refusal return");
        export.Text("manifest.sha256").Length.ShouldBe(64);

        // The purpose is mandatory, an export by AFM is not available yet, and a non-manager cannot export.
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/refusal-registers/export", Boss, Manager, new { period = year, requester = "Compliance" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/refusal-registers/export", Boss, Manager, new { afm = "123456789", requester = "Compliance", purpose = "x" })).Response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/refusal-registers/export", Anna, Underwriter, new { requester = "Compliance", purpose = "x" })).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
