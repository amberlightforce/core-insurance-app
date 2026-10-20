using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.SharedKernel.Dsar;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Compliance;

/// <summary>SL6-CMP-DSAR main paths through the real Host: fan-out over the registered participants, approval, erase, completion once.</summary>
public sealed class DsarCaseTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private const string Dpo = "Staff.ComplianceOfficer";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A WRK stand-in: PART-A style EXPORTED detail on export, an erased class on erase.</summary>
    private sealed class FakeWorkParticipant : IDsarParticipant
    {
        public string Module => "WRK";

        public int Erased { get; private set; }

        public Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default) =>
            Task.FromResult(new DsarModuleResult(
                Module, [new DsarRecordOutcome("ACTIVITY", DsarOutcomeKind.NotHeld, 2, Detail: "EXPORTED")], """{"activities":[1,2]}"""));

        public Task<DsarModuleResult> RestrictAsync(SubjectRef subject, string reason, string legalBasis, DateOnly? retentionEnd, CancellationToken ct = default) =>
            Task.FromResult(new DsarModuleResult(Module, [DsarRecordOutcome.Restricted("ACTIVITY", legalBasis, 2)]));

        public Task<DsarModuleResult> EraseAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
        {
            Erased++;
            return Task.FromResult(new DsarModuleResult(Module, [new DsarRecordOutcome("ACTIVITY", DsarOutcomeKind.Erased, 2)]));
        }
    }

    /// <param name="dropRealWork">Removes the real WRK participant (NX-HD-03) so WRK reads as not built.</param>
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host(IDsarParticipant? participant, bool dropRealWork = false)
    {
        var factory = new ApiHostFactory(database.AppConnectionString);
        return participant is null && !dropRealWork
            ? factory
            : factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            {
                // A stand-in replaces the real WRK participant; the engine refuses duplicate modules.
                foreach (var real in s.Where(d => d.ServiceType == typeof(IDsarParticipant) && d.ImplementationType?.Name == "WorkDsarParticipant").ToList())
                {
                    s.Remove(real);
                }

                if (participant is not null)
                {
                    s.AddSingleton(participant);
                }
            }));
    }

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

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<string> OpenVerifiedCase(HttpClient client, string type)
    {
        var (created, body) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar", "clerk", new { subjectPartyId = Guid.CreateVersion7(), requestType = type });
        created.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var id = body.Text("request.dsarId");
        var (verified, vbody) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/verify-identity", "clerk", new { id, method = "ID_DOCUMENT" });
        verified.StatusCode.ShouldBe(HttpStatusCode.OK, vbody?.ToJsonString());
        return id;
    }

    [Fact]
    public async Task Erasure_fans_out_waits_for_a_second_dpo_and_completes_once()
    {
        var work = new FakeWorkParticipant();
        await using var factory = Host(work);
        using var client = factory.CreateClient();

        var (created, body) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar", "clerk", new { subjectPartyId = Guid.CreateVersion7(), requestType = "ERASURE" });
        created.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var id = body.Text("request.dsarId");
        body.Text("request.status").ShouldBe("RECEIVED");

        // The one-month response clock is running from receipt.
        (await CountAsync($"SELECT count(*) FROM cmp.clock WHERE clock_code = 'CMP_DSAR_RESPONSE' AND subject_id = '{id}' AND status IN ('RUNNING','WARNED')")).ShouldBe(1);
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'DSARReceived' AND aggregate_id = '{id}'")).ShouldBe(1);

        // No fan-out before the identity is verified.
        var (early, earlyBody) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/start-fan-out", "clerk", new { id });
        early.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, earlyBody?.ToJsonString());
        await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/verify-identity", "clerk", new { id, method = "ID_DOCUMENT" });

        var (fan, fanBody) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/start-fan-out", "clerk", new { id });
        fan.StatusCode.ShouldBe(HttpStatusCode.OK, fanBody?.ToJsonString());
        fanBody.Text("request.status").ShouldBe("IN_REVIEW");
        var tasks = fanBody!["tasks"]!.AsArray();
        tasks.Count.ShouldBe(15);
        tasks.Select(t => t!["module"]!.GetValue<string>()).ShouldBe(["PTY", "POL", "BIL", "CLM", "UW", "RI", "DOC", "CHN", "WRK", "PLT", "FIN", "RAT", "DAT", "MIG", "CMP"]);
        tasks.Where(t => t!["status"]!.GetValue<string>() != "DONE").ShouldBeEmpty(fanBody.ToJsonString());

        // Erase waits for the approval; the creator cannot approve their own case.
        var (blocked, blockedBody) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/erase", "clerk", new { dsarId = id });
        blocked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, blockedBody?.ToJsonString());
        work.Erased.ShouldBe(0);
        var (same, sameBody) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/approve", "clerk", new { id, action = "ERASE", reason = "subject request" });
        same.StatusCode.ShouldBe(HttpStatusCode.Forbidden, sameBody?.ToJsonString());
        var (approved, approvedBody) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/approve", "dpo", new { id, action = "ERASE", reason = "subject request" });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, approvedBody?.ToJsonString());
        approvedBody.Text("request.approvedBy").ShouldBe("dpo");

        var (erased, erasedBody) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/erase", "dpo", new { dsarId = id });
        erased.StatusCode.ShouldBe(HttpStatusCode.OK, erasedBody?.ToJsonString());
        erasedBody.Text("request.status").ShouldBe("COMPLETED");
        work.Erased.ShouldBe(1);
        var items = erasedBody!["perItemOutcome"]!.AsArray();
        items.Any(i => i!["module"]!.GetValue<string>() == "WRK" && i["kind"]!.GetValue<string>() == "ERASED").ShouldBeTrue();
        items.Any(i => i!["module"]!.GetValue<string>() == "DAT").ShouldBeTrue();
        // Regulated classes are never erased: every billing class is restricted with the retention end unset (F-404).
        items.Where(i => i!["module"]!.GetValue<string>() == "BIL" && i["kind"]!.GetValue<string>() == "RESTRICTED")
            .All(i => i!["retentionEnd"]!.GetValue<string>() == "UNSET_F404").ShouldBeTrue();

        // Repeating the call changes nothing: one completion event, the clock stopped as met.
        var (again, _) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/erase", "dpo", new { dsarId = id });
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        work.Erased.ShouldBe(1);
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'DSARCompleted' AND aggregate_id = '{id}'")).ShouldBe(1);
        (await CountAsync($"SELECT count(*) FROM cmp.clock WHERE clock_code = 'CMP_DSAR_RESPONSE' AND subject_id = '{id}' AND status = 'MET'")).ShouldBe(1);

        var (got, gotBody) = await Call(client, HttpMethod.Get, $"/api/cmp/v1/dsar/{id}", "dpo");
        got.StatusCode.ShouldBe(HttpStatusCode.OK);
        gotBody.Text("request.status").ShouldBe("COMPLETED");
    }

    [Fact]
    public async Task A_participant_that_is_not_built_blocks_completion_and_clears_on_retry()
    {
        await using var factory = Host(null, dropRealWork: true);
        using var client = factory.CreateClient();
        var id = await OpenVerifiedCase(client, "ACCESS");

        var (fan, fanBody) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/start-fan-out", "clerk", new { id });
        fan.StatusCode.ShouldBe(HttpStatusCode.OK, fanBody?.ToJsonString());
        var wrk = fanBody!["tasks"]!.AsArray().Single(t => t!["module"]!.GetValue<string>() == "WRK")!;
        wrk["status"]!.GetValue<string>().ShouldBe("NOT_BUILT");

        // Export compiles the package but the case cannot auto-complete.
        var (exported, exportedBody) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/export", "dpo", new { dsarId = id });
        exported.StatusCode.ShouldBe(HttpStatusCode.OK, exportedBody?.ToJsonString());
        exportedBody.Text("request.status").ShouldBe("IN_REVIEW");
        exportedBody!["request"]!["blockers"]!.AsArray().Select(b => b!.GetValue<string>()).ShouldContain("NOT_BUILT:WRK");
        exportedBody["package"]!["index"]!.AsArray().Count.ShouldBeGreaterThan(0);
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'DSARCompleted' AND aggregate_id = '{id}'")).ShouldBe(0);

        // Retrying while still not built changes nothing.
        var (retry, retryBody) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/retry-task", "dpo", new { id, taskId = wrk["taskId"]!.GetValue<string>() });
        retry.StatusCode.ShouldBe(HttpStatusCode.OK, retryBody?.ToJsonString());
        retryBody.Text("request.status").ShouldBe("IN_REVIEW");
    }

    [Fact]
    public async Task Access_export_normalises_exported_outcomes_and_honours_third_party_redaction()
    {
        var work = new FakeWorkParticipant();
        await using var factory = Host(work);
        using var client = factory.CreateClient();
        var id = await OpenVerifiedCase(client, "ACCESS");
        var (_, fanBody) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/start-fan-out", "clerk", new { id });
        var wrk = fanBody!["tasks"]!.AsArray().Single(t => t!["module"]!.GetValue<string>() == "WRK")!;

        var (decided, decidedBody) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/decide-item", "dpo", new { id, taskId = wrk["taskId"]!.GetValue<string>(), redactThirdParty = true, note = "contains a third party" });
        decided.StatusCode.ShouldBe(HttpStatusCode.OK, decidedBody?.ToJsonString());

        var (exported, body) = await Call(client, HttpMethod.Post, "/api/cmp/v1/dsar/export", "dpo", new { dsarId = id });
        exported.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("request.status").ShouldBe("COMPLETED");
        var index = body!["package"]!["index"]!.AsArray();
        index.Any(i => i!["module"]!.GetValue<string>() == "WRK" && i["exported"]!.GetValue<bool>()).ShouldBeTrue("Detail EXPORTED counts as exported");
        var entries = body["package"]!["entries"]!.AsArray();
        entries.Single(e => e!["module"]!.GetValue<string>() == "WRK")!["redacted"]!.GetValue<bool>().ShouldBeTrue();
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'DSARCompleted' AND aggregate_id = '{id}'")).ShouldBe(1);
    }
}
