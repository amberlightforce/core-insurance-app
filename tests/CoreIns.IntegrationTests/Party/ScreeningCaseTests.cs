using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Policy;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Party;

/// <summary>
/// BO-PTY-07: a potential hit opens a screening case and a WRK task, the case is decided maker-checker (clear or true match), and the decision
/// drives the payment block and the POL bind gate, over HTTP on a real PostgreSQL. The demo provider hits on a name containing TESTHIT.
/// </summary>
public sealed class ScreeningCaseTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Compliance = "Staff.ComplianceOfficer";
    private static int _nameSeed;

    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>A Latin family name no other test uses, carrying the demo hit token (letters only).</summary>
    internal static string HitName()
    {
        var n = Interlocked.Increment(ref _nameSeed) + 2000;
        return "Testhit" + string.Concat(n.ToString(System.Globalization.CultureInfo.InvariantCulture).Select(c => (char)('a' + (c - '0'))));
    }

    internal static async Task<(HttpResponseMessage Response, JsonNode? Body)> AsAsync(HttpClient client, string user, string roles, HttpMethod method, string path, object? body = null)
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

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> AsAsync(string user, string roles, HttpMethod method, string path, object? body = null) =>
        AsAsync(_factory.CreateClient(), user, roles, method, path, body);

    private async Task<string> CreatePartyAsync(string family)
    {
        var (response, body) = await AsAsync("steward", Underwriter, HttpMethod.Post, "/api/pty/v1/parties", Person("Nikos", family, null));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return body.Text("party.partyId");
    }

    private async Task<(string Result, string? CaseNumber, bool Block)> ScreenAsync(string partyId)
    {
        var (response, body) = await AsAsync("billing", Billing, HttpMethod.Post, "/api/pty/v1/screening/screen",
            new { partyId, callerRef = new { module = "BIL", type = "Disbursement", id = Guid.NewGuid().ToString() } });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return (body.Text("result"), body!["caseNumber"]?.GetValue<string>(), body.Text("paymentBlock") == "true");
    }

    private async Task<JsonNode> CaseOfAsync(string partyId)
    {
        var (response, body) = await AsAsync("reviewer", Compliance, HttpMethod.Get, $"/api/pty/v1/screening/case-get?partyId={partyId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["items"]!.AsArray().Single()!;
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> DecideAsync(string user, string caseId, string action, string? decision = null, string? comment = "reviewed") =>
        AsAsync(user, Compliance, HttpMethod.Post, "/api/pty/v1/screening/case-decide", new { caseId, action, decision, comment });

    [Fact]
    public async Task A_hit_opens_a_case_and_a_wrk_task_and_blocks_until_a_second_person_clears_it()
    {
        var party = await CreatePartyAsync(HitName());
        var (result, caseNumber, block) = await ScreenAsync(party);
        result.ShouldBe("PotentialHit");
        caseNumber.ShouldStartWith("SCR");
        block.ShouldBeTrue();

        // Screening again finds the same open case: no second case, still blocked.
        (await ScreenAsync(party)).CaseNumber.ShouldBe(caseNumber);
        var open = await CaseOfAsync(party);
        open.Text("status").ShouldBe("OPEN");
        open.Text("caseNumber").ShouldBe(caseNumber);
        open["evidence"]!.AsArray().Single().Text("listName").ShouldBe("DEMO-LIST");
        var caseId = open.Text("caseId");

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var tasks = dataSource.CreateCommand($"SELECT count(*) FROM wrk.activity WHERE pattern_code = 'WRK-PTY-SCREENING-HIT' AND cause_ref = '{caseNumber}'");
        ((long)(await tasks.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(1);

        // Maker proposes Clear; the proposal needs a comment and a decision, and only compliance may propose.
        (await DecideAsync("maker", caseId, "PROPOSE", "CLEAR", null)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DecideAsync("maker", caseId, "PROPOSE", null)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AsAsync("biller", Billing, HttpMethod.Post, "/api/pty/v1/screening/case-decide", new { caseId, action = "PROPOSE", decision = "CLEAR", comment = "x" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (proposed, proposedBody) = await DecideAsync("maker", caseId, "PROPOSE", "CLEAR", "Different date of birth");
        proposed.StatusCode.ShouldBe(HttpStatusCode.OK, proposedBody?.ToJsonString());
        proposedBody.Text("case.status").ShouldBe("PENDING_APPROVAL");
        proposedBody.Text("case.proposedDecision").ShouldBe("CLEAR");

        // Still blocked while pending; the maker cannot approve their own proposal; a second person can.
        (await ScreenAsync(party)).Block.ShouldBeTrue();
        var (self, selfBody) = await DecideAsync("maker", caseId, "APPROVE");
        self.IsSuccessStatusCode.ShouldBeFalse();
        selfBody.Text("code").ShouldBe("PLT-ERR-SELF-APPROVAL");
        var (approved, approvedBody) = await DecideAsync("checker", caseId, "APPROVE", comment: "agreed");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, approvedBody?.ToJsonString());
        approvedBody.Text("case.status").ShouldBe("CLEARED");
        approvedBody.Text("case.decidedBy").ShouldNotBe(approvedBody.Text("case.proposedBy"));

        // Cleared: the same match no longer blocks, and a decided case cannot be decided again.
        var after = await ScreenAsync(party);
        after.Result.ShouldBe("Clear");
        after.Block.ShouldBeFalse();
        (await DecideAsync("checker", caseId, "APPROVE")).Body.Text("code").ShouldBe("PTY-ERR-STATE");
    }

    [Fact]
    public async Task A_true_match_blocks_for_good_and_a_rejected_proposal_returns_the_case_to_the_queue()
    {
        var party = await CreatePartyAsync(HitName());
        (await ScreenAsync(party)).Result.ShouldBe("PotentialHit");
        var caseId = (await CaseOfAsync(party)).Text("caseId");

        (await DecideAsync("maker", caseId, "PROPOSE", "TRUE_MATCH", "Same name, same birth date")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await DecideAsync("checker", caseId, "REJECT", comment: null)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (rejected, rejectedBody) = await DecideAsync("checker", caseId, "REJECT", comment: "Need the passport copy");
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, rejectedBody?.ToJsonString());
        rejectedBody.Text("case.status").ShouldBe("OPEN");
        rejectedBody.Text("case.decisionComment").ShouldBe("Need the passport copy");

        (await DecideAsync("maker", caseId, "PROPOSE", "TRUE_MATCH", "Passport confirms")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (approved, approvedBody) = await DecideAsync("checker", caseId, "APPROVE");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, approvedBody?.ToJsonString());
        approvedBody.Text("case.status").ShouldBe("TRUE_MATCH");

        var screened = await ScreenAsync(party);
        screened.Result.ShouldBe("Blocked");
        screened.Block.ShouldBeTrue();
        screened.CaseNumber.ShouldBe(approvedBody.Text("case.caseNumber"));
        var (_, queue) = await AsAsync("reviewer", Compliance, HttpMethod.Get, "/api/pty/v1/screening/case-get?status=TRUE_MATCH&limit=200");
        queue!["items"]!.AsArray().Select(i => i!.Text("caseId")).ShouldContain(caseId);
        (await AsAsync("reviewer", Compliance, HttpMethod.Get, "/api/pty/v1/screening/case-get?status=NOPE")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AsAsync("nobody", Underwriter, HttpMethod.Get, "/api/pty/v1/screening/case-get")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Bulk_screens_parties_and_opens_a_case_per_hit_and_an_ad_hoc_payee_gets_a_case_without_a_party()
    {
        var hit = await CreatePartyAsync(HitName());
        var clean = await CreatePartyAsync(HitName().Replace("Testhit", "Cleanly", StringComparison.Ordinal));
        var caller = new { module = "MIG", type = "Batch", id = Guid.NewGuid().ToString() };

        var (response, body) = await AsAsync("migrator", Compliance, HttpMethod.Post, "/api/pty/v1/screening/bulk", new { partyIds = new[] { hit, clean }, callerRef = caller });
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, body?.ToJsonString());
        body.Text("screened").ShouldBe("2");
        body.Text("hits").ShouldBe("1");
        body.Text("clear").ShouldBe("1");
        var results = body!["results"]!.AsArray();
        results.Single(r => r!.Text("partyId") == hit).Text("result").ShouldBe("PotentialHit");
        results.Single(r => r!.Text("partyId") == clean).Text("result").ShouldBe("Clear");
        (await CaseOfAsync(hit)).Text("status").ShouldBe("OPEN");
        (await AsAsync("migrator", Compliance, HttpMethod.Post, "/api/pty/v1/screening/bulk", new { partyIds = Array.Empty<string>(), callerRef = caller })).Response.StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);

        var payee = new { adHocPayee = new { name = HitName() }, callerRef = new { module = "CLM", type = "Payment", id = Guid.NewGuid().ToString() } };
        var (adHoc, adHocBody) = await AsAsync("claims", "Staff.ClaimsHandler", HttpMethod.Post, "/api/pty/v1/screening/screen", payee);
        adHoc.StatusCode.ShouldBe(HttpStatusCode.OK, adHocBody?.ToJsonString());
        adHocBody.Text("result").ShouldBe("PotentialHit");
        adHocBody.Text("paymentBlock").ShouldBe("true");
        var (_, again) = await AsAsync("claims", "Staff.ClaimsHandler", HttpMethod.Post, "/api/pty/v1/screening/screen", payee);
        again.Text("caseNumber").ShouldBe(adHocBody.Text("caseNumber"));
    }
}

/// <summary>BO-PTY-07: the POL bind gate screens the policyholder; an open case or a true match keeps the job Quoted, a cleared case lets it bind.</summary>
public sealed class ScreeningBindGateTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private PolicySlice _slice = null!;

    public async ValueTask InitializeAsync()
    {
        _slice = new PolicySlice(database.AppConnectionString, realRatingAndUnderwriting: true);
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    [Fact]
    public async Task A_policyholder_with_a_potential_hit_cannot_bind_until_the_case_is_cleared_and_a_true_match_never_binds()
    {
        var client = _slice.Factory.CreateClient();
        var (created, party) = await ScreeningCaseTests.AsAsync(client, "steward", Underwriter, HttpMethod.Post, "/api/pty/v1/parties", Person("Nikos", ScreeningCaseTests.HitName(), null));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, party?.ToJsonString());
        var partyId = party.Text("party.partyId");
        var (jobId, _, _) = await _slice.DraftAsync(partyId, DateTimeOffset.UtcNow.AddDays(2));
        (await _slice.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The first bind attempt screens the policyholder: potential hit, the gate fails, the job stays Quoted.
        var (blockedResponse, blocked) = await _slice.BindAsync(jobId);
        blockedResponse.StatusCode.ShouldBe(HttpStatusCode.OK, blocked?.ToJsonString());
        blocked.Text("state").ShouldBe("QUOTED");
        var gate = blocked!["gateResults"]!.AsArray().Single(g => g!.Text("gate") == "SANCTIONS_SCREENING")!;
        gate.Text("passed").ShouldBe("false");

        // Two compliance officers clear the case; the bind then goes through.
        var (_, queue) = await ScreeningCaseTests.AsAsync(client, "reviewer", "Staff.ComplianceOfficer", HttpMethod.Get, $"/api/pty/v1/screening/case-get?partyId={partyId}");
        var caseId = queue!["items"]![0]!.Text("caseId");
        (await ScreeningCaseTests.AsAsync(client, "maker", "Staff.ComplianceOfficer", HttpMethod.Post, "/api/pty/v1/screening/case-decide",
            new { caseId, action = "PROPOSE", decision = "CLEAR", comment = "False positive" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ScreeningCaseTests.AsAsync(client, "checker", "Staff.ComplianceOfficer", HttpMethod.Post, "/api/pty/v1/screening/case-decide",
            new { caseId, action = "APPROVE" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (boundResponse, bound) = await _slice.BindAsync(jobId);
        boundResponse.StatusCode.ShouldBe(HttpStatusCode.OK, bound?.ToJsonString());
        bound.Text("state").ShouldBe("BOUND");

        // A second policyholder whose case is a true match is refused for good.
        var (_, other) = await ScreeningCaseTests.AsAsync(client, "steward", Underwriter, HttpMethod.Post, "/api/pty/v1/parties", Person("Eleni", ScreeningCaseTests.HitName(), null));
        var otherId = other.Text("party.partyId");
        var (otherJob, _, _) = await _slice.DraftAsync(otherId, DateTimeOffset.UtcNow.AddDays(2));
        (await _slice.QuoteAsync(otherJob)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _slice.BindAsync(otherJob)).Body.Text("state").ShouldBe("QUOTED");
        var (_, otherQueue) = await ScreeningCaseTests.AsAsync(client, "reviewer", "Staff.ComplianceOfficer", HttpMethod.Get, $"/api/pty/v1/screening/case-get?partyId={otherId}");
        var otherCase = otherQueue!["items"]![0]!.Text("caseId");
        await ScreeningCaseTests.AsAsync(client, "maker", "Staff.ComplianceOfficer", HttpMethod.Post, "/api/pty/v1/screening/case-decide",
            new { caseId = otherCase, action = "PROPOSE", decision = "TRUE_MATCH", comment = "Confirmed" });
        await ScreeningCaseTests.AsAsync(client, "checker", "Staff.ComplianceOfficer", HttpMethod.Post, "/api/pty/v1/screening/case-decide", new { caseId = otherCase, action = "APPROVE" });
        var (_, still) = await _slice.BindAsync(otherJob);
        still.Text("state").ShouldBe("QUOTED");
        still!["gateResults"]!.AsArray().Single(g => g!.Text("gate") == "SANCTIONS_SCREENING")!.Text("passed").ShouldBe("false");
    }
}
