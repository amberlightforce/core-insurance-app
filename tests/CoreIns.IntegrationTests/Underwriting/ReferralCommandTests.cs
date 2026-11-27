using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Policy;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Underwriting;

/// <summary>
/// BO-UW-05 with every module real: the referral commands (request, withdraw, refer-up, request-information) and the review lock
/// (lock lease, a second locker refused, expiry, a manager breaking it with a reason, decide refused for a non-holder), and refer-up
/// changing who may decide. Decisions still go through uw.Issue.decide (maker-checker, SOD-UW-02).
/// </summary>
public sealed class ReferralCommandTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Manager = "Staff.UnderwritingManager";
    private const string Senior = "uw-rc-senior";
    private const string Other = "uw-rc-other";
    private const string Junior = "uw-rc-junior";
    private const string Underwriter = "Staff.Underwriter";

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
        HttpMethod method, string path, string user = Senior, string roles = Manager, object? body = null)
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

        var response = await _slice.Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text), text);
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body, string Text)> PostAsync(string path, object body, string user = Senior, string roles = Manager) =>
        AsAsync(HttpMethod.Post, path, user, roles, body);

    /// <summary>A quoted job for a 19-year-old driver and a 1991 car worth 120,000 whose bind is referred (three PRE_BIND referrals).</summary>
    private async Task<string> ReferredJobAsync()
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
        return jobId;
    }

    private async Task ExecAsync(string sql)
    {
        await using var dataSource = Npgsql.NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<string[]> IssueIdsAsync(string jobId)
    {
        var (_, detail, _) = await AsAsync(HttpMethod.Get, $"/api/uw/v1/referrals/{jobId}");
        return [.. detail!["referral"]!["issues"]!.AsArray().Select(i => i!["issue"]!["id"]!.GetValue<string>())];
    }

    private async Task<string[]> ReasonsAsync(string jobId, string user)
    {
        var (_, detail, _) = await AsAsync(HttpMethod.Get, $"/api/uw/v1/referrals/{jobId}", user);
        return [.. detail!["referral"]!["decidability"]!["reasons"]!.AsArray().Select(r => r!.GetValue<string>())];
    }

    [Fact]
    public async Task Lock_is_exclusive_blocks_decide_for_others_and_expires()
    {
        var jobId = await ReferredJobAsync();
        var ids = await IssueIdsAsync(jobId);

        var (locked, lockBody, lockText) = await PostAsync("/api/uw/v1/review/lock", new { jobRef = jobId });
        locked.StatusCode.ShouldBe(HttpStatusCode.OK, lockText);
        lockBody!["referral"]!["lock"]!["heldByMe"]!.GetValue<bool>().ShouldBeTrue();

        // The holder renews; a second underwriter is refused, cannot decide, and the workbench says why.
        (await PostAsync("/api/uw/v1/review/lock", new { jobRef = jobId })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (second, secondBody, _) = await PostAsync("/api/uw/v1/review/lock", new { jobRef = jobId }, Other);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        secondBody.Text("code").ShouldBe("UW-ERR-LOCKED-BY-OTHER");
        var (decide, decideBody, _) = await PostAsync("/api/uw/v1/issues/decide", new { issueIds = new[] { ids[0] }, decision = "APPROVE", reason = "ok" }, Other);
        decide.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        decideBody.Text("code").ShouldBe("UW-ERR-LOCKED-BY-OTHER");
        (await ReasonsAsync(jobId, Other)).ShouldContain("LOCKED_BY_OTHER");
        (await ReasonsAsync(jobId, Senior)).ShouldNotContain("LOCKED_BY_OTHER");

        // The lease runs out: the other underwriter takes over, decides, and the decision spends the lock.
        await ExecAsync("UPDATE uw.referral_state SET lock_expires_at = now() - interval '1 minute' WHERE lock_owner IS NOT NULL");
        (await PostAsync("/api/uw/v1/review/lock", new { jobRef = jobId }, Other)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (ok, okBody, _) = await PostAsync("/api/uw/v1/issues/decide", new { issueIds = new[] { ids[0] }, decision = "APPROVE", reason = "ok" }, Other);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, okBody?.ToJsonString());
        var (_, released, _) = await PostAsync("/api/uw/v1/review/release", new { jobRef = jobId }, Other);
        released!["referral"]!["lock"].ShouldBeNull();
    }

    [Fact]
    public async Task The_quoter_cannot_hold_the_review_lock()
    {
        var jobId = await ReferredJobAsync();

        var (response, body, _) = await PostAsync("/api/uw/v1/review/lock", new { jobRef = jobId }, "test-user", Manager + "," + Underwriter);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        body.Text("code").ShouldBe("UW-ERR-SOD");
    }

    [Fact]
    public async Task Only_a_manager_with_a_reason_breaks_another_lock()
    {
        var jobId = await ReferredJobAsync();
        (await PostAsync("/api/uw/v1/review/lock", new { jobRef = jobId })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await PostAsync("/api/uw/v1/review/release", new { jobRef = jobId }, Other)).Body.Text("code").ShouldBe("UW-ERR-LOCKED-BY-OTHER");
        var (junior, juniorBody, _) = await PostAsync("/api/uw/v1/review/release", new { jobRef = jobId, breakLock = true, reason = "urgent" }, Junior, Underwriter);
        junior.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        juniorBody.Text("code").ShouldBe("UW-ERR-AUTHORITY-DENIED");
        var (noReason, noReasonBody, _) = await PostAsync("/api/uw/v1/review/release", new { jobRef = jobId, breakLock = true }, Other);
        noReason.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        noReasonBody.Text("code").ShouldBe("UW-ERR-REASON-REQUIRED");

        var (broken, brokenBody, brokenText) = await PostAsync("/api/uw/v1/review/release", new { jobRef = jobId, breakLock = true, reason = "Holder is out sick" }, Other);
        broken.StatusCode.ShouldBe(HttpStatusCode.OK, brokenText);
        brokenBody!["referral"]!["lock"].ShouldBeNull();
        (await PostAsync("/api/uw/v1/review/lock", new { jobRef = jobId }, Other)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refer_up_changes_who_may_decide()
    {
        var jobId = await ReferredJobAsync();
        var ids = await IssueIdsAsync(jobId);

        var (up, upBody, upText) = await PostAsync(
            "/api/uw/v1/referrals/refer-up", new { jobRef = jobId, targetRole = "Platform.Admin", comment = "Above my limit" }, Junior, Underwriter);
        up.StatusCode.ShouldBe(HttpStatusCode.OK, upText);
        upBody.Text("referral.requiredRole").ShouldBe("Platform.Admin");
        upBody.Text("referral.priority").ShouldBe("HIGH");

        // The senior manager no longer may decide and the workbench says so; a holder of the target role (with authority) may.
        var (denied, deniedBody, _) = await PostAsync("/api/uw/v1/issues/decide", new { issueIds = new[] { ids[0] }, decision = "APPROVE", reason = "ok" });
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        deniedBody.Text("code").ShouldBe("UW-ERR-AUTHORITY-REFER");
        (await ReasonsAsync(jobId, Senior)).ShouldContain("REFERRED_UP");
        var (ok, okBody, _) = await PostAsync("/api/uw/v1/issues/decide", new { issueIds = new[] { ids[0] }, decision = "APPROVE", reason = "ok" }, "uw-rc-admin", Manager + ",Platform.Admin");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, okBody?.ToJsonString());
    }

    [Fact]
    public async Task Request_information_pauses_the_sla_and_request_resumes_it_and_a_stale_version_is_refused()
    {
        var jobId = await ReferredJobAsync();

        var (info, infoBody, infoText) = await PostAsync("/api/uw/v1/referrals/request-information", new { jobRef = jobId, comment = "Need the MOT certificate" }, Junior, Underwriter);
        info.StatusCode.ShouldBe(HttpStatusCode.OK, infoText);
        infoBody!["referral"]!["awaitingInformation"]!.GetValue<bool>().ShouldBeTrue();
        var version = infoBody["referral"]!["recordVersion"]!.GetValue<int>();

        var (stale, staleBody, _) = await PostAsync(
            "/api/uw/v1/referrals/request", new { jobRef = jobId, comment = "Certificate attached", expectedRecordVersion = version + 5 }, Junior, Underwriter);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        staleBody.Text("code").ShouldBe("UW-ERR-STALE");

        var (resumed, resumedBody, resumedText) = await PostAsync(
            "/api/uw/v1/referrals/request", new { jobRef = jobId, comment = "Certificate attached", expectedRecordVersion = version }, Junior, Underwriter);
        resumed.StatusCode.ShouldBe(HttpStatusCode.OK, resumedText);
        resumedBody!["referral"]!["awaitingInformation"]!.GetValue<bool>().ShouldBeFalse();
        resumedBody["referral"]!["recordVersion"]!.GetValue<int>().ShouldBeGreaterThan(version);
    }

    [Fact]
    public async Task Withdraw_by_requester_or_manager_and_not_referrable_jobs_are_refused()
    {
        var jobId = await ReferredJobAsync();

        var (none, noneBody, _) = await PostAsync("/api/uw/v1/referrals/request", new { jobRef = Guid.NewGuid(), comment = "x" }, Junior, Underwriter);
        none.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        noneBody.Text("code").ShouldBe("UW-ERR-NOT-REFERRABLE");

        (await PostAsync("/api/uw/v1/referrals/request", new { jobRef = jobId, comment = "Please review" }, Junior, Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (other, otherBody, _) = await PostAsync("/api/uw/v1/referrals/withdraw", new { jobRef = jobId, comment = "no" }, "uw-rc-peer", Underwriter);
        other.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        otherBody.Text("code").ShouldBe("UW-ERR-AUTHORITY-DENIED");

        var (done, doneBody, doneText) = await PostAsync("/api/uw/v1/referrals/withdraw", new { jobRef = jobId, comment = "Customer withdrew" }, Junior, Underwriter);
        done.StatusCode.ShouldBe(HttpStatusCode.OK, doneText);
        doneBody.Text("referral.lifecycle").ShouldBe("Withdrawn");
        var (locked, lockedBody, _) = await PostAsync("/api/uw/v1/review/lock", new { jobRef = jobId });
        locked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        lockedBody.Text("code").ShouldBe("UW-ERR-NOT-REFERRABLE");
    }
}
