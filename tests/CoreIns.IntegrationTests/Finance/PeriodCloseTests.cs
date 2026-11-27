using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Party;
using CoreIns.IntegrationTests.Product;
using Npgsql;
using static CoreIns.IntegrationTests.Finance.FinanceSlice;

namespace CoreIns.IntegrationTests.Finance;

/// <summary>BO-FIN-04: the period close checklist and OPEN, SOFT_CLOSED, LOCKED state machine with its two-person rules.</summary>
public sealed class PeriodCloseTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Period = "2026-11";
    private const string Day = "2026-11-02";
    private const string FinanceRole = "Staff.Finance";
    private static readonly string[] Tasks = ["INTAKE_EXCEPTIONS", "RECONCILIATIONS", "EARNING_RUN", "MANUAL_JOURNALS"];
    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;
    private FinanceSlice _slice = null!;
    private NpgsqlDataSource _db = null!;
    private string? _artefactHash;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _client = _factory.CreateClient();
        _slice = new FinanceSlice(_factory);
        _db = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var (response, body) = await ProductApi.ImportAsync(_client, ProductApi.Seed("MOTOR-FIN"));
        response.IsSuccessStatusCode.ShouldBeTrue(body?.ToJsonString());
        _artefactHash = body.Text("artefactHash");
        await database.ExecuteAsSuperuserAsync(
            $"DELETE FROM fin.financial_period WHERE legal_entity_id = '{ApiHostFactory.LegalEntityId}' AND period_code = '{Period}'", TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, string user, object? body = null, string roles = FinanceRole)
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

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> CloseAsync(string action, string user, string reason = "month end") =>
        SendAsync(HttpMethod.Post, $"/api/fin/v1/close/{action}", user, new { period = Period, reason });

    private async Task SignOffAllAsync(string user)
    {
        foreach (var task in Tasks)
        {
            var (response, body) = await SendAsync(HttpMethod.Post, "/api/fin/v1/close/sign-off", user, new { period = Period, task });
            response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        }
    }

    private async Task<string> StateAsync()
    {
        var (_, body) = await SendAsync(HttpMethod.Get, $"/api/fin/v1/close/tasks?period={Period}", "anna");
        return body!["state"]!.GetValue<string>();
    }

    private async Task LockAsync()
    {
        await SignOffAllAsync("anna");
        (await CloseAsync("soft-close", "anna")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CloseAsync("lock", "bob", "all clear")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Lock_is_refused_with_open_tasks_and_needs_a_second_person_than_the_soft_closer()
    {
        (await StateAsync()).ShouldBe("OPEN");
        var (soft, softBody) = await CloseAsync("soft-close", "anna");
        soft.StatusCode.ShouldBe((HttpStatusCode)422, softBody?.ToJsonString());
        softBody!["code"]!.GetValue<string>().ShouldBe("FIN-ERR-GATES-FAILED");
        (await CloseAsync("lock", "anna")).Response.StatusCode.ShouldBe((HttpStatusCode)422);

        // A reason is mandatory and the role is checked.
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/close/soft-close", "anna", new { period = Period })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/close/soft-close", "anna", new { period = Period, reason = "x" }, roles: PartyApi.Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await SignOffAllAsync("anna");
        (await CloseAsync("lock", "bob")).Response.StatusCode.ShouldBe((HttpStatusCode)422); // not soft-closed yet
        (await CloseAsync("soft-close", "anna")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StateAsync()).ShouldBe("SOFT_CLOSED");

        var (self, selfBody) = await CloseAsync("lock", "anna");
        self.StatusCode.ShouldBe((HttpStatusCode)422);
        selfBody!["code"]!.GetValue<string>().ShouldBe("FIN-ERR-APPROVAL-REQUIRED");

        var (locked, lockedBody) = await CloseAsync("lock", "bob", "all clear");
        locked.StatusCode.ShouldBe(HttpStatusCode.OK, lockedBody?.ToJsonString());
        lockedBody!["state"]!.GetValue<string>().ShouldBe("LOCKED");
        lockedBody["closedForPosting"]!.GetValue<bool>().ShouldBeTrue();
        (await ScalarAsync<long>(_db, "SELECT count(*) FROM plt.audit_event WHERE operation = 'fin.Close.lock' AND outcome = 'Succeeded' AND reason = 'all clear'")).ShouldBeGreaterThanOrEqualTo(1);

        // The period list shows it and nothing is signed off in a locked period.
        var (_, list) = await SendAsync(HttpMethod.Get, "/api/fin/v1/periods", "anna");
        list!["items"]!.AsArray().Single(i => i!["period"]!.GetValue<string>() == Period)!["state"]!.GetValue<string>().ShouldBe("LOCKED");
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/close/sign-off", "anna", new { period = Period, task = "EARNING_RUN" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_locked_period_suspends_postings_and_reopening_needs_a_second_approver()
    {
        // Both tests share the month: put it in the locked state whatever the other left behind.
        var state = await StateAsync();
        if (state == "OPEN")
        {
            await LockAsync();
        }
        else if (state == "SOFT_CLOSED")
        {
            (await CloseAsync("lock", "bob", "all clear")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var policy = Policy.New($"POL{Random.Shared.Next(100_000_000, 999_999_999)}", "MOTOR-FIN", _artefactHash!);
        await _slice.PolicyBoundAsync(policy);
        await _slice.DrainAsync();
        var account = Guid.CreateVersion7();
        var charge = Guid.CreateVersion7();
        JsonObject Charge(string side, string ledger)
        {
            var l = Line(ledger, side, "25.00", policy, account, "PREM-MTPL", "PREMIUM", "MTPL");
            l["dimensions"]!["chargeId"] = charge.ToString();
            return l;
        }

        var entry = await _slice.EntryAsync(account, "WRITTEN", Day, Charge("DEBIT", "LA-01"), Charge("CREDIT", "LA-04"));
        await _slice.DrainAsync();
        var eventId = await ScalarAsync<Guid>(_db, $"SELECT business_event_id FROM fin.business_event WHERE source_event_id = '{entry.EventId.Value}'");
        (await ScalarAsync<string>(_db, $"SELECT status || ':' || exception_reason FROM fin.business_event WHERE business_event_id = '{eventId}'")).ShouldBe("SUSPENDED:PERIOD_LOCKED");

        // Reopen: the first call only records the request; the requester cannot approve it.
        var (request, requestBody) = await CloseAsync("reopen", "anna", "late premium for October");
        request.StatusCode.ShouldBe(HttpStatusCode.OK, requestBody?.ToJsonString());
        requestBody!["state"]!.GetValue<string>().ShouldBe("LOCKED");
        requestBody["reopenRequestedBy"]!.GetValue<string>().ShouldBe("anna");
        var (selfApprove, selfBody) = await CloseAsync("reopen", "anna", "approve my own");
        selfApprove.StatusCode.ShouldBe((HttpStatusCode)422);
        selfBody!["code"]!.GetValue<string>().ShouldBe("FIN-ERR-APPROVAL-REQUIRED");
        (await StateAsync()).ShouldBe("LOCKED");

        var (approve, approveBody) = await CloseAsync("reopen", "bob", "approved");
        approve.StatusCode.ShouldBe(HttpStatusCode.OK, approveBody?.ToJsonString());
        approveBody!["state"]!.GetValue<string>().ShouldBe("OPEN");
        approveBody["tasks"]!.AsArray().All(t => t!["status"]!.GetValue<string>() == "OPEN").ShouldBeTrue();
        (await ScalarAsync<long>(_db, "SELECT count(*) FROM plt.audit_event WHERE operation = 'fin.Close.reopen' AND outcome = 'Succeeded' AND reason = 'late premium for October'")).ShouldBe(1);

        // The suspended event blocks the intake task until it is rerun into the open period.
        var (blocked, blockedBody) = await SendAsync(HttpMethod.Post, "/api/fin/v1/close/sign-off", "anna", new { period = Period, task = "INTAKE_EXCEPTIONS" });
        blocked.StatusCode.ShouldBe((HttpStatusCode)422, blockedBody?.ToJsonString());
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/intake-exceptions/rerun", "anna", new { itemIds = new[] { eventId } })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ScalarAsync<string>(_db, $"SELECT status FROM fin.business_event WHERE business_event_id = '{eventId}'")).ShouldBe("POSTED");
        (await SendAsync(HttpMethod.Post, "/api/fin/v1/close/sign-off", "anna", new { period = Period, task = "INTAKE_EXCEPTIONS" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
