using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Compliance.Clocks;
using CoreIns.Platform.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Compliance;

/// <summary>NX-TST-08: DSAR erasure edge cases (legal hold, closed cases, single extension, wrong approval) and statutory clock breaches, on a shiftable clock.</summary>
public sealed class DsarEdgeCaseTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Dpo = "Staff.ComplianceOfficer,Platform.Admin";
    private readonly ApiHostFactory _factory = new(
        database.AppConnectionString, settings: new Dictionary<string, string?> { ["Platform:Time:Mode"] = "Shiftable" });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private ShiftableClock Clock => (ShiftableClock)_factory.Services.GetRequiredService<IClock>();

    private static async Task<(HttpResponseMessage Response, JsonNode? Body)> Call(HttpClient client, HttpMethod method, string path, string user, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, Dpo);
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

    private async Task<string> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task<string> OpenAsync(HttpClient client, bool legalHold = false, bool verify = true, bool fanOut = true)
    {
        var (created, body) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar", "clerk", new { subjectPartyId = Guid.CreateVersion7(), requestType = "ERASURE", legalHold });
        created.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var id = body.Text("request.dsarId");
        if (verify)
        {
            (await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/verify-identity", "clerk", new { id, method = "ID_DOCUMENT" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        if (fanOut)
        {
            var (fan, fanBody) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/start-fan-out", "clerk", new { id });
            fan.StatusCode.ShouldBe(HttpStatusCode.OK, fanBody?.ToJsonString());
        }

        return id;
    }

    private static string ClockId(string dsarId) => $"(SELECT clock_id::text FROM cmp.clock WHERE clock_code = 'CMP_DSAR_RESPONSE' AND subject_id = '{dsarId}')";

    [Fact]
    public async Task A_legal_hold_blocks_the_purge_even_with_an_approval_and_nothing_is_erased()
    {
        using var client = _factory.CreateClient();
        var id = await OpenAsync(client, legalHold: true);
        (await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/approve", "dpo", new { id, action = "ERASE", reason = "request" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (response, body) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/erase", "dpo", new { dsarId = id });
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body?.ToJsonString());
        body.Text("code").ShouldBe("CMP-ERR-LEGAL-HOLD");
        (await ScalarAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'DSARCompleted' AND aggregate_id = '{id}'")).ShouldBe("0");
        (await Call(client, HttpMethod.Get, $"/api/cmp/v1/dsar/{id}", "dpo")).Body.Text("request.status").ShouldBe("IN_REVIEW");
    }

    [Fact]
    public async Task Approval_is_refused_before_fan_out_for_another_action_and_for_an_unknown_case()
    {
        using var client = _factory.CreateClient();
        var early = await OpenAsync(client, fanOut: false);
        var (tooEarly, tooEarlyBody) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/approve", "dpo", new { id = early, action = "ERASE", reason = "x" });
        tooEarly.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, tooEarlyBody?.ToJsonString());
        tooEarlyBody.Text("code").ShouldBe("CMP-ERR-DSAR-STATE");

        var id = await OpenAsync(client);
        (await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/approve", "dpo", new { id, action = "ERASE", reason = "x" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (other, otherBody) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/approve", "dpo2", new { id, action = "RESTRICT", reason = "y" });
        other.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, otherBody?.ToJsonString());
        otherBody.Text("code").ShouldBe("CMP-ERR-DSAR-STATE");

        var (restrict, restrictBody) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/restrict", "dpo", new { dsarId = id });
        restrict.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, restrictBody?.ToJsonString());
        restrictBody.Text("code").ShouldBe("CMP-ERR-APPROVAL-REQUIRED");

        var (missing, missingBody) = await Call(client, HttpMethod.Get, $"/api/cmp/v1/dsar/{Guid.CreateVersion7()}", "dpo");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound, missingBody?.ToJsonString());
        (await Call(client, HttpMethod.Get, "/api/cmp/v1/dsar/not-a-guid", "dpo")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_deadline_can_be_extended_only_once()
    {
        using var client = _factory.CreateClient();
        var id = await OpenAsync(client, verify: false, fanOut: false);
        var (first, firstBody) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/extend", "dpo", new { id, extension = "P2M", reason = "complex request" });
        first.StatusCode.ShouldBe(HttpStatusCode.OK, firstBody?.ToJsonString());

        var (second, secondBody) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/extend", "dpo", new { id, extension = "P1M", reason = "again" });
        second.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, secondBody?.ToJsonString());
        secondBody.Text("code").ShouldBe("CMP-ERR-EXTENSION-NOT-ALLOWED");
        (await ScalarAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'DSARExtended' AND aggregate_id = '{id}'")).ShouldBe("1");
    }

    [Fact]
    public async Task A_refused_case_is_closed_refusing_again_is_idempotent_and_further_steps_are_refused()
    {
        using var client = _factory.CreateClient();
        var id = await OpenAsync(client, verify: false, fanOut: false);
        var (refused, body) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/refuse", "dpo", new { id, reason = "manifestly unfounded" });
        refused.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("request.status").ShouldBe("REFUSED");

        (await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/refuse", "dpo", new { id, reason = "again" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var (path, payload) in new (string, object)[]
        {
            ("verify-identity", new { id, method = "ID_DOCUMENT" }),
            ("start-fan-out", new { id }),
            ("extend", new { id, extension = "P1M" }),
            ("erase", new { dsarId = id }),
        })
        {
            var (response, error) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/" + path, "dpo", payload);
            ((int)response.StatusCode).ShouldBeInRange(400, 499, path + ": " + error?.ToJsonString());
        }
    }

    [Fact]
    public async Task Breaching_the_one_month_deadline_keeps_the_case_workable_and_the_clock_stays_breached_after_the_erasure()
    {
        using var client = _factory.CreateClient();
        var id = await OpenAsync(client);
        (await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/approve", "dpo", new { id, action = "ERASE", reason = "request" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        Clock.Advance(TimeSpan.FromDays(40));
        await _factory.Services.GetRequiredService<ClockSweepRunner>().RunAsync(Ct);
        (await ScalarAsync($"SELECT status FROM cmp.clock WHERE clock_code = 'CMP_DSAR_RESPONSE' AND subject_id = '{id}'")).ShouldBe("BREACHED");
        (await ScalarAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ClockBreached' AND aggregate_id = {ClockId(id)}")).ShouldBe("1");

        // The late erasure still completes (the subject's right does not lapse) but the clock is not turned into MET.
        var (erased, erasedBody) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/erase", "dpo", new { dsarId = id });
        erased.StatusCode.ShouldBe(HttpStatusCode.OK, erasedBody?.ToJsonString());
        erasedBody.Text("request.status").ShouldBe("COMPLETED");
        (await ScalarAsync($"SELECT status FROM cmp.clock WHERE clock_code = 'CMP_DSAR_RESPONSE' AND subject_id = '{id}'")).ShouldBe("BREACHED");

        // A further sweep does not breach it twice.
        await _factory.Services.GetRequiredService<ClockSweepRunner>().RunAsync(Ct);
        (await ScalarAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ClockBreached' AND aggregate_id = {ClockId(id)}")).ShouldBe("1");
    }

    [Fact]
    public async Task Clock_commands_on_unknown_or_wrong_state_instances_are_refused_and_a_cancelled_clock_never_breaches()
    {
        using var client = _factory.CreateClient();
        Task<(HttpResponseMessage Response, JsonNode? Body)> Post(string op, object body) =>
            SendAsync(client, HttpMethod.Post, $"/api/cmp/v1/clocks/{op}", body, roles: "Platform.Admin");

        var unknown = Guid.CreateVersion7().ToString();
        foreach (var op in new[] { "pause", "resume", "stop", "extend" })
        {
            var (response, body) = await Post(op, new { instanceId = unknown, outcome = "Met", stopTime = Clock.Now.ToString(), extension = "P1M", reason = "x" });
            ((int)response.StatusCode).ShouldBeInRange(400, 499, op + ": " + body?.ToJsonString());
        }

        var subject = new { module = "BIL", type = "DelinquencyCase", id = Guid.CreateVersion7().ToString() };
        var (_, started) = await Post("start", new { clockCode = "BIL_NONPAY_NOTICE", subjectRef = subject });
        var id = started.Text("instanceId");
        var (resume, resumeBody) = await Post("resume", new { instanceId = id });
        ((int)resume.StatusCode).ShouldBeInRange(400, 499, "resume of a running clock: " + resumeBody?.ToJsonString());

        var (cancelled, cancelBody) = await Post("cancel", new { instanceId = id, reason = "raised in error" });
        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK, cancelBody?.ToJsonString());
        Clock.Advance(TimeSpan.FromDays(60));
        await _factory.Services.GetRequiredService<ClockSweepRunner>().RunAsync(Ct);
        (await ScalarAsync($"SELECT status FROM cmp.clock WHERE clock_id = '{id}'")).ShouldBe("CANCELLED");
        (await ScalarAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type IN ('ClockElapsed','ClockBreached') AND aggregate_id = '{id}'")).ShouldBe("0");
    }
}
