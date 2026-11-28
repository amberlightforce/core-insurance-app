using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CoreIns.IntegrationTests.Platform;

/// <summary>
/// BO-PLT-04: dead-letter discard (reason, audit, maker-checker for old letters), consumer replay, exchange search, file transfers,
/// the schema registry and the manual outbox publish, against the real Host.
/// </summary>
public sealed class PlatformOperationsApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Discard = "/api/plt/v1/dead-letters/discard";
    private const string DeadLetters = "/api/plt/v1/dead-letters";
    private const string OkHandler = "WRK.OpsTest.Ok";
    private const string BrokenHandler = "CLM.OpsTest.Broken";
    private static readonly string[] Keys = ["k1"];
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly ConcurrentDictionary<string, int> _invocations = new();

    public async ValueTask InitializeAsync() =>
        await database.ExecuteAsSuperuserAsync(
            "TRUNCATE plt.outbox_message, plt.processed_event, plt.outbox_dead_letter, plt.event_archive, plt.idempotency_record, plt.message_exchange, plt.file_transfer, plt.schema_registry, plt.approval_request",
            CancellationToken.None);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private HttpClient NewClient(out ApiHostFactory factory, out IDisposable cleanup, IReadOnlyDictionary<string, string?>? settings = null)
    {
        var root = new ApiHostFactory(database.AppConnectionString, settings: settings);
        factory = root;
        var custom = root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(new EventHandlerRegistration(OkHandler, ModuleCode.WRK, "wrk.OpsTestEvent.v1", (_, _, _) =>
            {
                _invocations.AddOrUpdate(OkHandler, 1, (_, n) => n + 1);
                return Task.CompletedTask;
            }));
            services.AddSingleton(new EventHandlerRegistration(BrokenHandler, ModuleCode.CLM, "clm.OpsTestEvent.v1", (_, _, _) =>
            {
                _invocations.AddOrUpdate(BrokenHandler, 1, (_, n) => n + 1);
                throw new InvalidOperationException("downstream is still broken");
            }));
        }));
        cleanup = custom;
        return custom.CreateClient();
    }

    private async Task<Guid> SeedAsync(string handler, string eventType, string status = "Parked", int minutesAgo = 0)
    {
        var eventId = Guid.CreateVersion7();
        var deadLetterId = Guid.CreateVersion7();
        await database.ExecuteAsSuperuserAsync(
            string.Create(CultureInfo.InvariantCulture, $$"""
                INSERT INTO plt.event_archive (event_id, position, archived_at, event_type, schema_version, producer, aggregate_type, aggregate_id, aggregate_sequence,
                    occurred_at, recorded_at, legal_entity, jurisdiction, configuration_hash, business_keys, correlation_id, actor_kind, actor_id,
                    origin, data_classification, payload)
                VALUES ('{{eventId}}', (SELECT coalesce(max(position), 0) + 1 FROM plt.event_archive), now(), '{{eventType}}', '1.0', 'WRK', 'Widget', '{{eventId}}', 1, now(), now(), 'GR-TEST', 'GR',
                    repeat('a', 64), '{"widgetId":"w"}', repeat('b', 32), 'USER', 'seed', 'LIVE', 'P0', '{"customerName":"SECRET-PII-NAME"}');
                INSERT INTO plt.outbox_dead_letter (dead_letter_id, handler, event_id, event_type, aggregate_type, aggregate_id, error_class, error_message,
                    attempts, parked_at, status)
                VALUES ('{{deadLetterId}}', '{{handler}}', '{{eventId}}', '{{eventType}}', 'Widget', '{{eventId}}', 'System.InvalidOperationException',
                    'InvalidOperationException: seeded failure', 8, now() - interval '{{minutesAgo}} minutes', '{{status}}');
                """),
            CancellationToken.None);
        return deadLetterId;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, object? body = null, string? roles = "Platform.Admin", Guid? key = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (roles is not null)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
            request.Headers.Add(TestAuthHandler.UserHeader, "ops-admin");
        }

        if (method != HttpMethod.Get && key is null)
        {
            key = Guid.NewGuid();
        }

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key.Value.ToString());
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!;

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task Discard_needs_a_reason_then_removes_the_letter_from_the_parked_list_and_audits_it()
    {
        var id = await SeedAsync(BrokenHandler, "OpsTestEvent", minutesAgo: 5);
        using var client = NewClient(out var factory, out var cleanup);
        await using var _ = factory;
        using var __ = cleanup;

        using var noReason = await SendAsync(client, HttpMethod.Post, Discard, new { deadLetterIds = new[] { id }, reason = "  " });
        noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var dry = await SendAsync(client, HttpMethod.Post, $"{Discard}?dryRun=true", new { deadLetterIds = new[] { id }, reason = "poison message, upstream fixed" });
        dry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(dry))["results"]![0]!["outcome"]!.GetValue<string>().ShouldBe("WouldDiscard");
        (await ScalarAsync<string>($"SELECT status FROM plt.outbox_dead_letter WHERE dead_letter_id = '{id}'")).ShouldBe("Parked");

        using var response = await SendAsync(client, HttpMethod.Post, Discard, new { deadLetterIds = new[] { id }, reason = "poison message, upstream fixed" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await JsonAsync(response);
        body["discarded"]!.GetValue<int>().ShouldBe(1);
        body["results"]![0]!["outcome"]!.GetValue<string>().ShouldBe("Discarded");
        (await ScalarAsync<string>($"SELECT resolution_reason FROM plt.outbox_dead_letter WHERE dead_letter_id = '{id}'")).ShouldBe("poison message, upstream fixed");

        using var parked = await SendAsync(client, HttpMethod.Get, $"{DeadLetters}?status=Parked");
        (await JsonAsync(parked))["items"]!.AsArray().Count.ShouldBe(0);
        using var one = await SendAsync(client, HttpMethod.Get, $"{DeadLetters}/{id}");
        (await JsonAsync(one))["status"]!.GetValue<string>().ShouldBe("Discarded");
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = 'plt.DeadLetter.discard' AND outcome = 'Succeeded' AND object_id = '{id}'"))
            .ShouldBe(1);

        using var again = await SendAsync(client, HttpMethod.Post, Discard, new { deadLetterIds = new[] { id }, reason = "second try" });
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        _invocations.ShouldBeEmpty();

        using var forbidden = await SendAsync(client, HttpMethod.Post, Discard, new { deadLetterIds = new[] { id }, reason = "not an admin" }, roles: "Staff.ClaimsHandler");
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Discard_of_an_old_letter_needs_an_approved_request_decided_by_someone_else()
    {
        var id = await SeedAsync(BrokenHandler, "OpsTestEvent", minutesAgo: 60 * 24 * 10);
        using var client = NewClient(out var factory, out var cleanup);
        await using var _ = factory;
        using var __ = cleanup;

        using var refused = await SendAsync(client, HttpMethod.Post, Discard, new { deadLetterIds = new[] { id }, reason = "ten days old" });
        refused.StatusCode.ShouldBe((HttpStatusCode)422);
        (await JsonAsync(refused))["code"]!.GetValue<string>().ShouldBe("PLT-ERR-DISCARD-APPROVAL-REQUIRED");
        (await ScalarAsync<string>($"SELECT status FROM plt.outbox_dead_letter WHERE dead_letter_id = '{id}'")).ShouldBe("Parked");

        // Approved, but decided by the discarding actor itself: still refused (four eyes).
        await SeedApprovalAsync(id, checker: "ops-admin");
        using var self = await SendAsync(client, HttpMethod.Post, Discard, new { deadLetterIds = new[] { id }, reason = "ten days old" });
        self.StatusCode.ShouldBe((HttpStatusCode)422);

        await SeedApprovalAsync(id, checker: "second-admin");
        using var ok = await SendAsync(client, HttpMethod.Post, Discard, new { deadLetterIds = new[] { id }, reason = "ten days old" });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ScalarAsync<string>($"SELECT status FROM plt.outbox_dead_letter WHERE dead_letter_id = '{id}'")).ShouldBe("Discarded");
    }

    private Task SeedApprovalAsync(Guid deadLetterId, string checker) =>
        database.ExecuteAsSuperuserAsync(
            string.Create(CultureInfo.InvariantCulture, $$"""
                INSERT INTO plt.approval_request (request_id, legal_entity, approval_type, object_module, object_type, object_id, payload_hash, status, maker_kind, maker_id,
                    editors, authority_type, authority_codes, referral_role, requested_at, decision, checker_kind, checker_id, authority_check_id, decided_at, version)
                VALUES ('{{Guid.NewGuid()}}', 'GR-TEST', 'plt.DeadLetter.discard', 'PLT', 'DeadLetter', '{{deadLetterId}}', repeat('c', 64), 'Approved', 'USER', 'maker',
                    ARRAY[]::text[], 'PLT.TEST', '{}', 'Platform.Admin', now(), 'Approved', 'USER', '{{checker}}', '{{Guid.NewGuid()}}', now(), 2);
                """),
            CancellationToken.None);

    [Fact]
    public async Task Consumer_replay_by_dead_letter_ids_reruns_parked_ones_and_is_a_no_op_for_resolved_ones()
    {
        var parked = await SeedAsync(OkHandler, "OpsTestEvent");
        var resolved = await SeedAsync(OkHandler, "OpsTestEvent", status: "Replayed");
        using var client = NewClient(out var factory, out var cleanup);
        await using var _ = factory;
        using var __ = cleanup;

        using var noop = await SendAsync(client, HttpMethod.Post, "/api/plt/v1/consumers/replay", new { deadLetterIds = new[] { resolved } });
        noop.StatusCode.ShouldBe(HttpStatusCode.OK);
        var noopBody = await JsonAsync(noop);
        noopBody["delivered"]!.GetValue<int>().ShouldBe(0);
        noopBody["skipped"]!.GetValue<int>().ShouldBe(1);
        _invocations.ShouldBeEmpty();

        using var response = await SendAsync(client, HttpMethod.Post, "/api/plt/v1/consumers/replay", new { deadLetterIds = new[] { parked, resolved } });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await JsonAsync(response);
        body["delivered"]!.GetValue<int>().ShouldBe(1);
        body["skipped"]!.GetValue<int>().ShouldBe(1);
        _invocations[OkHandler].ShouldBe(1);
        (await ScalarAsync<string>($"SELECT status FROM plt.outbox_dead_letter WHERE dead_letter_id = '{parked}'")).ShouldBe("Replayed");
    }

    [Fact]
    public async Task Consumer_replay_by_range_redelivers_archived_events_and_refuses_an_unbounded_or_unknown_consumer()
    {
        await SeedAsync(OkHandler, "OpsTestEvent", status: "Replayed");
        await SeedAsync(OkHandler, "OpsTestEvent", status: "Replayed");
        using var client = NewClient(out var factory, out var cleanup);
        await using var _ = factory;
        using var __ = cleanup;
        var from = DateTimeOffset.UtcNow.AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        using var unbounded = await SendAsync(client, HttpMethod.Post, "/api/plt/v1/consumers/replay", new { consumer = OkHandler });
        unbounded.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var unknown = await SendAsync(client, HttpMethod.Post, "/api/plt/v1/consumers/replay", new { consumer = "WRK.Nope", recordedFrom = from });
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var dry = await SendAsync(client, HttpMethod.Post, "/api/plt/v1/consumers/replay?dryRun=true", new { consumer = OkHandler, recordedFrom = from });
        var dryBody = await JsonAsync(dry);
        dryBody["matched"]!.GetValue<int>().ShouldBe(2);
        dryBody["delivered"]!.GetValue<int>().ShouldBe(0);
        _invocations.ShouldBeEmpty();

        using var response = await SendAsync(client, HttpMethod.Post, "/api/plt/v1/consumers/replay", new { consumer = OkHandler, recordedFrom = from });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(response))["delivered"]!.GetValue<int>().ShouldBe(2);
        _invocations[OkHandler].ShouldBe(2);
    }

    [Fact]
    public async Task Exchange_search_filters_by_party_and_period_and_pages()
    {
        var now = DateTimeOffset.UtcNow;
        await database.ExecuteAsSuperuserAsync(
            string.Create(CultureInfo.InvariantCulture, $$"""
                INSERT INTO plt.message_exchange (exchange_id, legal_entity, direction, counterparty, party_ref, channel, operation, status, started_at) VALUES
                  ('{{Guid.NewGuid()}}', 'GR-TEST', 'Outbound', 'ACME-REINS', 'PTY-1', 'SFTP', 'send-bordereau', 'Delivered', now() - interval '1 hour'),
                  ('{{Guid.NewGuid()}}', 'GR-TEST', 'Inbound', 'ACME-REINS', 'PTY-1', 'SFTP', 'receive-ack', 'Received', now() - interval '2 hour'),
                  ('{{Guid.NewGuid()}}', 'GR-TEST', 'Outbound', 'OTHER', 'PTY-2', 'API', 'notify', 'Delivered', now() - interval '3 hour'),
                  ('{{Guid.NewGuid()}}', 'GR-OTHER', 'Outbound', 'ACME-REINS', 'PTY-1', 'SFTP', 'send-bordereau', 'Delivered', now() - interval '1 hour');
                """),
            CancellationToken.None);
        using var client = NewClient(out var factory, out var cleanup);
        await using var _ = factory;
        using var __ = cleanup;

        using var byParty = await SendAsync(client, HttpMethod.Get, "/api/plt/v1/exchanges/search?partyRef=PTY-1");
        byParty.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(byParty))["items"]!.AsArray().Count.ShouldBe(2, "the other legal entity's exchange is not visible");

        using var page = await SendAsync(client, HttpMethod.Get, "/api/plt/v1/exchanges/search?limit=2");
        var pageBody = await JsonAsync(page);
        pageBody["items"]!.AsArray().Count.ShouldBe(2);
        var cursor = pageBody["nextCursor"]!.GetValue<string>();
        using var next = await SendAsync(client, HttpMethod.Get, $"/api/plt/v1/exchanges/search?limit=2&cursor={Uri.EscapeDataString(cursor)}");
        (await JsonAsync(next))["items"]!.AsArray().Count.ShouldBe(1);

        var from = Uri.EscapeDataString(now.AddMinutes(-90).ToString("O", CultureInfo.InvariantCulture));
        using var byPeriod = await SendAsync(client, HttpMethod.Get, $"/api/plt/v1/exchanges/search?from={from}&direction=Outbound");
        (await JsonAsync(byPeriod))["items"]!.AsArray().Count.ShouldBe(1);

        using var compliance = await SendAsync(client, HttpMethod.Get, "/api/plt/v1/exchanges/search", roles: "Staff.ComplianceOfficer");
        compliance.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var denied = await SendAsync(client, HttpMethod.Get, "/api/plt/v1/exchanges/search", roles: "Staff.ClaimsHandler");
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Transfers_list_and_retry_resets_only_failed_ones()
    {
        var failed = Guid.NewGuid();
        var sent = Guid.NewGuid();
        await database.ExecuteAsSuperuserAsync(
            string.Create(CultureInfo.InvariantCulture, $$"""
                INSERT INTO plt.file_transfer (transfer_id, legal_entity, direction, partner, file_name, status, attempts, last_error, created_at, updated_at) VALUES
                  ('{{failed}}', 'GR-TEST', 'Outbound', 'BANK', 'sepa-1.xml', 'Failed', 3, 'connection refused', now() - interval '1 hour', now() - interval '1 hour'),
                  ('{{sent}}', 'GR-TEST', 'Outbound', 'BANK', 'sepa-0.xml', 'Sent', 1, NULL, now() - interval '2 hour', now() - interval '2 hour');
                """),
            CancellationToken.None);
        using var client = NewClient(out var factory, out var cleanup);
        await using var _ = factory;
        using var __ = cleanup;

        using var list = await SendAsync(client, HttpMethod.Get, "/api/plt/v1/transfers?status=Failed");
        var items = (await JsonAsync(list))["items"]!.AsArray();
        items.Count.ShouldBe(1);
        items[0]!["lastError"]!.GetValue<string>().ShouldBe("connection refused");

        using var notRetryable = await SendAsync(client, HttpMethod.Post, "/api/plt/v1/transfers/retry", new { transferIds = new[] { sent } });
        notRetryable.StatusCode.ShouldBe((HttpStatusCode)422);

        using var retry = await SendAsync(client, HttpMethod.Post, "/api/plt/v1/transfers/retry", new { transferIds = new[] { failed, sent } });
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await JsonAsync(retry);
        body["retried"]!.AsArray().Select(i => i!.GetValue<Guid>()).ToArray().ShouldBe([failed]);
        body["skipped"]!.AsArray().Select(i => i!.GetValue<Guid>()).ToArray().ShouldBe([sent]);
        (await ScalarAsync<string>($"SELECT status FROM plt.file_transfer WHERE transfer_id = '{failed}'")).ShouldBe("Pending");
        (await ScalarAsync<string>($"SELECT status FROM plt.file_transfer WHERE transfer_id = '{sent}'")).ShouldBe("Sent");
    }

    [Fact]
    public async Task Schema_registry_versions_compatible_changes_and_refuses_incompatible_ones()
    {
        using var client = NewClient(out var factory, out var cleanup);
        await using var _ = factory;
        using var __ = cleanup;
        var v1 = new { type = "object", properties = new { id = new { type = "string" }, amount = new { type = "number" } }, required = new[] { "id" } };
        var v2 = new
        {
            type = "object",
            properties = new { id = new { type = "string" }, amount = new { type = "number" }, note = new { type = "string" } },
            required = new[] { "id" },
        };
        var removed = new { type = "object", properties = new { id = new { type = "string" }, note = new { type = "string" } }, required = new[] { "id" } };
        var retyped = new { type = "object", properties = new { id = new { type = "integer" }, amount = new { type = "number" }, note = new { type = "string" } }, required = new[] { "id" } };
        var newRequired = new
        {
            type = "object",
            properties = new { id = new { type = "string" }, amount = new { type = "number" }, note = new { type = "string" } },
            required = new[] { "id", "amount" },
        };

        using var first = await SendAsync(client, HttpMethod.Post, "/api/plt/v1/schemas/register", new { subject = "bil.payment", schema = v1 });
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(first))["version"]!.GetValue<int>().ShouldBe(1);

        using var same = await SendAsync(client, HttpMethod.Post, "/api/plt/v1/schemas/register", new { subject = "bil.payment", schema = v1 });
        (await JsonAsync(same))["version"]!.GetValue<int>().ShouldBe(1, "re-registering identical content does not add a version");

        using var second = await SendAsync(client, HttpMethod.Post, "/api/plt/v1/schemas/register", new { subject = "bil.payment", schema = v2 });
        (await JsonAsync(second))["version"]!.GetValue<int>().ShouldBe(2);

        foreach (var (candidate, expected) in new (object, string)[] { (removed, "was removed"), (retyped, "changed from"), (newRequired, "became required") })
        {
            using var check = await SendAsync(client, HttpMethod.Post, "/api/plt/v1/schemas/check", new { subject = "bil.payment", schema = candidate });
            check.StatusCode.ShouldBe(HttpStatusCode.OK);
            var checkBody = await JsonAsync(check);
            checkBody["compatible"]!.GetValue<bool>().ShouldBeFalse();
            checkBody["issues"]![0]!.GetValue<string>().ShouldContain(expected);

            using var register = await SendAsync(client, HttpMethod.Post, "/api/plt/v1/schemas/register", new { subject = "bil.payment", schema = candidate });
            register.StatusCode.ShouldBe((HttpStatusCode)422);
            (await JsonAsync(register))["code"]!.GetValue<string>().ShouldBe("PLT-ERR-INCOMPATIBLE");
        }

        (await ScalarAsync<long>("SELECT count(*) FROM plt.schema_registry WHERE subject = 'bil.payment'")).ShouldBe(2);

        using var bad = await SendAsync(client, HttpMethod.Post, "/api/plt/v1/schemas/check", new { subject = "bad subject!", schema = v1 });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Outbox_publish_works_on_a_test_tenant_for_allowed_events_only()
    {
        using var client = NewClient(out var factory, out var cleanup);
        await using var _ = factory;
        using var __ = cleanup;
        var payload = new
        {
            keys = new[] { "k1" },
            layer = "TENANT",
            newHash = Hash,
            effectiveAt = "2026-11-28T10:00:00Z",
        };

        using var response = await SendAsync(client, HttpMethod.Post, "/api/plt/v1/outbox/publish", new
        {
            eventType = "ConfigChanged",
            aggregateType = "ResolutionContext",
            aggregateId = "ctx-1",
            payload,
            businessKeys = new Dictionary<string, string> { ["resolutionContextId"] = "ctx-1" },
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var eventId = (await JsonAsync(response))["eventId"]!.GetValue<Guid>();
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_id = '{eventId}'")).ShouldBe(1);

        using var notAllowed = await SendAsync(client, HttpMethod.Post, "/api/plt/v1/outbox/publish", new
        {
            eventType = "PolicyBound",
            aggregateType = "Policy",
            aggregateId = "p1",
            payload,
        });
        notAllowed.StatusCode.ShouldBe((HttpStatusCode)422);
        (await JsonAsync(notAllowed))["code"]!.GetValue<string>().ShouldBe("PLT-ERR-EVENT-TYPE-NOT-ALLOWED");

        using var badPayload = await SendAsync(client, HttpMethod.Post, "/api/plt/v1/outbox/publish", new
        {
            eventType = "ConfigChanged",
            aggregateType = "ResolutionContext",
            aggregateId = "ctx-1",
            payload = new { layer = "TENANT" },
        });
        badPayload.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Outbox_publish_is_refused_outside_a_test_tenant()
    {
        using var client = NewClient(out var factory, out var cleanup, new Dictionary<string, string?> { ["Stamp:LegalEntity"] = "GR-LIVE" });
        await using var _ = factory;
        using var __ = cleanup;

        using var response = await SendAsync(client, HttpMethod.Post, "/api/plt/v1/outbox/publish", new
        {
            eventType = "ConfigChanged",
            aggregateType = "ResolutionContext",
            aggregateId = "ctx-1",
            payload = new { keys = Keys, layer = "TENANT", newHash = Hash, effectiveAt = "2026-11-28T10:00:00Z" },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await JsonAsync(response))["code"]!.GetValue<string>().ShouldBe("PLT-ERR-TEST-TENANT-ONLY");
        (await ScalarAsync<long>("SELECT count(*) FROM plt.outbox_message")).ShouldBe(0);
    }
}
