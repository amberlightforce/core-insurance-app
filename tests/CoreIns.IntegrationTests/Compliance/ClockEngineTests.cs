using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Compliance.Clocks;
using CoreIns.Platform.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Compliance;

/// <summary>SL6-CMP-CLOCK: start, stop, elapse and pause through the real host on a shiftable clock; the sweeper is run directly.</summary>
public sealed class ClockEngineTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private readonly ApiHostFactory _factory = new(
        database.AppConnectionString, settings: new Dictionary<string, string?> { ["Platform:Time:Mode"] = "Shiftable" });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private ShiftableClock Clock => (ShiftableClock)_factory.Services.GetRequiredService<IClock>();

    private Task<int> SweepAsync() => _factory.Services.GetRequiredService<ClockSweepRunner>().RunAsync(Ct);

    private static object Subject() => new { module = "BIL", type = "DelinquencyCase", id = Guid.CreateVersion7().ToString() };

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> Post(HttpClient client, string op, object body, Guid? key = null) =>
        SendAsync(client, HttpMethod.Post, $"/api/cmp/v1/clocks/{op}", body, roles: Admin, key: key);

    private async Task<long> CountEventsAsync(string eventType, string clockId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM plt.outbox_message WHERE event_type = @t AND aggregate_id = @a", connection);
        command.Parameters.AddWithValue("t", eventType);
        command.Parameters.AddWithValue("a", clockId);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task A_waiting_period_elapses_exactly_once_when_the_clock_passes_its_deadline()
    {
        using var client = _factory.CreateClient();
        var subject = Subject();
        var (response, started) = await Post(client, "start", new { clockCode = "BIL_NONPAY_NOTICE", subjectRef = subject });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, started?.ToJsonString());
        started.Text("status").ShouldBe("RUNNING");
        var id = started.Text("instanceId");
        (await CountEventsAsync("ClockStarted", id)).ShouldBe(1);

        // Before the deadline nothing happens.
        Clock.Advance(TimeSpan.FromDays(20));
        await SweepAsync();
        (await CountEventsAsync("ClockElapsed", id)).ShouldBe(0);

        // Past one calendar month (end of the Athens day) the sweeper elapses it, once, however often it runs.
        Clock.Advance(TimeSpan.FromDays(15));
        await SweepAsync();
        await SweepAsync();

        (await CountEventsAsync("ClockElapsed", id)).ShouldBe(1);
        (await CountEventsAsync("ClockBreached", id)).ShouldBe(0);
        var (_, got) = await SendAsync(client, HttpMethod.Get, $"/api/cmp/v1/clocks/{id}", roles: Admin);
        got.Text("instances.0.status").ShouldBe("ELAPSED");
    }

    [Fact]
    public async Task Stopping_before_the_deadline_is_ClockMet_and_it_never_elapses()
    {
        using var client = _factory.CreateClient();
        var subject = Subject();
        var (_, started) = await Post(client, "start", new { clockCode = "BIL_NONPAY_NOTICE", subjectRef = subject });
        var id = started.Text("instanceId");

        var (response, stopped) = await Post(client, "stop", new { instanceId = id, outcome = "Cured", stopTime = Clock.Now.ToString() });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, stopped?.ToJsonString());
        stopped.Text("instanceState").ShouldBe("MET");
        stopped.Text("metLateFlag").ShouldBe("false");

        Clock.Advance(TimeSpan.FromDays(40));
        await SweepAsync();

        (await CountEventsAsync("ClockMet", id)).ShouldBe(1);
        (await CountEventsAsync("ClockElapsed", id)).ShouldBe(0);
    }

    [Fact]
    public async Task Starting_twice_for_the_same_definition_subject_and_key_is_idempotent()
    {
        using var client = _factory.CreateClient();
        var subject = Subject();
        var (_, first) = await Post(client, "start", new { clockCode = "BIL_NONPAY_NOTICE", subjectRef = subject });
        var (_, again) = await Post(client, "start", new { clockCode = "BIL_NONPAY_NOTICE", subjectRef = subject });
        var (_, other) = await Post(client, "start", new { clockCode = "BIL_NONPAY_NOTICE", subjectRef = subject, key = "second-notice" });

        again.Text("instanceId").ShouldBe(first.Text("instanceId"));
        other.Text("instanceId").ShouldNotBe(first.Text("instanceId"));
        (await CountEventsAsync("ClockStarted", first.Text("instanceId"))).ShouldBe(1);
    }

    [Fact]
    public async Task A_deadline_warns_then_breaches_and_a_late_stop_stays_breached()
    {
        using var client = _factory.CreateClient();
        var subject = Subject();
        var (_, started) = await Post(client, "start", new { clockCode = "CMP_DSAR_RESPONSE", subjectRef = subject });
        var id = started.Text("instanceId");

        Clock.Advance(TimeSpan.FromDays(25)); // inside the 7 day warning window, before the month ends
        await SweepAsync();
        (await CountEventsAsync("ClockWarned", id)).ShouldBe(1);

        Clock.Advance(TimeSpan.FromDays(10));
        await SweepAsync();
        (await CountEventsAsync("ClockBreached", id)).ShouldBe(1);

        var (_, late) = await Post(client, "stop", new { instanceId = id, outcome = "Met", stopTime = Clock.Now.ToString() });
        late.Text("instanceState").ShouldBe("BREACHED");
        late.Text("metLateFlag").ShouldBe("true");
        (await CountEventsAsync("ClockMet", id)).ShouldBe(0);
    }

    [Fact]
    public async Task Pause_and_resume_move_the_deadline_and_a_paused_clock_does_not_elapse()
    {
        using var client = _factory.CreateClient();
        var subject = Subject();
        var (_, started) = await Post(client, "start", new { clockCode = "BIL_NONPAY_NOTICE", subjectRef = subject });
        var id = started.Text("instanceId");
        var original = Instant(started.Text("deadline"));

        Clock.Advance(TimeSpan.FromDays(10));
        var (paused, pausedBody) = await Post(client, "pause", new { instanceId = id, reason = "arrangement" });
        paused.StatusCode.ShouldBe(HttpStatusCode.OK, pausedBody?.ToJsonString());
        pausedBody.Text("status").ShouldBe("PAUSED");

        Clock.Advance(TimeSpan.FromDays(60));
        await SweepAsync(); // paused: never elapses
        (await CountEventsAsync("ClockElapsed", id)).ShouldBe(0);

        var (_, resumed) = await Post(client, "resume", new { instanceId = id });
        resumed.Text("status").ShouldBe("RUNNING");
        (Instant(resumed.Text("deadline")) - original).ShouldBe(TimeSpan.FromDays(60), TimeSpan.FromSeconds(5));

        var (cancel, cancelBody) = await Post(client, "cancel", new { instanceId = id });
        cancel.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        cancelBody.Text("code").ShouldBe("CMP-ERR-CLOCK-CANCEL-REASON");
    }

    [Fact]
    public async Task Extend_is_allowed_only_for_a_definition_that_permits_it()
    {
        using var client = _factory.CreateClient();
        var (started, dsar) = await Post(client, "start", new { clockCode = "CMP_DSAR_RESPONSE", subjectRef = Subject() });
        started.StatusCode.ShouldBe(HttpStatusCode.OK, dsar?.ToJsonString());
        var (ok, extended) = await Post(client, "extend", new { instanceId = dsar.Text("instanceId"), extension = "P2M" });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, extended?.ToJsonString());
        (Instant(extended.Text("deadline")) - Instant(dsar.Text("deadline"))).TotalDays.ShouldBeInRange(58, 63);

        var (_, bil) = await Post(client, "start", new { clockCode = "BIL_NONPAY_NOTICE", subjectRef = Subject() });
        var (refused, body) = await Post(client, "extend", new { instanceId = bil.Text("instanceId"), extension = "P1M" });
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        body.Text("code").ShouldBe("CMP-ERR-EXTENSION-NOT-ALLOWED");
    }

    [Fact]
    public async Task An_unknown_definition_is_refused_and_the_seeded_definitions_are_listed_with_their_legal_status()
    {
        using var client = _factory.CreateClient();
        var (response, body) = await Post(client, "start", new { clockCode = "BIL_NOPE", subjectRef = Subject() });
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        body.Text("code").ShouldBe("CMP-ERR-CLOCK-UNKNOWN");

        var (listed, list) = await SendAsync(client, HttpMethod.Get, "/api/cmp/v1/clock-defs", roles: Admin);
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, list?.ToJsonString());
        var nonpay = list!["items"]!.AsArray().Single(i => i.Text("code") == "BIL_NONPAY_NOTICE");
        nonpay.Text("kind").ShouldBe("WAITING_PERIOD");
        nonpay.Text("duration").ShouldBe("P1M");
        nonpay.Text("legalStatus").ShouldBe("SECONDARY_SOURCE");
    }

    private static CoreIns.SharedKernel.Instant Instant(string text) => CoreIns.SharedKernel.Instant.Parse(text);
}
