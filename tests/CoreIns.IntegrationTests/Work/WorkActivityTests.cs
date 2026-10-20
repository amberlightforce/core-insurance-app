using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts.Events;
using CoreIns.Modules.Work.Jobs;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Dsar;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Work;

/// <summary>
/// NX-HD-05: the WRK core over HTTP on PostgreSQL 17: create from a pattern, my tasks / team queue, claim, complete, getNext, the SLA sweep
/// (SLABreached), the pack-rollback review activity (D-SL5-10) and the DSAR participant reporting real task data.
/// </summary>
public sealed class WorkActivityTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Manager = "Staff.UnderwritingManager";
    private const string Admin = "Platform.Admin";
    private const string Psr = "WRK-POL-NONPAY-PSR-EXCEPTION";

    private readonly ApiHostFactory _factory = new(database.AppConnectionString);
    private HttpClient? _client;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client => _client ??= _factory.CreateClient();

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task An_unknown_pattern_or_a_module_that_may_not_raise_it_creates_nothing()
    {
        var unknown = await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities", CreateBody("WRK-NO-SUCH-PATTERN", "POL", Guid.NewGuid().ToString()), Admin);
        unknown.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        unknown.Body.Text("code").ShouldBe("WRK-ERR-PATTERN-NOT-APPLICABLE");

        // The PSR exception pattern is POL's; CLM may not raise it.
        var wrongModule = await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities", CreateBody(Psr, "CLM", Guid.NewGuid().ToString()), Admin);
        wrongModule.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        wrongModule.Body.Text("code").ShouldBe("WRK-ERR-PATTERN-NOT-APPLICABLE");

        // Creating is for managers: an underwriter is forbidden.
        (await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities", CreateBody(Psr, "POL", Guid.NewGuid().ToString()), "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_psr_activity_is_created_in_the_queue_claimed_and_completed_by_one_assignee()
    {
        var objectId = Guid.NewGuid().ToString();
        var created = await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities", CreateBody(Psr, "POL", objectId, cause: "psr-1"), Admin);
        created.Response.StatusCode.ShouldBe(HttpStatusCode.OK, created.Body?.ToJsonString());
        var id = created.Body.Text("activityId");
        created.Body.Text("assignment.assigneeType").ShouldBe("QUEUE");
        created.Body.Text("assignment.assigneeId").ShouldBe("PSR");
        created.Body.Text("number").ShouldStartWith("ACT-");

        // The same open pattern, object and cause is a duplicate.
        var duplicate = await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities", CreateBody(Psr, "POL", objectId, cause: "psr-1"), Admin);
        duplicate.Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        duplicate.Body.Text("code").ShouldBe("WRK-ERR-DUPLICATE");

        // A billing clerk is not in the PSR queue's roles: it neither lists nor reads the activity.
        (await ListAsync("scope:team", "Staff.Billing", "carol")).Select(i => i.Text("id")).ShouldNotContain(id);
        (await SendAsync(HttpMethod.Get, $"/api/wrk/v1/activities/{id}", null, "Staff.Billing", "carol")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities/assign", new { id }, "Staff.Billing", "carol")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The underwriter sees it in the team queue and the unassigned queue, but not in "my tasks".
        (await ListAsync("scope:queue", Manager, "alice")).Select(i => i.Text("id")).ShouldContain(id);
        (await ListAsync("scope:mine", Manager, "alice")).Select(i => i.Text("id")).ShouldNotContain(id);

        // Claim it; now it is alice's.
        var claim = await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities/assign", new { id, rulesOrMe = "me" }, Manager, "alice");
        claim.Response.StatusCode.ShouldBe(HttpStatusCode.OK, claim.Body?.ToJsonString());
        claim.Body.Text("assignment.assignee.type").ShouldBe("USER");
        claim.Body.Text("assignment.assignee.id").ShouldBe("alice");
        (await ListAsync("scope:mine", Manager, "alice")).Select(i => i.Text("id")).ShouldContain(id);
        (await ListAsync("scope:queue", Manager, "alice")).Select(i => i.Text("id")).ShouldNotContain(id);

        // Someone else with the same role cannot take it over (not a manager of their own: use the plain underwriter role) and cannot close it.
        var steal = await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities/assign", new { id }, "Staff.Underwriter", "bob");
        steal.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        steal.Body.Text("code").ShouldBe("WRK-ERR-PERMISSION");
        (await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities/complete", new { id, outcome = "HOLD" }, "Staff.Underwriter", "bob")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Completing needs an outcome; then it is closed once.
        var noOutcome = await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities/complete", new { id }, Manager, "alice");
        noOutcome.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        noOutcome.Body.Text("code").ShouldBe("WRK-ERR-OUTCOME-REQUIRED");
        var done = await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities/complete", new { id, outcome = "HOLD", note = "Customer is vulnerable; keep cover." }, Manager, "alice");
        done.Response.StatusCode.ShouldBe(HttpStatusCode.OK, done.Body?.ToJsonString());
        done.Body.Text("activity.state").ShouldBe("COMPLETED");
        done.Body.Text("activity.outcome").ShouldBe("HOLD");
        (await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities/complete", new { id, outcome = "HOLD" }, Manager, "alice")).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Closing freed the duplicate key and the events were staged in the same transaction.
        (await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities", CreateBody(Psr, "POL", objectId, cause: "psr-1"), Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{id}' AND event_type IN ('ActivityCreated', 'ActivityCompleted')")).ShouldBe(2);
    }

    [Fact]
    public async Task Skip_and_cancel_need_a_reason_and_the_assignee_or_a_manager()
    {
        var created = await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities", CreateBody(Psr, "POL", Guid.NewGuid().ToString()), Admin);
        var id = created.Body.Text("activityId");
        (await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities/assign", new { id }, "Staff.Underwriter", "bob")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var noReason = await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities/skip", new { id }, "Staff.Underwriter", "bob");
        noReason.Body.Text("code").ShouldBe("WRK-ERR-NOTE-REQUIRED");
        var skipped = await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities/skip", new { id, reason = "Duplicate of another review" }, "Staff.Underwriter", "bob");
        skipped.Response.StatusCode.ShouldBe(HttpStatusCode.OK, skipped.Body?.ToJsonString());
        skipped.Body.Text("activity.state").ShouldBe("SKIPPED");

        // A manager may cancel an activity somebody else holds.
        var other = (await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities", CreateBody(Psr, "POL", Guid.NewGuid().ToString()), Admin)).Body.Text("activityId");
        (await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities/assign", new { id = other }, "Staff.Underwriter", "bob")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cancelled = await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities/cancel", new { id = other, reason = "Policy withdrawn" }, Manager, "alice");
        cancelled.Response.StatusCode.ShouldBe(HttpStatusCode.OK, cancelled.Body?.ToJsonString());
        cancelled.Body.Text("activity.state").ShouldBe("CANCELLED");
    }

    [Fact]
    public async Task GetNext_claims_the_most_urgent_activity_first_and_an_empty_queue_is_404()
    {
        var normal = (await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities", CreateBody("WRK-BIL-NONPAY-NOTICE-RETURNED", "BIL", Guid.NewGuid().ToString()), Admin)).Body.Text("activityId");
        var high = (await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities", CreateBody("WRK-BIL-NONPAY-PSR-HOLD", "BIL", Guid.NewGuid().ToString()), Admin)).Body.Text("activityId");

        var claimed = new List<string>();
        for (var i = 0; i < 50; i++)
        {
            var next = await SendAsync(HttpMethod.Post, "/api/wrk/v1/queues/get-next", new { }, Manager, "dora");
            if (next.Response.StatusCode == HttpStatusCode.NotFound)
            {
                next.Body.Text("code").ShouldBe("WRK-ERR-QUEUE-EMPTY");
                break;
            }

            next.Response.StatusCode.ShouldBe(HttpStatusCode.OK, next.Body?.ToJsonString());
            next.Body.Text("assignedActivity.assignee.id").ShouldBe("dora");
            claimed.Add(next.Body.Text("assignedActivity.id"));
        }

        claimed.ShouldContain(high);
        claimed.ShouldContain(normal);
        claimed.IndexOf(high).ShouldBeLessThan(claimed.IndexOf(normal));
        (await SendAsync(HttpMethod.Post, "/api/wrk/v1/queues/get-next", new { }, Manager, "dora")).Body.Text("code").ShouldBe("WRK-ERR-QUEUE-EMPTY");
    }

    [Fact]
    public async Task The_sla_sweep_publishes_SLABreached_once_for_an_open_activity_past_its_target()
    {
        var id = (await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities", CreateBody(Psr, "POL", Guid.NewGuid().ToString()), Admin)).Body.Text("activityId");
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using (var command = dataSource.CreateCommand($"UPDATE wrk.activity SET target_at = now() - interval '90 minutes' WHERE activity_id = '{id}'"))
        {
            await command.ExecuteNonQueryAsync(Ct);
        }

        var runner = _factory.Services.GetRequiredService<SlaSweepRunner>();
        (await runner.RunAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);
        (await runner.RunAsync(Ct)).ShouldBe(0); // marked: no second event

        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{id}' AND event_type = 'SLABreached'")).ShouldBe(1);
        var read = await SendAsync(HttpMethod.Get, $"/api/wrk/v1/activities/{id}", null, Admin, "root");
        read.Body.Text("items.0.activity.slaBreached").ShouldBe("true");
        (await ListAsync("scope:team,overdue:true", Manager, "alice")).Select(i => i.Text("id")).ShouldContain(id);
    }

    [Fact]
    public async Task A_pack_rollback_raises_one_review_activity_in_the_rollback_queue_and_redelivery_adds_nothing()
    {
        var activation = Guid.CreateVersion7();
        await DeliverRollbackAsync(activation);
        await DeliverRollbackAsync(activation);

        var items = await ListAsync("scope:queue,queue:PACK-ROLLBACK-REVIEW,pattern:WRK-MKT-PACK-ROLLBACK-REVIEW", Manager, "alice");
        var mine = items.Where(i => i["primaryObject"]!.Text("id") == activation.ToString("D")).ToList();
        mine.Count.ShouldBe(1);
        mine[0].Text("primaryObject.module").ShouldBe("MKT");
        mine[0].Text("priority").ShouldBe("HIGH");

        // Only the underwriting manager (or admin) sees that queue; an underwriter does not.
        (await ListAsync("scope:team,queue:PACK-ROLLBACK-REVIEW", "Staff.Underwriter", "bob")).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_dsar_participant_exports_and_restricts_the_tasks_about_a_party()
    {
        var party = PartyId.New();
        var id = (await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities", new
        {
            patternCode = Psr, raisingModule = "POL", causeRef = "dsar",
            primaryObject = new { module = "POL", type = "Policy", id = Guid.NewGuid().ToString() },
            secondaryObjects = new[] { new { module = "PTY", type = "Party", id = party.Value.ToString("D") } },
        }, Admin)).Body.Text("activityId");

        var wrk = _factory.Services.GetServices<IDsarParticipant>().Single(p => p.Module == "WRK");
        var subject = SubjectRef.ForParty(party);
        var context = new DsarContext(Guid.NewGuid(), "tester", "dpo");

        var export = await wrk.ExportAsync(subject, context, Ct);
        export.ExportPayload.ShouldNotBeNull().ShouldContain(id);
        export.Outcomes.Single().RecordClass.ShouldBe("WORK_TASK");
        export.Outcomes.Single().RecordCount.ShouldBe(1);
        export.Outcomes.Single().Kind.ShouldBe(DsarOutcomeKind.NotHeld); // an export lists records, it changes none
        export.Outcomes.Single().Detail.ShouldBe("EXPORTED");

        var restrict = await wrk.RestrictAsync(subject, "dispute", "legal claim", null, Ct);
        restrict.Outcomes.Single().Kind.ShouldBe(DsarOutcomeKind.Restricted);
        restrict.Outcomes.Single().RecordCount.ShouldBe(1);
        restrict.HasNotBuilt.ShouldBeFalse();

        var erase = await wrk.EraseAsync(subject, context, Ct);
        erase.Outcomes.Single().Kind.ShouldBe(DsarOutcomeKind.Restricted); // retained: the supervisory record is kept
        erase.Outcomes.Single().RecordCount.ShouldBe(1);

        // A party without tasks holds nothing.
        var nothing = await wrk.ExportAsync(SubjectRef.ForParty(PartyId.New()), context, Ct);
        nothing.Outcomes.Single().Kind.ShouldBe(DsarOutcomeKind.NotHeld);
        nothing.Outcomes.Single().RecordCount.ShouldBe(0);
    }

    private static object CreateBody(string pattern, string raising, string objectId, string? cause = null) => new
    {
        patternCode = pattern,
        raisingModule = raising,
        causeRef = cause,
        primaryObject = new { module = raising, type = "Case", id = objectId },
    };

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body, string roles, string user = "test-user")
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

        var response = await Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    /// <summary>Every activity of the filter, following the cursor.</summary>
    private async Task<List<JsonNode>> ListAsync(string filters, string roles, string user)
    {
        var items = new List<JsonNode>();
        string? cursor = null;
        do
        {
            var url = $"/api/wrk/v1/activities?limit=200&filters={Uri.EscapeDataString(filters + ",state:ALL")}" + (cursor is null ? string.Empty : $"&cursor={cursor}");
            var (response, body) = await SendAsync(HttpMethod.Get, url, null, roles, user);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
            items.AddRange(body!["items"]!.AsArray().Select(i => i!["activity"]!));
            cursor = body["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null);
        return items;
    }

    private async Task DeliverRollbackAsync(Guid activation)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<RequestContext>();
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.ConfigurationHash = ConfigurationHash.Parse(new string('a', 64));
        var session = services.GetRequiredService<DbSession>();
        await using var transaction = await session.BeginTransactionAsync(Ct);
        var window = new InstantRange(Instant.FromUtcDateTime(DateTime.UtcNow.AddDays(-3)), Instant.FromUtcDateTime(DateTime.UtcNow));
        var hash = Sha256Hash.Parse(new string('c', 64));
        var payload = new PackRolledBackV1
        {
            PackId = Guid.CreateVersion7(), FromVersion = "0.2.0", ToVersion = "0.1.0", AffectedWindow = window, Window = window,
            HashesInWindow = [hash], HashesIssued = [hash], Reason = "rollback after defect found in treatment rules",
            Pack = "GR", LegalEntity = "GR-TEST", ActivationId = activation, ResultingHash = Sha256Hash.Parse(new string('b', 64)),
        };
        var envelope = services.GetRequiredService<IEventPublisher>().Publish(new OutgoingEvent(
            EventDescriptor.From(PackRolledBackV1.Descriptor), "Stamp", activation.ToString(), payload,
            BusinessKeys.Empty.With("stampId", payload.ResultingHash.Value).With("packId", payload.PackId.ToString())));
        var handler = services.GetRequiredService<EventHandlerRegistry>().Find("WRK.PackRolledBack.RaiseReview")!;
        await handler.Invoke(services, envelope, Ct);
        await transaction.CommitAsync(Ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
