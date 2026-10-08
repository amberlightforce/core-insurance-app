using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Policy;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Underwriting;

/// <summary>
/// The referral workbench reads (uw.Referral.list / uw.Referral.get, D-USR-13) with every module real: an underwriter quotes
/// a young driver with a 1991 car worth 120,000 and the bind is referred; a senior underwriter sees the job in the queues with
/// the quote header from POL, the policyholder's name from PTY's masked view and the derived risk facts (no birth date),
/// decides, and the job moves queue. Legal-entity scoping and permissions are checked on the real host.
/// </summary>
public sealed class ReferralWorkbenchTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Manager = "Staff.UnderwritingManager";
    private const string Senior = "uw-wb-senior";

    private PolicySlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _slice = new PolicySlice(database.AppConnectionString, realRatingAndUnderwriting: true);
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private static string YoungBirthDate => DateTime.UtcNow.AddYears(-19).AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<(HttpResponseMessage Response, JsonNode? Body, string Text)> AsAsync(
        HttpMethod method, string path, string user = Senior, string roles = Manager, object? body = null, HttpClient? client = null)
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

        var response = await (client ?? _slice.Client).SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text), text);
    }

    /// <summary>A quoted job for a 19-year-old driver and a 1991 car worth 120,000 whose bind is referred (three PRE_BIND referrals).</summary>
    private async Task<(string JobId, string PartyId)> ReferredJobAsync()
    {
        var party = await _slice.CreatePartyAsync(YoungBirthDate);
        var (created, submission) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/submissions", _slice.Submission(party, DateTimeOffset.UtcNow.AddDays(2)));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, submission?.ToJsonString());
        var jobId = submission.Text("jobId");
        var risk = PolicySlice.MotorRisk();
        risk[0] = new
        {
            op = "SET_VEHICLE",
            vehicle = new
            {
                plate = "ikx-1991", make = "Mercedes-Benz", model = "190E", firstRegistrationYear = 1991, engineCapacityCc = 2000, use = "PRIVATE",
                value = new { amount = "120000.00", currency = "EUR" },
            },
        };
        var (first, draft) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/jobs/update-draft", new { jobId, versionNo = 1, expectedDraftVersion = 0, instructions = risk });
        first.StatusCode.ShouldBe(HttpStatusCode.OK, draft?.ToJsonString());
        var (second, filled) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/jobs/update-draft",
            new { jobId, versionNo = 1, expectedDraftVersion = 1, instructions = PolicySlice.DriverAndCovers(draft.Text("riskTree.vehicles.0.locator"), party) });
        second.StatusCode.ShouldBe(HttpStatusCode.OK, filled?.ToJsonString());
        var (_, quote) = await _slice.QuoteAsync(jobId);
        quote.Text("state").ShouldBe("QUOTED", quote?.ToJsonString());
        var (_, bind) = await _slice.BindAsync(jobId);
        bind.Text("state").ShouldBe("QUOTED", bind?.ToJsonString());
        return (jobId, party);
    }

    private async Task<JsonNode> QueueAsync(string queue = "OPEN", string user = Senior)
    {
        var (response, body, _) = await AsAsync(HttpMethod.Get, $"/api/uw/v1/referrals?queue={queue}&limit=100", user);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!;
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var dataSource = Npgsql.NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static JsonNode? Row(JsonNode page, string jobId) =>
        page["items"]!.AsArray().SingleOrDefault(i => i!["jobRef"]!.GetValue<string>() == jobId);

    [Fact]
    public async Task The_open_queue_shows_the_referred_job_with_quote_header_masked_name_and_reasons()
    {
        var (jobId, partyId) = await ReferredJobAsync();

        var page = await QueueAsync();

        var row = Row(page, jobId).ShouldNotBeNull();
        row.Text("jobNumber").ShouldNotBeNullOrWhiteSpace();
        row.Text("jobState").ShouldBe("QUOTED");
        row.Text("productCode").ShouldBe("MOTOR-GR");
        row.Text("customer.partyId").ShouldBe(partyId);
        row.Text("customer.displayName").ShouldBe("Παπαδοπούλου Μαρία");
        decimal.Parse(row.Text("premiumTotal.amount"), CultureInfo.InvariantCulture).ShouldBeGreaterThan(0m);
        row.Text("premiumTotal.currency").ShouldBe("EUR");
        row.Text("effectiveDate").ShouldMatch(@"^\d{4}-\d{2}-\d{2}$");
        row.Text("referralStatus").ShouldBe("Open");
        row["reasons"]!.AsArray().Select(r => r!["issueType"]!.GetValue<string>()).Order()
            .ShouldBe(["DRIVER_AGE_REFERRAL", "VEHICLE_AGE_REFERRAL", "VEHICLE_VALUE_REFERRAL"]);
        row["callerWorkedOnJob"]!.GetValue<bool>().ShouldBeFalse();
        row["lastDecidedAt"].ShouldBeNull();
        page["counts"]!["open"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(1);

        // The underwriter who quoted and bound it worked on the job (SOD-UW-02): the row says so.
        var (mine, mineBody, _) = await AsAsync(HttpMethod.Get, "/api/uw/v1/referrals?limit=100", "test-user", "Staff.Underwriter," + Manager);
        mine.StatusCode.ShouldBe(HttpStatusCode.OK, mineBody?.ToJsonString());
        Row(mineBody!, jobId).ShouldNotBeNull()["callerWorkedOnJob"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task The_detail_carries_the_risk_facts_the_rules_read_without_personal_data()
    {
        var (jobId, _) = await ReferredJobAsync();

        var (response, body, text) = await AsAsync(HttpMethod.Get, $"/api/uw/v1/referrals/{jobId}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        var referral = body!["referral"]!;
        referral.Text("summary.jobRef").ShouldBe(jobId);
        referral.Text("productVersion").ShouldNotBeNullOrWhiteSpace();
        referral.Text("expirationDate").ShouldMatch(@"^\d{4}-\d{2}-\d{2}$");
        referral["quoteVersionNo"]!.GetValue<int>().ShouldBe(1);
        (decimal.Parse(referral.Text("premium.amount"), CultureInfo.InvariantCulture) + decimal.Parse(referral.Text("taxes.amount"), CultureInfo.InvariantCulture))
            .ShouldBe(decimal.Parse(referral.Text("summary.premiumTotal.amount"), CultureInfo.InvariantCulture));
        referral.Text("facts.ruleSetCode").ShouldBe("UW-MOTOR-GR-B");
        referral.Text("facts.dataStatus").ShouldBe("ILLUSTRATIVE_TEST_DATA");
        referral.Text("facts.vehicle.make").ShouldBe("Mercedes-Benz");
        referral.Text("facts.vehicle.model").ShouldBe("190E");
        referral["facts"]!["vehicle"]!["firstRegistrationYear"]!.GetValue<int>().ShouldBe(1991);
        referral["facts"]!["vehicle"]!["ageYears"]!.GetValue<int>().ShouldBeGreaterThan(30);
        referral.Text("facts.vehicle.value.amount").ShouldBe("120000.00");
        referral.Text("facts.vehicle.usage").ShouldBe("PRIVATE");
        referral.Text("facts.driver.youngestAgeBand").ShouldBe("FROM_18_TO_20");
        referral["facts"]!["driver"]!["claimsLast5Years"]!.GetValue<int>().ShouldBe(0);
        var issues = referral["issues"]!.AsArray();
        issues.Count.ShouldBe(3);
        issues.ShouldAllBe(i => i!["status"]!.GetValue<string>() == "Open");

        // No P2: the birth date and identifiers never leave PTY; the plate (P1, not needed to decide) is not shown either.
        text.ShouldNotContain(YoungBirthDate);
        text.ShouldNotContain("birthDate", Case.Insensitive);
        text.ShouldNotContain("dateOfBirth", Case.Insensitive);
        text.ShouldNotContain("identifier", Case.Insensitive);
        text.ShouldNotContain("ikx-1991", Case.Insensitive);
        (await CountAsync($"SELECT count(*) FROM uw.evaluation WHERE job_id = '{jobId}' AND (facts::text LIKE '%{YoungBirthDate}%' OR facts::text LIKE '%irth%')"))
            .ShouldBe(0);
    }

    [Fact]
    public async Task After_a_decision_the_job_moves_queue()
    {
        var (jobId, _) = await ReferredJobAsync();
        var (_, detail, _) = await AsAsync(HttpMethod.Get, $"/api/uw/v1/referrals/{jobId}");
        var issues = detail!["referral"]!["issues"]!.AsArray();
        string IdOf(string type) => issues.Single(i => i!["issueType"]!.GetValue<string>() == type)!["id"]!.GetValue<string>();

        var (approved, approvedBody, _) = await AsAsync(HttpMethod.Post, "/api/uw/v1/issues/decide", body: new
        {
            issueIds = new[] { IdOf("DRIVER_AGE_REFERRAL"), IdOf("VEHICLE_VALUE_REFERRAL") }, decision = "APPROVE", reason = "Experienced family driver; car inspected.",
        });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, approvedBody?.ToJsonString());

        // One issue still waits: the job stays in OPEN and is now also in APPROVED_TODAY and DECIDED_BY_ME_TODAY.
        Row(await QueueAsync("OPEN"), jobId).ShouldNotBeNull().Text("referralStatus").ShouldBe("Open");
        Row(await QueueAsync("APPROVED_TODAY"), jobId).ShouldNotBeNull().Text("lastDecidedBy").ShouldContain(Senior);
        Row(await QueueAsync("DECIDED_BY_ME_TODAY"), jobId).ShouldNotBeNull();
        Row(await QueueAsync("DECIDED_BY_ME_TODAY", "someone-else"), jobId).ShouldBeNull();

        var (rejected, rejectedBody, _) = await AsAsync(HttpMethod.Post, "/api/uw/v1/issues/decide", body: new
        {
            issueIds = new[] { IdOf("VEHICLE_AGE_REFERRAL") }, decision = "REJECT", reason = "Too old for the appetite.",
        });
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, rejectedBody?.ToJsonString());

        var open = await QueueAsync("OPEN");
        Row(open, jobId).ShouldBeNull();
        var rejectedRow = Row(await QueueAsync("REJECTED"), jobId).ShouldNotBeNull();
        rejectedRow.Text("referralStatus").ShouldBe("Rejected");
        rejectedRow["reasons"]!.AsArray().Select(r => r!["status"]!.GetValue<string>()).Order().ShouldBe(["Approved", "Approved", "Rejected"]);
        var counts = (await QueueAsync("REJECTED"))["counts"]!;
        counts["rejected"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(1);
        counts["decidedByMeToday"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(1);

        var (_, after, _) = await AsAsync(HttpMethod.Get, $"/api/uw/v1/referrals/{jobId}");
        after!["referral"]!["issues"]!.AsArray().Where(i => i!["decision"] is not null).Select(i => i!["decision"]!["reason"]!.GetValue<string>())
            .ShouldContain("Too old for the appetite.");
    }

    [Fact]
    public async Task Only_underwriting_managers_and_admins_read_the_workbench()
    {
        var (jobId, _) = await ReferredJobAsync();

        (await AsAsync(HttpMethod.Get, "/api/uw/v1/referrals", "test-user", "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AsAsync(HttpMethod.Get, $"/api/uw/v1/referrals/{jobId}", "test-user", "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AsAsync(HttpMethod.Get, "/api/uw/v1/referrals", "claims", "Staff.ClaimsHandler")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AsAsync(HttpMethod.Get, "/api/uw/v1/referrals", "admin", "Platform.Admin")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AsAsync(HttpMethod.Get, $"/api/uw/v1/referrals/{jobId}", "admin", "Platform.Admin")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Bad_queue_cursor_and_unknown_job_are_refused()
    {
        var (badQueue, badQueueBody, _) = await AsAsync(HttpMethod.Get, "/api/uw/v1/referrals?queue=NOPE");
        badQueue.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        badQueueBody.Text("code").ShouldBe("UW-ERR-VALIDATION");
        (await AsAsync(HttpMethod.Get, "/api/uw/v1/referrals?cursor=not-a-cursor")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AsAsync(HttpMethod.Get, "/api/uw/v1/referrals?limit=500")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (unknown, unknownBody, _) = await AsAsync(HttpMethod.Get, $"/api/uw/v1/referrals/{Guid.CreateVersion7()}");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unknownBody.Text("code").ShouldBe("UW-ERR-NOT-FOUND");
        (await AsAsync(HttpMethod.Get, "/api/uw/v1/referrals/not-a-job")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Paging_walks_the_queue_without_repeating_a_job()
    {
        await ReferredJobAsync();
        await ReferredJobAsync();

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var (response, body, _) = await AsAsync(HttpMethod.Get, "/api/uw/v1/referrals?limit=1" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)));
            response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
            seen.AddRange(body!["items"]!.AsArray().Select(i => i!["jobRef"]!.GetValue<string>()));
            cursor = body["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null && seen.Count < 200);

        seen.Count.ShouldBeGreaterThanOrEqualTo(2);
        seen.ShouldBeUnique();
        seen.Count.ShouldBe((await QueueAsync())["counts"]!["open"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_job_of_one_legal_entity_is_not_in_another_entitys_workbench()
    {
        var (jobId, _) = await ReferredJobAsync();
        await database.ExecuteAsSuperuserAsync(
            """
            INSERT INTO mkt.legal_entity
                (legal_entity_id, code, native_name, latin_name, home_jurisdiction, pack_id, functional_currency, timezone, status, is_test_entity, record_version, created_at)
            VALUES ('0192f0c4-0000-7000-8000-0000000000c2', 'GR-OTHER', 'GR-OTHER (synthetic)', 'GR-OTHER (synthetic)', 'GR', 'gr', 'EUR', 'Europe/Athens', 'ACTIVE', true, 1, now())
            ON CONFLICT (legal_entity_id) DO NOTHING
            """, Ct);
        await using var other = new ApiHostFactory(database.AppConnectionString, settings: new Dictionary<string, string?> { ["Stamp:LegalEntity"] = "GR-OTHER" });
        using var client = other.CreateClient();

        var (list, listBody, _) = await AsAsync(HttpMethod.Get, "/api/uw/v1/referrals?limit=100", client: client);
        list.StatusCode.ShouldBe(HttpStatusCode.OK, listBody?.ToJsonString());
        Row(listBody!, jobId).ShouldBeNull();
        (await AsAsync(HttpMethod.Get, $"/api/uw/v1/referrals/{jobId}", client: client)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
