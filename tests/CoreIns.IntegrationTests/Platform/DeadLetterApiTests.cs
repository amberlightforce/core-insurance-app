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
/// NX-OT-14: the dead-letter HTTP API (plt.DeadLetter.list/get/replay) against the real Host. Replay is privileged (Platform.Admin
/// only), audited, idempotent and never returns event payloads.
/// </summary>
public sealed class DeadLetterApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string List = "/api/plt/v1/dead-letters";
    private const string Replay = "/api/plt/v1/dead-letters/replay";
    private const string OkHandler = "WRK.DlqTest.Ok";
    private const string BrokenHandler = "CLM.DlqTest.Broken";

    private readonly ConcurrentDictionary<string, int> _invocations = new();

    public async ValueTask InitializeAsync() =>
        await database.ExecuteAsSuperuserAsync(
            "TRUNCATE plt.outbox_message, plt.processed_event, plt.outbox_dead_letter, plt.event_archive, plt.idempotency_record",
            CancellationToken.None);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private HttpClient NewClient(out ApiHostFactory factory, out IDisposable cleanup)
    {
        var root = new ApiHostFactory(database.AppConnectionString);
        factory = root;
        var custom = root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(new EventHandlerRegistration(OkHandler, ModuleCode.WRK, "WRK.DlqTestEvent", (_, _, _) =>
            {
                _invocations.AddOrUpdate(OkHandler, 1, (_, n) => n + 1);
                return Task.CompletedTask;
            }));
            services.AddSingleton(new EventHandlerRegistration(BrokenHandler, ModuleCode.CLM, "CLM.DlqTestEvent", (_, _, _) =>
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
    public async Task List_filters_pages_with_a_keyset_cursor_and_never_returns_payloads()
    {
        var ok1 = await SeedAsync(OkHandler, "DlqTestEvent", minutesAgo: 1);
        var ok2 = await SeedAsync(OkHandler, "DlqTestEvent", minutesAgo: 2);
        var other = await SeedAsync(BrokenHandler, "OtherEvent", minutesAgo: 3);
        await SeedAsync(OkHandler, "DlqTestEvent", status: "Replayed", minutesAgo: 4);
        using var client = NewClient(out var factory, out var cleanup);
        await using var _ = factory;
        using var __ = cleanup;

        using var first = await SendAsync(client, HttpMethod.Get, $"{List}?limit=2");
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstBody = await JsonAsync(first);
        firstBody["items"]!.AsArray().Select(i => i!["deadLetterId"]!.GetValue<Guid>()).ToArray().ShouldBe([ok1, ok2], "newest first");
        var cursor = firstBody["nextCursor"]!.GetValue<string>();

        using var second = await SendAsync(client, HttpMethod.Get, $"{List}?limit=2&cursor={Uri.EscapeDataString(cursor)}");
        var secondBody = await JsonAsync(second);
        secondBody["items"]!.AsArray().Count.ShouldBe(2);
        secondBody["items"]![0]!["deadLetterId"]!.GetValue<Guid>().ShouldBe(other);
        secondBody["nextCursor"].ShouldBeNull();

        using var byModule = await SendAsync(client, HttpMethod.Get, $"{List}?module=clm&status=Parked");
        (await JsonAsync(byModule))["items"]!.AsArray().Select(i => i!["deadLetterId"]!.GetValue<Guid>()).ToArray().ShouldBe([other]);

        using var byType = await SendAsync(client, HttpMethod.Get, $"{List}?eventType=DlqTestEvent&status=Parked");
        (await JsonAsync(byType))["items"]!.AsArray().Count.ShouldBe(2);

        using var bad = await SendAsync(client, HttpMethod.Get, $"{List}?cursor=not-a-cursor");
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var one = await SendAsync(client, HttpMethod.Get, $"{List}/{ok1}");
        one.StatusCode.ShouldBe(HttpStatusCode.OK);
        var oneText = await one.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        oneText.ShouldContain("seeded failure");
        oneText.ShouldNotContain("SECRET-PII-NAME", Case.Sensitive);
        (await SendAsync(client, HttpMethod.Get, $"{List}/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Replay_success_resolves_the_letter_is_audited_and_a_second_replay_is_409_and_does_not_rerun_the_handler()
    {
        var id = await SeedAsync(OkHandler, "DlqTestEvent");
        using var client = NewClient(out var factory, out var cleanup);
        await using var _ = factory;
        using var __ = cleanup;
        var key = Guid.NewGuid();

        using var response = await SendAsync(client, HttpMethod.Post, Replay, new { deadLetterIds = new[] { id } }, key: key);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await JsonAsync(response);
        body["replayed"]!.GetValue<int>().ShouldBe(1);
        body["results"]![0]!["outcome"]!.GetValue<string>().ShouldBe("Replayed");
        (await ScalarAsync<string>($"SELECT status FROM plt.outbox_dead_letter WHERE dead_letter_id = '{id}'")).ShouldBe("Replayed");
        _invocations[OkHandler].ShouldBe(1);
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = 'plt.DeadLetter.replay' AND outcome = 'Succeeded' AND object_id = '{id}'"))
            .ShouldBe(1);

        // Same key: the stored response, handler not run again.
        using var retry = await SendAsync(client, HttpMethod.Post, Replay, new { deadLetterIds = new[] { id } }, key: key);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        _invocations[OkHandler].ShouldBe(1);

        // New key: the letter is resolved, so it is refused and nothing runs.
        using var again = await SendAsync(client, HttpMethod.Post, Replay, new { deadLetterIds = new[] { id } }, key: Guid.NewGuid());
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await JsonAsync(again))["code"]!.GetValue<string>().ShouldBe("PLT-ERR-DEAD-LETTER-RESOLVED");
        _invocations[OkHandler].ShouldBe(1);
    }

    [Fact]
    public async Task Replay_of_a_still_failing_handler_reports_StillFailing_and_keeps_the_letter_parked()
    {
        var id = await SeedAsync(BrokenHandler, "DlqTestEvent");
        using var client = NewClient(out var factory, out var cleanup);
        await using var _ = factory;
        using var __ = cleanup;

        using var response = await SendAsync(client, HttpMethod.Post, Replay, new { deadLetterIds = new[] { id } }, key: Guid.NewGuid());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await JsonAsync(response);
        body["stillFailing"]!.GetValue<int>().ShouldBe(1);
        body["results"]![0]!["outcome"]!.GetValue<string>().ShouldBe("StillFailing");
        body["results"]![0]!["errorMessage"]!.GetValue<string>().ShouldContain("downstream is still broken");
        (await ScalarAsync<string>($"SELECT status FROM plt.outbox_dead_letter WHERE dead_letter_id = '{id}'")).ShouldBe("Parked");
        (await ScalarAsync<int>($"SELECT attempts FROM plt.outbox_dead_letter WHERE dead_letter_id = '{id}'")).ShouldBe(9);
    }

    [Fact]
    public async Task Replay_by_filter_honours_dry_run_and_then_replays_only_the_matching_parked_letters()
    {
        var okA = await SeedAsync(OkHandler, "DlqTestEvent", minutesAgo: 2);
        var okB = await SeedAsync(OkHandler, "DlqTestEvent", minutesAgo: 1);
        var broken = await SeedAsync(BrokenHandler, "DlqTestEvent");
        using var client = NewClient(out var factory, out var cleanup);
        await using var _ = factory;
        using var __ = cleanup;

        using var dry = await SendAsync(client, HttpMethod.Post, $"{Replay}?dryRun=true", new { module = "WRK" }, key: Guid.NewGuid());
        dry.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dryBody = await JsonAsync(dry);
        dryBody["dryRun"]!.GetValue<bool>().ShouldBeTrue();
        dryBody["matched"]!.GetValue<int>().ShouldBe(2);
        dryBody["results"]!.AsArray().Select(r => r!["outcome"]!.GetValue<string>()).ShouldAllBe(o => o == "WouldReplay");
        _invocations.ShouldBeEmpty();
        (await ScalarAsync<long>("SELECT count(*) FROM plt.outbox_dead_letter WHERE status = 'Parked'")).ShouldBe(3);

        using var real = await SendAsync(client, HttpMethod.Post, Replay, new { module = "WRK" }, key: Guid.NewGuid());
        var body = await JsonAsync(real);
        body["replayed"]!.GetValue<int>().ShouldBe(2);
        _invocations[OkHandler].ShouldBe(2);
        (await ScalarAsync<string>($"SELECT status FROM plt.outbox_dead_letter WHERE dead_letter_id = '{broken}'")).ShouldBe("Parked");
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_dead_letter WHERE status = 'Replayed' AND dead_letter_id IN ('{okA}', '{okB}')")).ShouldBe(2);

        // An empty request (no ids, no filter) is refused: replaying "everything" must be explicit.
        using var empty = await SendAsync(client, HttpMethod.Post, Replay, new { }, key: Guid.NewGuid());
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var missingKey = await SendAsync(client, HttpMethod.Post, Replay, new { module = "WRK" });
        missingKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("Staff.ClaimsManager")]
    [InlineData("Staff.Billing")]
    public async Task Everything_is_403_for_a_caller_who_is_not_Platform_Admin_and_nothing_runs(string role)
    {
        var id = await SeedAsync(OkHandler, "DlqTestEvent");
        using var client = NewClient(out var factory, out var cleanup);
        await using var _ = factory;
        using var __ = cleanup;

        using var list = await SendAsync(client, HttpMethod.Get, List, roles: role);
        using var get = await SendAsync(client, HttpMethod.Get, $"{List}/{id}", roles: role);
        using var replay = await SendAsync(client, HttpMethod.Post, Replay, new { deadLetterIds = new[] { id } }, roles: role, key: Guid.NewGuid());
        using var anonymous = await SendAsync(client, HttpMethod.Post, Replay, new { deadLetterIds = new[] { id } }, roles: null, key: Guid.NewGuid());

        list.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        get.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        replay.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _invocations.ShouldBeEmpty();
        (await ScalarAsync<string>($"SELECT status FROM plt.outbox_dead_letter WHERE dead_letter_id = '{id}'")).ShouldBe("Parked");
    }
}
