using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Compliance;

/// <summary>NX-CMP-COMPLAINTS main paths through the real Host: register to close, reply approval by a second user, 403, 404.</summary>
public sealed class ComplaintCaseTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IDisposable
{
    private const string Officer = "Staff.ComplianceOfficer";
    private const string Root = "/api/cmp/v1/complaints";

    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _factory.Dispose();

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> Call(HttpMethod method, string path, string user, object? body = null, string roles = Officer)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            request.Content = JsonContent.Create(body);
        }

        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<string> Register()
    {
        var (created, body) = await Call(HttpMethod.Post, Root, "clerk", new { description = "The claim was handled too slowly.", channel = "EMAIL", complainantType = "INDIVIDUAL", language = "el" });
        created.StatusCode.ShouldBeOneOf([HttpStatusCode.OK, HttpStatusCode.Created], body?.ToJsonString());
        return body.Text("complaint.complaintId");
    }

    private async Task Step(string op, object body, string user = "clerk")
    {
        var (response, result) = await Call(HttpMethod.Post, $"{Root}/{op}", user, body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, result?.ToJsonString());
    }

    [Fact]
    public async Task A_complaint_goes_from_registration_to_closure_with_a_second_user_approving_the_reply()
    {
        var (created, body) = await Call(HttpMethod.Post, Root, "clerk", new { description = "The claim was handled too slowly.", channel = "EMAIL", complainantType = "INDIVIDUAL", language = "el" });
        created.StatusCode.ShouldBeOneOf([HttpStatusCode.OK, HttpStatusCode.Created], body?.ToJsonString());
        var id = body.Text("complaint.complaintId");
        body.Text("complaint.status").ShouldBe("RECEIVED");
        body.Text("number").ShouldStartWith("CMP-");

        // The 50-day reply clock runs from receipt and is part of the response.
        body!["deadlines"]!.AsArray().Count.ShouldBe(1);
        body.Text("deadlines.0.clockCode").ShouldBe("CMP_COMPLAINT_REPLY");
        (await CountAsync($"SELECT count(*) FROM cmp.clock WHERE clock_code = 'CMP_COMPLAINT_REPLY' AND subject_id = '{id}' AND status IN ('RUNNING','WARNED')")).ShouldBe(1);
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ComplaintReceived' AND aggregate_id = '{id}'")).ShouldBe(1);

        // A reply needs an acknowledged complaint.
        var (early, earlyBody) = await Call(HttpMethod.Post, $"{Root}/submit-reply", "clerk", new { id, outcome = "REJECTED", replyText = "No." });
        early.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, earlyBody?.ToJsonString());
        earlyBody.Text("code").ShouldBe("CMP-ERR-COMPLAINT-STATE");

        await Step("classify", new { id, category = "CLAIMS_HANDLING", cause = "DELAY" });
        await Step("link", new { id, links = new[] { new { module = "CLM", type = "Claim", id = Guid.CreateVersion7().ToString() } } });
        await Step("acknowledge", new { id, note = "Receipt confirmed" });
        await Step("add-action", new { id, kind = "REVIEW", note = "Claim file reviewed" });

        await Step("submit-reply", new { id, outcome = "UPHELD", replyText = "We apologise and have paid the claim.", rootCause = "STAFFING" });

        // Segregation of duties: the author cannot approve the reply.
        var (same, sameBody) = await Call(HttpMethod.Post, $"{Root}/approve-reply", "clerk", new { id });
        same.StatusCode.ShouldBe(HttpStatusCode.Forbidden, sameBody?.ToJsonString());
        sameBody.Text("code").ShouldBe("CMP-ERR-SOD");

        var (approved, approvedBody) = await Call(HttpMethod.Post, $"{Root}/approve-reply", "manager", new { id });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, approvedBody?.ToJsonString());
        approvedBody.Text("complaint.status").ShouldBe("ANSWERED");
        approvedBody.Text("complaint.replyApprovedBy").ShouldBe("manager");
        approvedBody.Text("deadlines.0.status").ShouldBe("MET");
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ComplaintAnswered' AND aggregate_id = '{id}'")).ShouldBe(1);

        await Step("close", new { id, reason = "Resolved" });
        var (got, gotBody) = await Call(HttpMethod.Get, $"{Root}/{id}", "clerk");
        got.StatusCode.ShouldBe(HttpStatusCode.OK, gotBody?.ToJsonString());
        gotBody.Text("complaint.status").ShouldBe("CLOSED");
        gotBody.Text("complaint.actions.0.kind").ShouldBe("REVIEW");

        // Closed is final.
        var (again, againBody) = await Call(HttpMethod.Post, $"{Root}/classify", "clerk", new { id, category = "OTHER" });
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, againBody?.ToJsonString());

        var (query, queryBody) = await Call(HttpMethod.Get, $"{Root}/query?limit=200", "clerk");
        query.StatusCode.ShouldBe(HttpStatusCode.OK, queryBody?.ToJsonString());
        queryBody!["items"]!.AsArray().Any(i => i.Text("complaint.complaintId") == id && i.Text("number").StartsWith("CMP-", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public async Task The_query_pages_by_an_opaque_keyset_cursor_and_refuses_a_malformed_one()
    {
        var registered = new List<string> { await Register(), await Register(), await Register() };

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var (response, body) = await Call(HttpMethod.Get, $"{Root}/query?limit=2" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}"), "clerk");
            response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
            var items = body!["items"]!.AsArray();
            items.Count.ShouldBeLessThanOrEqualTo(2);
            seen.AddRange(items.Select(i => i.Text("complaint.complaintId")));
            cursor = body["nextCursor"]?.GetValue<string>();
            pages++;
        }
        while (cursor is not null && pages < 50);

        seen.Distinct().Count().ShouldBe(seen.Count);
        registered.ShouldBeSubsetOf(seen);
        pages.ShouldBeGreaterThan(1);

        // An offset-style or garbled cursor is refused, never read as the first page.
        var (bad, badBody) = await Call(HttpMethod.Get, $"{Root}/query?cursor=2", "clerk");
        bad.IsSuccessStatusCode.ShouldBeFalse(badBody?.ToJsonString());
    }

    [Fact]
    public async Task A_returned_reply_is_redrafted_and_an_upheld_complaint_needs_a_root_cause_to_close()
    {
        var id = await Register();
        await Step("acknowledge", new { id });
        await Step("submit-reply", new { id, outcome = "PARTIALLY_UPHELD", replyText = "Draft one" });
        await Step("approve-reply", new { id, approved = false, comment = "Too short" }, "manager");
        await Step("submit-reply", new { id, outcome = "PARTIALLY_UPHELD", replyText = "Draft two" });
        await Step("approve-reply", new { id }, "manager");

        var (blocked, blockedBody) = await Call(HttpMethod.Post, $"{Root}/close", "clerk", new { id });
        blocked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, blockedBody?.ToJsonString());
        blockedBody.Text("code").ShouldBe("CMP-ERR-ROOT-CAUSE-REQUIRED");

        await Step("escalate", new { id, adrBody = "Consumer Ombudsman", reference = "A-1" });
        await Step("close", new { id, rootCause = "PROCESS_GAP" });
    }

    [Fact]
    public async Task A_claim_notification_is_not_a_complaint()
    {
        var id = await Register();
        var (response, body) = await Call(HttpMethod.Post, $"{Root}/classify", "clerk", new { id, category = "CLAIM_NOTIFICATION" });
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body?.ToJsonString());
        body.Text("code").ShouldBe("CMP-ERR-NOT-A-COMPLAINT");
    }

    [Fact]
    public async Task Access_is_limited_to_compliance_roles_and_unknown_complaints_are_404()
    {
        var (forbidden, _) = await Call(HttpMethod.Get, $"{Root}/query", "clerk", roles: "Staff.Billing");
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (forbiddenPost, _) = await Call(HttpMethod.Post, Root, "clerk", new { description = "x" }, roles: "Staff.Billing");
        forbiddenPost.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var (missing, _) = await Call(HttpMethod.Get, $"{Root}/{Guid.CreateVersion7()}", "clerk");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var (missingPost, _) = await Call(HttpMethod.Post, $"{Root}/acknowledge", "clerk", new { id = Guid.CreateVersion7() });
        missingPost.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var (text, textBody) = await Call(HttpMethod.Get, $"{Root}/procedure-text?language=el", "clerk");
        text.StatusCode.ShouldBe(HttpStatusCode.OK, textBody?.ToJsonString());
        textBody.Text("language").ShouldBe("el");
    }
}
