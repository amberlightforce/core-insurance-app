using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Underwriting.Services;
using CoreIns.Platform.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Rating.RatingTestSupport;

namespace CoreIns.IntegrationTests.Underwriting;

/// <summary>
/// BO-UW-08 on a real PostgreSQL 17 through the real Host: contingencies on a term (create, update, satisfy, waive, extend), the gate
/// they put on <c>uw.Issue.blockingStatus</c>, the due-date job (<c>ContingencyOverdue</c> once) and the consequences of an overdue one.
/// </summary>
public sealed class ContingencyTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Underwriter = "Staff.Underwriter";
    private const string Manager = "Staff.UnderwritingManager";
    private const string Anna = "uw-anna";
    private const string Boss = "uw-boss";

    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ShiftableClock Clock => (ShiftableClock)_factory.Services.GetRequiredService<IClock>();

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString, settings: new Dictionary<string, string?> { ["Platform:Time:Mode"] = "Shiftable" });
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(
        HttpMethod method, string path, string user = Anna, string roles = Underwriter, object? body = null)
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

        var response = await _client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
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

    private Task<long> EventsAsync(string type, string contingencyId) =>
        CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = '{type}' AND business_keys->>'contingencyId' = '{contingencyId}'");

    private static string InDays(int days) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(days)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<JsonNode> CreateAsync(Guid? job, int dueInDays = 5, string? consequence = null, string? issueType = null, Guid? term = null, string blockingPoint = "PRE_ISSUE")
    {
        var (response, body) = await SendAsync(
            HttpMethod.Post, "/api/uw/v1/contingencies",
            body: new
            {
                termRef = term ?? Guid.CreateVersion7(),
                jobRef = job,
                contingencyType = "PROOF_OF_NCB",
                description = "No-claims bonus certificate",
                customerText = "Please send your no-claims certificate.",
                dueDate = InDays(dueInDays),
                owner = "customer",
                blockingPoint,
                consequenceCode = consequence,
                consequenceIssueType = issueType,
            });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return body!["contingency"]!;
    }

    private async Task<bool> BlockedAsync(Guid job, string point)
    {
        var (response, body) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/issues/blocking-status?jobRef={job}&blockingPoint={point}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["blocked"]!.GetValue<bool>();
    }

    [Fact]
    public async Task An_open_pre_issue_contingency_blocks_the_job_until_it_is_satisfied()
    {
        var job = await AcceptedJobAsync();
        (await BlockedAsync(job, "PRE_ISSUE")).ShouldBeFalse();

        var contingency = await CreateAsync(job);
        var id = contingency.Text("id");
        contingency.Text("status").ShouldBe("Open");
        contingency.Text("blockingPoint").ShouldBe("PRE_ISSUE");
        (await EventsAsync("ContingencyCreated", id)).ShouldBe(1);

        // It gates issue, not quote or bind.
        (await BlockedAsync(job, "PRE_ISSUE")).ShouldBeTrue();
        (await BlockedAsync(job, "PRE_BIND")).ShouldBeFalse();
        var (_, status) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/issues/blocking-status?jobRef={job}&blockingPoint=PRE_ISSUE");
        status!["contingencies"]!.AsArray().Single()!.Text("contingencyId").ShouldBe(id);

        var (satisfied, body) = await SendAsync(
            HttpMethod.Post, "/api/uw/v1/contingencies/satisfy", body: new { contingencyId = id, evidenceRef = "DOC-123", note = "Certificate received." });
        satisfied.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("contingency.status").ShouldBe("Satisfied");
        (await BlockedAsync(job, "PRE_ISSUE")).ShouldBeFalse();
        (await EventsAsync("ContingencyResolved", id)).ShouldBe(1);

        // A resolved contingency does not change any more.
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies/satisfy", body: new { contingencyId = id })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Patch, $"/api/uw/v1/contingencies/{id}", body: new { owner = "someone" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Creation_validates_and_needs_the_permission()
    {
        var term = Guid.CreateVersion7();
        var good = new { termRef = term, contingencyType = "PROOF_OF_NCB", description = "NCB", dueDate = InDays(3), owner = "customer" };

        (await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies", roles: "Staff.Billing", body: good)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies", body: good)).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies", body: new { termRef = term, contingencyType = "not a code", description = "NCB", dueDate = InDays(3), owner = "customer" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies", body: new { termRef = term, contingencyType = "PROOF_OF_NCB", description = " ", dueDate = InDays(3), owner = "customer" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies", body: new { termRef = term, contingencyType = "PROOF_OF_NCB", description = "NCB", dueDate = InDays(-1), owner = "customer" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // ADD_ISSUE names an active issue type and needs a job.
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies", body: new { termRef = term, contingencyType = "PROOF_OF_NCB", description = "NCB", dueDate = InDays(3), owner = "customer", consequenceCode = "ADD_ISSUE" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies", body: new { termRef = term, jobRef = Guid.CreateVersion7(), contingencyType = "PROOF_OF_NCB", description = "NCB", dueDate = InDays(3), owner = "customer", consequenceCode = "ADD_ISSUE", consequenceIssueType = "NO_SUCH_TYPE" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Waiving_needs_a_manager_and_a_reason_and_extending_moves_the_date()
    {
        var job = await AcceptedJobAsync();
        var id = (await CreateAsync(job, dueInDays: 2)).Text("id");

        (await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies/waive", body: new { contingencyId = id, reason = "Known customer." })).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies/waive", Boss, Manager, new { contingencyId = id, reason = " " })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Extending must move the date later.
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies/extend", body: new { contingencyId = id, newDueDate = InDays(1), reason = "Earlier." })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (extended, extendedBody) = await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies/extend", body: new { contingencyId = id, newDueDate = InDays(9), reason = "Customer abroad." });
        extended.StatusCode.ShouldBe(HttpStatusCode.OK, extendedBody?.ToJsonString());
        extendedBody.Text("contingency.dueDate").ShouldBe(InDays(9));

        var (patched, patchedBody) = await SendAsync(HttpMethod.Patch, $"/api/uw/v1/contingencies/{id}", body: new { owner = "producer", recordVersion = 1 });
        patched.StatusCode.ShouldBe(HttpStatusCode.Conflict, patchedBody?.ToJsonString()); // version 1 was read before the extension
        var (patchedAgain, again) = await SendAsync(HttpMethod.Patch, $"/api/uw/v1/contingencies/{id}", body: new { owner = "producer" });
        patchedAgain.StatusCode.ShouldBe(HttpStatusCode.OK, again?.ToJsonString());
        again.Text("contingency.owner").ShouldBe("producer");

        var (waived, waivedBody) = await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies/waive", Boss, Manager, new { contingencyId = id, reason = "Known customer." });
        waived.StatusCode.ShouldBe(HttpStatusCode.OK, waivedBody?.ToJsonString());
        waivedBody.Text("contingency.status").ShouldBe("Waived");
        (await BlockedAsync(job, "PRE_ISSUE")).ShouldBeFalse();
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies/waive", Boss, Manager, new { contingencyId = id, reason = "Again." })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies/waive", Boss, Manager, new { contingencyId = Guid.CreateVersion7(), reason = "Missing." })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_list_filters_by_term_and_status_and_gives_the_customer_their_own_text()
    {
        var term = Guid.CreateVersion7();
        var first = await CreateAsync(null, term: term);
        var second = await CreateAsync(null, term: term);
        await CreateAsync(null); // another term
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies/satisfy", body: new { contingencyId = second.Text("id") })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (_, all) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/contingencies?termRef={term}");
        all!["items"]!.AsArray().Count.ShouldBe(2);

        var (_, open) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/contingencies?termRef={term}&filters=status%3DOpen");
        open!["items"]!.AsArray().Single()!.Text("id").ShouldBe(first.Text("id"));

        var (_, paged) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/contingencies?termRef={term}&limit=1");
        paged!["items"]!.AsArray().Count.ShouldBe(1);
        paged["nextCursor"].ShouldNotBeNull();
        var (_, next) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/contingencies?termRef={term}&limit=1&cursor={Uri.EscapeDataString(paged.Text("nextCursor"))}");
        next!["items"]!.AsArray().Single()!.Text("id").ShouldNotBe(paged["items"]![0]!.Text("id"));

        var (_, customer) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/contingencies?termRef={term}&audience=CUSTOMER&filters=status%3DOpen");
        var item = customer!["items"]!.AsArray().Single()!;
        item.Text("description").ShouldBe("Please send your no-claims certificate.");
        item.Text("owner").ShouldBeEmpty();

        (await SendAsync(HttpMethod.Get, "/api/uw/v1/contingencies?filters=bogus%3D1")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Get, "/api/uw/v1/contingencies", roles: "Staff.Billing")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_due_date_job_marks_a_contingency_overdue_once_and_extending_clears_it()
    {
        var job = await AcceptedJobAsync();
        var id = (await CreateAsync(job, dueInDays: 2)).Text("id");

        // Not yet due: nothing of this job to mark.
        await _factory.Services.GetRequiredService<ContingencySweepRunner>().RunAsync(Ct);
        (await EventsAsync("ContingencyOverdue", id)).ShouldBe(0);

        Clock.Advance(TimeSpan.FromDays(4));
        var runner = _factory.Services.GetRequiredService<ContingencySweepRunner>();
        (await runner.RunAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);
        (await runner.RunAsync(Ct)).ShouldBe(0); // idempotent
        (await EventsAsync("ContingencyOverdue", id)).ShouldBe(1);

        var (_, overdue) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/contingencies?filters=overdue%3Dtrue,jobRef%3D{job}");
        overdue!["items"]!.AsArray().Single()!["overdue"]!.GetValue<bool>().ShouldBeTrue();
        (await BlockedAsync(job, "PRE_ISSUE")).ShouldBeTrue(); // overdue is still open and still blocks

        var newDue = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var (extended, body) = await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies/extend", body: new { contingencyId = id, newDueDate = newDue, reason = "More time." });
        extended.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["contingency"]!["overdue"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task An_overdue_consequence_adds_an_issue_or_cancels_the_quote_and_stops_blocking()
    {
        var addJob = await AcceptedJobAsync();
        var cancelJob = await AcceptedJobAsync();
        var add = (await CreateAsync(addJob, dueInDays: 2, consequence: "ADD_ISSUE", issueType: "VEHICLE_AGE_REFERRAL")).Text("id");
        var cancel = (await CreateAsync(cancelJob, dueInDays: 2, consequence: "CANCEL_QUOTE")).Text("id");
        var none = (await CreateAsync(addJob, dueInDays: 2)).Text("id");

        // Not overdue yet, and only a manager may apply a consequence.
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies/apply-consequence", Boss, Manager, new { contingencyId = add })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies/apply-consequence", body: new { contingencyId = add })).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        Clock.Advance(TimeSpan.FromDays(4));

        // A contingency without a consequence has nothing to apply.
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies/apply-consequence", Boss, Manager, new { contingencyId = none })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var (applied, body) = await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies/apply-consequence", Boss, Manager, new { contingencyId = add });
        applied.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("contingency.status").ShouldBe("ConsequenceApplied");
        (await EventsAsync("ContingencyResolved", add)).ShouldBe(1);
        (await CountAsync($"SELECT count(*) FROM uw.issue WHERE job_id = '{addJob}' AND issue_type = 'VEHICLE_AGE_REFERRAL' AND rule_id = 'MANUAL'")).ShouldBe(1);

        var (cancelled, cancelBody) = await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies/apply-consequence", Boss, Manager, new { contingencyId = cancel });
        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK, cancelBody?.ToJsonString());
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ContingencyResolved' AND business_keys->>'contingencyId' = '{cancel}' AND payload::text LIKE '%CONSEQUENCE_APPLIED%' AND payload::text LIKE '%{cancelJob}%'")).ShouldBe(1);
        (await BlockedAsync(cancelJob, "PRE_ISSUE")).ShouldBeFalse();
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/contingencies/apply-consequence", Boss, Manager, new { contingencyId = cancel })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }
}
