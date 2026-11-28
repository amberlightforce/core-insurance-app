using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Compliance;

/// <summary>BO-CMP-09 regulatory submissions tracker through the real Host: state machine, maker-checker (PLT approval), resubmission as a new version, audit history.</summary>
public sealed class SubmissionTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IDisposable
{
    private const string Officer = "Staff.ComplianceOfficer";
    private const string Root = "/api/cmp/v1/submissions";

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

    private async Task<string> Create(string user = "maker", object? extra = null)
    {
        var (created, body) = await Call(HttpMethod.Post, Root, user, extra ?? new { submissionType = "ANNUAL_RETURN", regulator = "BOG", periodLabel = "2026", dueDate = "2027-04-30", dueLegalStatus = "Verified" });
        created.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("submission.status").ShouldBe("PLANNED");
        return body.Text("submission.submissionId");
    }

    private async Task<JsonNode?> Ok(string op, object body, string user, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (response, result) = await Call(HttpMethod.Post, op.Length == 0 ? Root : $"{Root}/{op}", user, body);
        response.StatusCode.ShouldBe(expected, result?.ToJsonString());
        return result;
    }

    private async Task<JsonNode?> Fails(string op, object body, string user, HttpStatusCode status, string code)
    {
        var (response, result) = await Call(HttpMethod.Post, op.Length == 0 ? Root : $"{Root}/{op}", user, body);
        response.StatusCode.ShouldBe(status, result?.ToJsonString());
        result.Text("code").ShouldBe(code);
        return result;
    }

    private async Task<string> Ready(string id)
    {
        await Ok("attach-content", new { id, kind = "MART_EXPORT", refId = "mart-" + Guid.NewGuid().ToString("N"), hash = new string('a', 64), label = "Annual mart export" }, "maker");
        var requested = await Ok("approve", new { id, decision = "REQUEST", comment = "Please check" }, "maker");
        requested.Text("submission.status").ShouldBe("READY");
        requested.Text("submission.approvalStatus").ShouldBe("PENDING");
        return id;
    }

    [Fact]
    public async Task A_submission_goes_from_planned_to_acknowledged_with_a_second_user_approving()
    {
        var id = await Create();

        var attached = await Ok("attach-content", new { id, kind = "TAX_RETURN", refId = Guid.NewGuid().ToString(), label = "IPT return Q4" }, "maker");
        attached.Text("submission.status").ShouldBe("IN_PREPARATION");
        attached!["submission"]!["evidence"]!.AsArray().Count.ShouldBe(1);

        // Same link twice stays one item.
        var again = await Ok("attach-content", new { id, kind = "TAX_RETURN", refId = attached.Text("submission.evidence.0.refId") }, "maker");
        again!["submission"]!["evidence"]!.AsArray().Count.ShouldBe(1);

        var commented = await Ok("comment", new { id, text = "Waiting for the final figures." }, "maker");
        commented!["submission"]!["history"]!.AsArray().Any(h => h.Text("kind") == "COMMENT").ShouldBeTrue();

        // Filing needs an approval first.
        await Fails("record-filing", new { submissionId = id, channel = "PORTAL", filingReference = "X-1" }, "maker", HttpStatusCode.UnprocessableEntity, "CMP-ERR-SUBMISSION-STATE");

        var requested = await Ok("approve", new { id, decision = "REQUEST" }, "maker");
        requested.Text("submission.status").ShouldBe("READY");
        requested.Text("submission.approvalStatus").ShouldBe("PENDING");

        // READY content is frozen.
        await Fails("attach-content", new { id, kind = "OTHER", refId = "late" }, "maker", HttpStatusCode.UnprocessableEntity, "CMP-ERR-SUBMISSION-STATE");
        await Fails("record-filing", new { submissionId = id, channel = "PORTAL", filingReference = "X-1" }, "maker", HttpStatusCode.UnprocessableEntity, "CMP-ERR-SUBMISSION-NOT-APPROVED");

        // Maker is not checker.
        await Fails("approve", new { id, decision = "APPROVE" }, "maker", HttpStatusCode.Forbidden, "CMP-ERR-APPROVAL-SAME-USER");

        var approved = await Ok("approve", new { id, decision = "APPROVE", comment = "Checked against the ledger" }, "checker");
        approved.Text("submission.approvalStatus").ShouldBe("APPROVED");
        approved.Text("submission.approvedBy").ShouldBe("checker");
        approved.Text("submission.status").ShouldBe("READY");

        var filed = await Ok("record-filing", new { submissionId = id, channel = "PORTAL", filingReference = "BOG-2026-77", receiptHash = new string('b', 64) }, "maker");
        filed.Text("submission.status").ShouldBe("SUBMITTED");
        filed.Text("submission.filingReference").ShouldBe("BOG-2026-77");
        filed.Text("submission.receiptHash").ShouldBe(new string('b', 64));

        // A submitted record is never edited; comments only append to its history.
        await Fails("attach-content", new { id, kind = "OTHER", refId = "x" }, "maker", HttpStatusCode.UnprocessableEntity, "CMP-ERR-SUBMISSION-STATE");
        await Fails("record-filing", new { submissionId = id, channel = "PORTAL", filingReference = "BOG-2" }, "maker", HttpStatusCode.UnprocessableEntity, "CMP-ERR-SUBMISSION-STATE");
        await Ok("comment", new { id, text = "Filed on time." }, "checker");

        var acknowledged = await Ok("record-response", new { id, outcome = "ACKNOWLEDGED", reference = "ACK-1" }, "maker");
        acknowledged.Text("submission.status").ShouldBe("ACKNOWLEDGED");

        // Every transition is in the history, in order.
        var (got, gotBody) = await Call(HttpMethod.Get, $"{Root}/{id}", "maker");
        got.StatusCode.ShouldBe(HttpStatusCode.OK, gotBody?.ToJsonString());
        var steps = gotBody!["submission"]!["history"]!.AsArray().Where(h => h.Text("kind") == "TRANSITION").Select(h => h.Text("toStatus")).ToList();
        steps.ShouldBe(["PLANNED", "IN_PREPARATION", "READY", "READY", "SUBMITTED", "ACKNOWLEDGED"]);
        (await CountAsync($"SELECT count(*) FROM cmp.submission_history WHERE submission_id = '{id}' AND kind = 'COMMENT'")).ShouldBe(2);

        var (query, queryBody) = await Call(HttpMethod.Get, $"{Root}/query?status=ACKNOWLEDGED&limit=200", "maker");
        query.StatusCode.ShouldBe(HttpStatusCode.OK, queryBody?.ToJsonString());
        queryBody!["items"]!.AsArray().Any(i => i.Text("submissionId") == id).ShouldBeTrue();
    }

    [Fact]
    public async Task A_rejected_submission_is_resubmitted_as_a_new_linked_version()
    {
        var id = await Create();
        await Ready(id);
        await Ok("approve", new { id, decision = "APPROVE" }, "checker");
        await Ok("record-filing", new { submissionId = id, channel = "API", filingReference = "REF-1" }, "maker");
        var rejected = await Ok("record-response", new { id, outcome = "REJECTED", note = "Schema error in table 3" }, "maker");
        rejected.Text("submission.status").ShouldBe("REJECTED");
        rejected.Text("submission.responseNote").ShouldBe("Schema error in table 3");

        // Only a rejected submission can be resubmitted, and a fresh one has nothing to replace.
        await Fails("", new { submissionType = "ANNUAL_RETURN", regulator = "BOG", periodLabel = "2026", resubmitOf = Guid.NewGuid() }, "maker", HttpStatusCode.NotFound, "CMP-ERR-NOT-FOUND");
        var fresh = await Create();
        await Fails("", new { submissionType = "ANNUAL_RETURN", regulator = "BOG", periodLabel = "2026", resubmitOf = fresh }, "maker", HttpStatusCode.UnprocessableEntity, "CMP-ERR-SUBMISSION-STATE");

        var next = await Ok("", new { submissionType = "ANNUAL_RETURN", regulator = "BOG", periodLabel = "2026", resubmitOf = id }, "maker", HttpStatusCode.OK);
        next.Text("submission.version").ShouldBe("2");
        next.Text("submission.previousVersionId").ShouldBe(id);
        next.Text("submission.status").ShouldBe("IN_PREPARATION");
        next.Text("submission.number").ShouldEndWith("-V2");
        next!["submission"]!["evidence"]!.AsArray().Count.ShouldBe(1);

        var (old, oldBody) = await Call(HttpMethod.Get, $"{Root}/{id}", "maker");
        old.StatusCode.ShouldBe(HttpStatusCode.OK, oldBody?.ToJsonString());
        oldBody.Text("submission.status").ShouldBe("RESUBMITTED");
        oldBody.Text("submission.filingReference").ShouldBe("REF-1");

        // A version is replaced once.
        await Fails("", new { submissionType = "ANNUAL_RETURN", regulator = "BOG", periodLabel = "2026", resubmitOf = id }, "maker", HttpStatusCode.UnprocessableEntity, "CMP-ERR-SUBMISSION-STATE");
    }

    [Fact]
    public async Task Approval_needs_content_a_different_checker_and_a_rejection_returns_to_preparation()
    {
        var id = await Create();
        await Fails("approve", new { id, decision = "REQUEST" }, "maker", HttpStatusCode.UnprocessableEntity, "CMP-ERR-SUBMISSION-STATE");
        await Ok("attach-content", new { id, kind = "EVIDENCE_PACK", refId = Guid.NewGuid().ToString() }, "helper");
        await Ok("approve", new { id, decision = "REQUEST" }, "maker");

        // The preparer, the person who linked content and the requester are all makers.
        await Fails("approve", new { id, decision = "APPROVE" }, "helper", HttpStatusCode.Forbidden, "CMP-ERR-APPROVAL-SAME-USER");
        await Fails("approve", new { id, decision = "REJECT" }, "maker", HttpStatusCode.Forbidden, "CMP-ERR-APPROVAL-SAME-USER");

        var rejected = await Ok("approve", new { id, decision = "REJECT", comment = "Figures missing" }, "checker");
        rejected.Text("submission.status").ShouldBe("IN_PREPARATION");
        rejected.Text("submission.approvalStatus").ShouldBe("NONE");

        // Editable again; the next request asks again and can be approved.
        await Ok("attach-content", new { id, kind = "OTHER", refId = "more" }, "maker");
        await Ok("approve", new { id, decision = "REQUEST" }, "maker");
        var approved = await Ok("approve", new { id, decision = "APPROVE" }, "checker");
        approved.Text("submission.approvalStatus").ShouldBe("APPROVED");

        // A response needs a filing first.
        await Fails("record-response", new { id, outcome = "ACKNOWLEDGED" }, "maker", HttpStatusCode.UnprocessableEntity, "CMP-ERR-SUBMISSION-STATE");
    }

    [Fact]
    public async Task Overdue_is_flagged_and_other_legal_entities_roles_and_ids_are_refused()
    {
        var id = await Create(extra: new { submissionType = "SOLVENCY_QRT", regulator = "BOG", periodLabel = "2026-Q1", periodFrom = "2026-01-01", periodTo = "2026-03-31", dueDate = "2026-05-20" });
        var (got, body) = await Call(HttpMethod.Get, $"{Root}/{id}", "maker");
        got.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["submission"]!["overdue"]!.GetValue<bool>().ShouldBeTrue();
        body.Text("submission.dueLegalStatus").ShouldBe("Unverified");

        var (nope, _) = await Call(HttpMethod.Get, $"{Root}/{Guid.NewGuid()}", "maker");
        nope.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var (bad, _) = await Call(HttpMethod.Get, $"{Root}/not-a-guid", "maker");
        bad.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var (forbidden, _) = await Call(HttpMethod.Post, Root, "maker", new { submissionType = "ANNUAL_RETURN", regulator = "BOG", periodLabel = "2026" }, "Staff.Billing");
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var (invalid, invalidBody) = await Call(HttpMethod.Post, Root, "maker", new { submissionType = "", regulator = "BOG", periodLabel = "2026" });
        invalid.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, invalidBody?.ToJsonString());
    }

    [Fact]
    public async Task The_archive_evidence_goes_through_the_DOC_contract()
    {
        var id = await Create(extra: new { submissionType = "ANNUAL_RETURN", regulator = "BOG", periodLabel = "2020", periodFrom = "2020-01-01", periodTo = "2020-12-31" });

        // No document exists in the period: DOC answers NOT-FOUND and nothing is linked.
        var (pack, packBody) = await Call(HttpMethod.Post, $"{Root}/evidence-pack", "maker", new { id });
        pack.StatusCode.ShouldBe(HttpStatusCode.NotFound, packBody?.ToJsonString());
        packBody.Text("code").ShouldStartWith("DOC-ERR-");
        (await CountAsync($"SELECT count(*) FROM cmp.submission WHERE submission_id = '{id}' AND evidence = '[]'::jsonb")).ShouldBe(1);

        var (report, reportBody) = await Call(HttpMethod.Get, $"{Root}/archive-completeness?from=2020-01-01&to=2020-12-31", "maker");
        report.StatusCode.ShouldBe(HttpStatusCode.OK, reportBody?.ToJsonString());
        reportBody!["periods"]!.AsArray().ShouldNotBeNull();
        reportBody["truncated"]!.GetValue<bool>().ShouldBeFalse();

        var (badRange, _) = await Call(HttpMethod.Get, $"{Root}/archive-completeness?from=2020-12-31&to=2020-01-01", "maker");
        badRange.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }
}
