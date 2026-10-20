using System.Collections.Concurrent;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.IntegrationTests.Platform;

/// <summary>Controls the order-sensitive <see cref="LedgerHandler"/> in the replay tests.</summary>
internal sealed class LedgerBehaviour
{
    private LedgerGate? _gate;

    public Func<EventEnvelope, bool> FailWhile { get; set; } = _ => false;

    public ConcurrentQueue<long> Stale { get; } = new();

    public ConcurrentQueue<long> Duplicates { get; } = new();

    /// <summary>The next invocation (once) parks at the gate after its effect, like a slow handler.</summary>
    public LedgerGate ArmGate(bool failAfterRelease)
    {
        var gate = new LedgerGate(failAfterRelease);
        _gate = gate;
        return gate;
    }

    public LedgerGate? TakeGate() => Interlocked.Exchange(ref _gate, null);
}

internal sealed class LedgerGate(bool failAfterRelease)
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool FailAfterRelease { get; } = failAfterRelease;
}

/// <summary>
/// The shape the order-sensitive consumers (cession, ledger posting, claim financials) must have: dedupe on
/// <c>event_id</c> (a row keyed by it, in the handler's transaction) and apply to the aggregate state only when the
/// <c>aggregateSequence</c> is newer than the last applied one.
/// </summary>
internal sealed class LedgerHandler(LedgerBehaviour behaviour, DbSession session) : IEventHandler<WidgetCreatedPayload>
{
    public const string Name = "WRK.WidgetCreated.Ledger";

    public async Task HandleAsync(EventEnvelope envelope, WidgetCreatedPayload payload, CancellationToken cancellationToken)
    {
        await using (var ledger = new NpgsqlCommand(
            "INSERT INTO tst.ledger (event_id, aggregate_id, seq) VALUES (@id, @aggregate, @seq) ON CONFLICT (event_id) DO NOTHING",
            session.Connection, session.Transaction))
        {
            ledger.Parameters.AddWithValue("id", envelope.EventId.Value);
            ledger.Parameters.AddWithValue("aggregate", envelope.AggregateId);
            ledger.Parameters.AddWithValue("seq", envelope.AggregateSequence);
            if (await ledger.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                behaviour.Duplicates.Enqueue(envelope.AggregateSequence);
                return;
            }
        }

        if (behaviour.TakeGate() is { } gate)
        {
            gate.Entered.TrySetResult();
            await gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            if (gate.FailAfterRelease)
            {
                throw new InvalidOperationException("slow handler failed");
            }
        }

        if (behaviour.FailWhile(envelope))
        {
            throw new InvalidOperationException($"ledger failure for {payload.Name}");
        }

        await using var state = new NpgsqlCommand(
            """
            INSERT INTO tst.agg_state (aggregate_id, last_seq, applied, name) VALUES (@aggregate, @seq, 1, @name)
            ON CONFLICT (aggregate_id) DO UPDATE SET last_seq = EXCLUDED.last_seq, name = EXCLUDED.name, applied = agg_state.applied + 1
            WHERE agg_state.last_seq < EXCLUDED.last_seq
            """,
            session.Connection, session.Transaction);
        state.Parameters.AddWithValue("aggregate", envelope.AggregateId);
        state.Parameters.AddWithValue("seq", envelope.AggregateSequence);
        state.Parameters.AddWithValue("name", payload.Name);
        if (await state.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            behaviour.Stale.Enqueue(envelope.AggregateSequence);
        }
    }
}

/// <summary>Dead-letter / archive replay, out-of-order delivery and lease expiry against a real PostgreSQL (NX-EC-07).</summary>
public sealed class OutboxReplayTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string LedgerDdl = """
        CREATE SCHEMA IF NOT EXISTS tst;
        CREATE TABLE IF NOT EXISTS tst.ledger (event_id uuid PRIMARY KEY, aggregate_id text NOT NULL, seq bigint NOT NULL);
        CREATE TABLE IF NOT EXISTS tst.agg_state (aggregate_id text PRIMARY KEY, last_seq bigint NOT NULL, applied int NOT NULL, name text NOT NULL);
        GRANT USAGE ON SCHEMA tst TO app;
        GRANT SELECT, INSERT, UPDATE, DELETE ON tst.ledger, tst.agg_state TO app;
        TRUNCATE tst.ledger, tst.agg_state;
        """;

    public async ValueTask InitializeAsync() =>
        await database.ExecuteAsSuperuserAsync(
            "TRUNCATE plt.outbox_message, plt.aggregate_sequence, plt.processed_event, plt.outbox_dead_letter, plt.event_archive, plt.idempotency_record",
            CancellationToken.None);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_dead_letter_replayed_after_later_events_is_refused_by_the_sequence_guard_and_never_double_applies()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await NewHarnessAsync(o =>
        {
            o.MaxAttempts = 2;
            o.HandlerBatchSize = 1;
        });
        var behaviour = harness.Services.GetRequiredService<LedgerBehaviour>();
        var aggregate = Guid.CreateVersion7();
        for (var i = 1; i <= 3; i++)
        {
            (await harness.SendAsync(new CreateWidget($"n{i}", i, aggregate))).IsSuccess.ShouldBeTrue();
        }

        behaviour.FailWhile = e => e.AggregateSequence == 1;
        await harness.Processor.DrainAsync(ct);
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.Processor.DrainAsync(ct);

        (await harness.CountAsync($"SELECT count(*) FROM plt.outbox_dead_letter WHERE status = 'Parked' AND handler = '{LedgerHandler.Name}'")).ShouldBe(1);
        (await harness.CountAsync("SELECT count(*) FROM tst.ledger")).ShouldBe(2, "the failed attempt rolled back; 2 and 3 were applied");
        (await StateAsync(harness, aggregate)).ShouldBe((3L, 2, "n3"));

        behaviour.FailWhile = _ => false;
        var replay = harness.Services.GetRequiredService<OutboxReplayService>();
        var deadLetter = await DeadLetterIdAsync(harness);
        (await replay.ReplayDeadLetterAsync(deadLetter, ct)).ShouldBeTrue();

        (await harness.CountAsync("SELECT count(*) FROM tst.ledger")).ShouldBe(3);
        (await StateAsync(harness, aggregate)).ShouldBe((3L, 2, "n3"), "the stale sequence 1 must not overwrite the newer state");
        behaviour.Stale.ToArray().ShouldBe([1L]);

        // A resolved dead letter cannot be replayed a second time.
        await Should.ThrowAsync<InvalidOperationException>(() => replay.ReplayDeadLetterAsync(deadLetter, ct));
        (await harness.CountAsync("SELECT count(*) FROM tst.ledger")).ShouldBe(3);
    }

    [Fact]
    public async Task Replaying_the_whole_archive_to_a_deduping_handler_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await NewHarnessAsync();
        var behaviour = harness.Services.GetRequiredService<LedgerBehaviour>();
        var aggregate = Guid.CreateVersion7();
        for (var i = 1; i <= 4; i++)
        {
            await harness.SendAsync(new CreateWidget($"a{i}", i, aggregate));
        }

        await harness.Processor.DrainAsync(ct);
        (await StateAsync(harness, aggregate)).ShouldBe((4L, 4, "a4"));

        var replay = harness.Services.GetRequiredService<OutboxReplayService>();
        var filter = new ReplayFilter { AggregateType = "Widget", AggregateId = aggregate.ToString() };
        var delivered = await replay.ReplayAsync(LedgerHandler.Name, filter, ct);
        var again = await replay.ReplayAsync(LedgerHandler.Name, filter, ct);

        delivered.ShouldBe(4);
        again.ShouldBe(4);
        behaviour.Duplicates.Count.ShouldBe(8, "every replayed event is recognised by its event_id");
        (await harness.CountAsync("SELECT count(*) FROM tst.ledger")).ShouldBe(4);
        (await StateAsync(harness, aggregate)).ShouldBe((4L, 4, "a4"));
        (await harness.CountAsync($"SELECT sum(replay_count) FROM plt.processed_event WHERE handler = '{LedgerHandler.Name}'")).ShouldBe(8);
    }

    [Fact]
    public async Task A_replay_of_an_event_that_was_never_applied_is_applied_exactly_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await NewHarnessAsync();
        var aggregate = Guid.CreateVersion7();
        await harness.SendAsync(new CreateWidget("only", 1m, aggregate));
        await harness.Processor.DrainAsync(ct);

        // Operator wipes the effect (e.g. restored projection) and replays: applied once, a second replay is a no-op.
        await database.ExecuteAsSuperuserAsync("TRUNCATE tst.ledger, tst.agg_state", ct);
        var replay = harness.Services.GetRequiredService<OutboxReplayService>();
        var filter = new ReplayFilter { AggregateType = "Widget", AggregateId = aggregate.ToString() };
        await replay.ReplayAsync(LedgerHandler.Name, filter, ct);
        await replay.ReplayAsync(LedgerHandler.Name, filter, ct);

        (await StateAsync(harness, aggregate)).ShouldBe((1L, 1, "only"));
        (await harness.CountAsync("SELECT count(*) FROM tst.ledger")).ShouldBe(1);
    }

    [Fact]
    public async Task A_slow_handler_that_outlives_its_lease_is_not_applied_twice_by_the_reclaiming_dispatcher()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await NewHarnessAsync(o => o.LeaseDuration = TimeSpan.FromSeconds(60));
        var behaviour = harness.Services.GetRequiredService<LedgerBehaviour>();
        var aggregate = Guid.CreateVersion7();
        await harness.SendAsync(new CreateWidget("slow", 1m, aggregate));

        var gate = behaviour.ArmGate(failAfterRelease: false);
        var first = Task.Run(() => harness.Processor.ProcessBatchAsync(ct), ct);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        var leaseBefore = await LeaseUntilAsync(harness);
        harness.Clock.Advance(TimeSpan.FromSeconds(61));
        var second = Task.Run(() => SecondDispatcher(harness).ProcessBatchAsync(ct), ct);
        await WaitUntilAsync(async () => await LeaseUntilAsync(harness) > leaseBefore, ct);

        gate.Release.SetResult();
        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(60), ct);
        var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(60), ct);

        firstResult.Claimed.ShouldBe(1);
        secondResult.Claimed.ShouldBe(1, "the expired lease was reclaimed");
        (await harness.CountAsync("SELECT count(*) FROM tst.ledger")).ShouldBe(1);
        (await StateAsync(harness, aggregate)).ShouldBe((1L, 1, "slow"));
        (await harness.CountAsync("SELECT count(*) FROM tst.delivery")).ShouldBe(1);
        (await harness.CountAsync("SELECT count(*) FROM plt.outbox_message WHERE status = 'Pending'")).ShouldBe(0);
        (await harness.CountAsync("SELECT count(*) FROM plt.event_archive")).ShouldBe(1);
        (await harness.CountAsync("SELECT count(*) FROM plt.outbox_dead_letter")).ShouldBe(0);
    }

    [Fact]
    public async Task A_slow_handler_that_fails_after_losing_its_lease_leaves_the_reclaiming_dispatchers_effect_intact()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await NewHarnessAsync(o =>
        {
            o.LeaseDuration = TimeSpan.FromSeconds(60);
            o.MaxAttempts = 1; // the stale dispatcher's failure is its last attempt: it must not park
        });
        var behaviour = harness.Services.GetRequiredService<LedgerBehaviour>();
        var aggregate = Guid.CreateVersion7();
        await harness.SendAsync(new CreateWidget("slowfail", 1m, aggregate));

        var gate = behaviour.ArmGate(failAfterRelease: true);
        var first = Task.Run(() => harness.Processor.ProcessBatchAsync(ct), ct);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        var leaseBefore = await LeaseUntilAsync(harness);
        harness.Clock.Advance(TimeSpan.FromSeconds(61));
        var second = Task.Run(() => SecondDispatcher(harness).ProcessBatchAsync(ct), ct);
        await WaitUntilAsync(async () => await LeaseUntilAsync(harness) > leaseBefore, ct);

        gate.Release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(60), ct);
        await second.WaitAsync(TimeSpan.FromSeconds(60), ct);

        // The first dispatcher's handler rolled back; the second applied the event once and completed it. The first
        // dispatcher's retry bookkeeping is guarded by lease ownership, so it does not resurrect the message.
        (await harness.CountAsync("SELECT count(*) FROM tst.ledger")).ShouldBe(1);
        (await StateAsync(harness, aggregate)).ShouldBe((1L, 1, "slowfail"));
        (await harness.CountAsync("SELECT count(*) FROM plt.outbox_message WHERE status = 'Pending'")).ShouldBe(0);
        (await harness.CountAsync("SELECT count(*) FROM plt.event_archive")).ShouldBe(1);
        (await harness.CountAsync("SELECT count(*) FROM plt.outbox_dead_letter")).ShouldBe(0, "a dispatcher that lost its lease must not park");
        (await harness.CountAsync("SELECT count(*) FROM plt.outbox_message WHERE event_type = 'DeadLetterParked'")).ShouldBe(0);
    }

    private async Task<PlatformHarness> NewHarnessAsync(Action<OutboxOptions>? outbox = null)
    {
        await database.ExecuteAsMigratorAsync(LedgerDdl, CancellationToken.None);
        return await PlatformHarness.CreateAsync(database, outbox, services =>
        {
            services.AddSingleton<LedgerBehaviour>();
            services.AddEventHandler<WidgetCreatedPayload, LedgerHandler>(WidgetEvents.WidgetCreated, LedgerHandler.Name, ModuleCode.WRK);
        });
    }

    private static OutboxProcessor SecondDispatcher(PlatformHarness harness)
    {
        var services = harness.Services;
        return new OutboxProcessor(
            harness.DataSource,
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<EventHandlerRegistry>(),
            harness.Clock,
            services.GetRequiredService<IOptions<OutboxOptions>>(),
            services.GetRequiredService<ILogger<OutboxProcessor>>());
    }

    private static async Task<(long LastSeq, int Applied, string Name)> StateAsync(PlatformHarness harness, Guid aggregate)
    {
        await using var command = harness.DataSource.CreateCommand($"SELECT last_seq, applied, name FROM tst.agg_state WHERE aggregate_id = '{aggregate}'");
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue("aggregate state exists");
        return (reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2));
    }

    private static async Task<Guid> DeadLetterIdAsync(PlatformHarness harness)
    {
        await using var command = harness.DataSource.CreateCommand($"SELECT dead_letter_id FROM plt.outbox_dead_letter WHERE handler = '{LedgerHandler.Name}'");
        return (Guid)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<DateTime> LeaseUntilAsync(PlatformHarness harness)
    {
        await using var command = harness.DataSource.CreateCommand("SELECT lease_until FROM plt.outbox_message WHERE status = 'Pending' AND lease_until IS NOT NULL LIMIT 1");
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is DateTime dt ? dt : DateTime.MinValue;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not reached");
            }

            await Task.Delay(50, cancellationToken);
        }
    }
}
