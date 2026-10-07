using System.Collections.Concurrent;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.IntegrationTests.Platform;

internal sealed class OrderLog
{
    public ConcurrentQueue<(string Handler, string AggregateId, long Sequence)> Seen { get; } = new();

    public Func<EventEnvelope, bool> Crash { get; set; } = _ => false;

    public CancellationTokenSource? Cts { get; set; }
}

internal static class RevEvents
{
    public static EventDescriptor WidgetRenamed { get; } = new(
        ModuleCode.WRK, EventTypeName.Parse("WidgetRenamed"), "1.0", ["Widget"], DataClassification.P0)
    {
        RequiredBusinessKeys = ["widgetId"],
    };
}

internal sealed class CreatedOrderHandler(OrderLog log) : IEventHandler<WidgetCreatedPayload>
{
    public Task HandleAsync(EventEnvelope envelope, WidgetCreatedPayload payload, CancellationToken cancellationToken)
    {
        log.Seen.Enqueue(("created", envelope.AggregateId, envelope.AggregateSequence));
        return Task.CompletedTask;
    }
}

internal sealed class RenamedOrderHandler(OrderLog log) : IEventHandler<WidgetCreatedPayload>
{
    public Task HandleAsync(EventEnvelope envelope, WidgetCreatedPayload payload, CancellationToken cancellationToken)
    {
        log.Seen.Enqueue(("renamed", envelope.AggregateId, envelope.AggregateSequence));
        return Task.CompletedTask;
    }
}

/// <summary>Crashes the dispatcher (cancels its token) when told to; effect row in the handler transaction.</summary>
internal sealed class CrashingHandler(OrderLog log, DbSession session) : IEventHandler<WidgetCreatedPayload>
{
    public async Task HandleAsync(EventEnvelope envelope, WidgetCreatedPayload payload, CancellationToken cancellationToken)
    {
        if (log.Crash(envelope))
        {
            log.Cts!.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }

        await using (var effect = new NpgsqlCommand(
            "INSERT INTO tst.delivery (handler, event_id, origin) VALUES ('crash', @id, 'LIVE')", session.Connection, session.Transaction))
        {
            effect.Parameters.AddWithValue("id", envelope.EventId.Value);
            await effect.ExecuteNonQueryAsync(cancellationToken);
        }

        log.Seen.Enqueue(("crash", envelope.AggregateId, envelope.AggregateSequence));
    }
}

/// <summary>Independent review of F-1b (attempt 1): adversarial reproducers kept as regression tests (R1 = M1, R2 = M2).</summary>
public sealed class ReviewerRegressionTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() =>
        await database.ExecuteAsSuperuserAsync(
            "TRUNCATE plt.outbox_message, plt.aggregate_sequence, plt.processed_event, plt.outbox_dead_letter, plt.event_archive, plt.idempotency_record",
            CancellationToken.None);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task PublishAsync(PlatformHarness harness, params (EventDescriptor Descriptor, string Aggregate)[] events)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.User("user-1");
        context.ConfigurationHash = PlatformHarness.Hash;
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        var session = scope.ServiceProvider.GetRequiredService<DbSession>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
        var tx = await session.BeginTransactionAsync(CancellationToken.None);
        await using (tx)
        {
            foreach (var (descriptor, aggregate) in events)
            {
                var payload = new WidgetCreatedPayload(Guid.CreateVersion7(), "x", Money.Of(1m, "EUR"));
                publisher.Publish(new OutgoingEvent(descriptor, "Widget", aggregate, payload, BusinessKeys.Empty.With("widgetId", aggregate)));
            }

            await tx.CommitAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task R1_micro_batching_keeps_aggregate_order_across_event_types()
    {
        var log = new OrderLog();
        await using var harness = await PlatformHarness.CreateAsync(database, outbox: o => o.HandlerBatchSize = 100, configure: s =>
        {
            s.AddSingleton(log);
            s.AddEventHandler<WidgetCreatedPayload, CreatedOrderHandler>(WidgetEvents.WidgetCreated, "WRK.WidgetCreated.Order", ModuleCode.WRK);
            s.AddEventHandler<WidgetCreatedPayload, RenamedOrderHandler>(RevEvents.WidgetRenamed, "WRK.WidgetRenamed.Order", ModuleCode.WRK);
        });
        var a = Guid.CreateVersion7().ToString();
        await PublishAsync(harness, (WidgetEvents.WidgetCreated, a), (RevEvents.WidgetRenamed, a), (WidgetEvents.WidgetCreated, a));

        await harness.Processor.DrainAsync(CancellationToken.None);

        log.Seen.Where(s => s.AggregateId == a).Select(s => s.Sequence).ShouldBe([1L, 2L, 3L], "per-aggregate order across handlers: " + string.Join(",", log.Seen.Select(s => $"{s.Handler}:{s.Sequence}")));
    }

    [Fact]
    public async Task R1b_without_micro_batching_order_is_kept()
    {
        var log = new OrderLog();
        await using var harness = await PlatformHarness.CreateAsync(database, outbox: o => o.HandlerBatchSize = 1, configure: s =>
        {
            s.AddSingleton(log);
            s.AddEventHandler<WidgetCreatedPayload, CreatedOrderHandler>(WidgetEvents.WidgetCreated, "WRK.WidgetCreated.Order", ModuleCode.WRK);
            s.AddEventHandler<WidgetCreatedPayload, RenamedOrderHandler>(RevEvents.WidgetRenamed, "WRK.WidgetRenamed.Order", ModuleCode.WRK);
        });
        var a = Guid.CreateVersion7().ToString();
        await PublishAsync(harness, (WidgetEvents.WidgetCreated, a), (RevEvents.WidgetRenamed, a), (WidgetEvents.WidgetCreated, a));

        await harness.Processor.DrainAsync(CancellationToken.None);

        log.Seen.Where(s => s.AggregateId == a).Select(s => s.Sequence).ShouldBe([1L, 2L, 3L]);
    }

    [Fact]
    public async Task R2_a_blocked_aggregate_does_not_starve_other_aggregates()
    {
        await using var harness = await PlatformHarness.CreateAsync(database, outbox: o =>
        {
            o.BatchSize = 5;
            o.HandlerBatchSize = 1;
        });
        var blocked = Guid.CreateVersion7();
        for (var i = 0; i < 20; i++)
        {
            await harness.SendAsync(new CreateWidget($"b{i}", i, blocked));
        }

        var other = Guid.CreateVersion7();
        await harness.SendAsync(new CreateWidget("other", 1m, other));
        harness.Behaviour.FailWhile = e => e.AggregateId == blocked.ToString() && e.AggregateSequence == 1;

        for (var round = 0; round < 10; round++)
        {
            await harness.Processor.ProcessBatchAsync(CancellationToken.None);
        }

        harness.Log.Deliveries.Count(d => d.AggregateId == other.ToString()).ShouldBe(1, "an unrelated aggregate should be delivered while another is in backoff");
    }

    [Fact]
    public async Task R3_dispatcher_crash_mid_batch_then_lease_expiry_delivers_each_event_exactly_once()
    {
        var log = new OrderLog();
        await using var harness = await PlatformHarness.CreateAsync(database, outbox: o =>
        {
            o.HandlerBatchSize = 1;
            o.MaxDegreeOfParallelism = 4;
            o.LeaseDuration = TimeSpan.FromSeconds(60);
        }, configure: s =>
        {
            s.AddSingleton(log);
            s.AddEventHandler<WidgetCreatedPayload, CrashingHandler>(WidgetEvents.WidgetCreated, "WRK.WidgetCreated.Crash", ModuleCode.WRK);
        });
        var aggregates = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7().ToString()).ToArray();
        foreach (var a in aggregates)
        {
            for (var i = 0; i < 6; i++)
            {
                await PublishAsync(harness, (WidgetEvents.WidgetCreated, a));
            }
        }

        log.Cts = new CancellationTokenSource();
        log.Crash = e => e.AggregateId == aggregates[2] && e.AggregateSequence == 4;
        await Should.ThrowAsync<OperationCanceledException>(() => harness.Processor.ProcessBatchAsync(log.Cts.Token));
        log.Crash = _ => false;
        var before = log.Seen.Count;
        before.ShouldBeGreaterThan(0);

        var second = ActivatorUtilities.CreateInstance<OutboxProcessor>(harness.Services);
        (await second.ProcessBatchAsync(CancellationToken.None)).Claimed.ShouldBe(0, "leases still held");
        harness.Clock.Advance(TimeSpan.FromSeconds(61));
        var drained = await second.DrainAsync(CancellationToken.None);

        drained.Retried.ShouldBe(0);
        (await harness.CountAsync("SELECT count(*) FROM tst.delivery WHERE handler = 'crash'")).ShouldBe(30);
        (await harness.CountAsync("SELECT count(*) FROM plt.processed_event WHERE handler = 'WRK.WidgetCreated.Crash'")).ShouldBe(30);
        Console.WriteLine($"R3 invocations={log.Seen.Count}");
        (await harness.CountAsync("SELECT count(*) FROM plt.outbox_message WHERE status = 'Pending'")).ShouldBe(0);
        (await harness.CountAsync("SELECT count(*) FROM plt.event_archive")).ShouldBe(30);
        foreach (var a in aggregates)
        {
            var seq = log.Seen.Where(s => s.AggregateId == a).Select(s => s.Sequence).ToList();
            Console.WriteLine($"R3 {a}: {string.Join(",", seq)}");
            seq.Zip(seq.Skip(1)).ShouldAllBe(p => p.Second >= p.First);
            seq.Distinct().ShouldBe(Enumerable.Range(1, 6).Select(i => (long)i));
        }
    }

    [Fact]
    public async Task R4_concurrent_dispatchers_and_producers_keep_order_and_exactly_once()
    {
        var log = new OrderLog();
        await using var harness = await PlatformHarness.CreateAsync(database, outbox: o =>
        {
            o.BatchSize = 37;
            o.HandlerBatchSize = 10;
            o.MaxDegreeOfParallelism = 4;
        }, configure: s =>
        {
            s.AddSingleton(log);
            s.AddEventHandler<WidgetCreatedPayload, CreatedOrderHandler>(WidgetEvents.WidgetCreated, "WRK.WidgetCreated.Order", ModuleCode.WRK);
        });
        var aggregates = Enumerable.Range(0, 12).Select(_ => Guid.CreateVersion7()).ToArray();
        var producers = Task.WhenAll(aggregates.Select(a => Task.Run(async () =>
        {
            for (var i = 0; i < 25; i++)
            {
                (await harness.SendAsync(new CreateWidget($"c{i}", i, a))).IsSuccess.ShouldBeTrue();
            }
        })));
        var dispatchers = Enumerable.Range(0, 4).Select(_ => ActivatorUtilities.CreateInstance<OutboxProcessor>(harness.Services)).ToArray();
        var running = dispatchers.Select(d => Task.Run(async () =>
        {
            while (!producers.IsCompleted || (await harness.CountAsync("SELECT count(*) FROM plt.outbox_message WHERE status='Pending'")) > 0)
            {
                await d.ProcessBatchAsync(CancellationToken.None);
            }
        })).ToArray();
        await producers;
        await Task.WhenAll(running);

        (await harness.CountAsync("SELECT count(*) FROM plt.processed_event WHERE handler = 'WRK.WidgetCreated.Order'")).ShouldBe(300);
        log.Seen.Count.ShouldBe(300, "each event once");
        (await harness.CountAsync("SELECT count(*) FROM tst.delivery WHERE handler = 'recording'")).ShouldBe(300);
        foreach (var a in aggregates)
        {
            log.Seen.Where(s => s.AggregateId == a.ToString()).Select(s => s.Sequence).ShouldBe(Enumerable.Range(1, 25).Select(i => (long)i));
        }
    }

    [Fact]
    public async Task R5_one_failing_handler_in_a_micro_batch_does_not_duplicate_the_others()
    {
        var log = new OrderLog();
        await using var harness = await PlatformHarness.CreateAsync(database, outbox: o =>
        {
            o.HandlerBatchSize = 100;
            o.MaxAttempts = 2;
        }, configure: s =>
        {
            s.AddSingleton(log);
            s.AddEventHandler<WidgetCreatedPayload, CreatedOrderHandler>(WidgetEvents.WidgetCreated, "WRK.WidgetCreated.Order", ModuleCode.WRK);
        });
        var aggregates = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7()).ToArray();
        foreach (var a in aggregates)
        {
            for (var i = 0; i < 4; i++)
            {
                await harness.SendAsync(new CreateWidget($"f{i}", i, a));
            }
        }

        harness.Behaviour.FailWhile = e => e.AggregateId == aggregates[1].ToString() && e.AggregateSequence == 2;
        await harness.Processor.DrainAsync(CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.Processor.DrainAsync(CancellationToken.None);

        // The order handler ran once per event, whatever the recording handler did.
        log.Seen.Count.ShouldBe(20, string.Join(",", log.Seen.Select(s => s.Sequence)));
        (await harness.CountAsync("SELECT count(*) FROM plt.outbox_dead_letter")).ShouldBe(1);
        (await harness.CountAsync("SELECT count(*) FROM tst.delivery WHERE handler = 'recording'")).ShouldBe(19);
    }

    [Fact]
    public async Task R6_concurrent_audited_commands_chain_without_deadlock()
    {
        await using var harness = await PlatformHarness.CreateAsync(database);
        harness.Clock.Set(Instant.Parse("2026-12-01T09:00:00Z"));
        var shared = Enumerable.Range(0, 3).Select(_ => Guid.CreateVersion7()).ToArray();
        var results = await Task.WhenAll(Enumerable.Range(0, 60).Select(i => Task.Run(() =>
            harness.SendAsync(new CreateWidget($"a{i}", i, shared[i % 3], Fail: i % 7 == 0 ? "result" : string.Empty)))));
        results.Count(r => r.IsSuccess).ShouldBe(51);
        var verification = await harness.Services.GetRequiredService<AuditChainVerifier>().VerifyAsync(new BusinessDate(2026, 12, 1), CancellationToken.None);
        verification.IsValid.ShouldBeTrue(verification.Problem);
        verification.Records.ShouldBe(60);
    }

    [Fact]
    public async Task R7_concurrent_identical_commands_execute_once_and_return_the_same_result()
    {
        await using var harness = await PlatformHarness.CreateAsync(database);
        var key = IdempotencyKey.New();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => harness.SendAsync(new CreateWidget("dup", 4m), key))));
        harness.Log.Executions.Count(e => e == "dup").ShouldBe(1);
        results.ShouldAllBe(r => r.IsSuccess);
        results.Select(r => r.Value).Distinct().Count().ShouldBe(1);
        (await harness.CountAsync("SELECT count(*) FROM tst.widget WHERE name = 'dup'")).ShouldBe(1);
    }

    [Fact]
    public async Task R8_rollback_writes_no_outbox_row_and_no_sequence()
    {
        await using var harness = await PlatformHarness.CreateAsync(database);
        var a = Guid.CreateVersion7();
        await harness.SendAsync(new CreateWidget("r", 1m, a, Fail: "result"));
        await Should.ThrowAsync<InvalidOperationException>(() => harness.SendAsync(new CreateWidget("t", 1m, a, Fail: "throw")));
        (await harness.CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{a}'")).ShouldBe(0);
        (await harness.CountAsync($"SELECT count(*) FROM plt.aggregate_sequence WHERE aggregate_id = '{a}'")).ShouldBe(0);
        await harness.SendAsync(new CreateWidget("ok", 1m, a));
        (await harness.CountAsync($"SELECT max(aggregate_sequence) FROM plt.outbox_message WHERE aggregate_id = '{a}'")).ShouldBe(1);
    }

    [Theory]
    [InlineData("TRUNCATE plt.audit_event CASCADE")]
    [InlineData("ALTER TABLE plt.audit_event DISABLE TRIGGER ALL")]
    public async Task R9_app_role_more_tamper_attempts(string sql)
    {
        await using var harness = await PlatformHarness.CreateAsync(database);
        await harness.SendAsync(new CreateWidget("p", 1m));
        await using var command = harness.DataSource.CreateCommand(sql);
        var ex = await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync(CancellationToken.None));
        Console.WriteLine($"R9 {sql} -> {ex?.GetType().Name}: {(ex as PostgresException)?.SqlState} {ex?.Message}");
        if (!sql.StartsWith("UPDATE", StringComparison.Ordinal))
        {
            ex.ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task M6_a_dispatcher_whose_lease_was_taken_over_cannot_record_a_retry()
    {
        var gate = new GateHandler.Gate();
        await using var harness = await PlatformHarness.CreateAsync(database, outbox: o =>
        {
            o.HandlerBatchSize = 1;
            o.MaxDegreeOfParallelism = 1;
            o.LeaseDuration = TimeSpan.FromSeconds(60);
        }, configure: s =>
        {
            s.AddSingleton(gate);
            s.AddEventHandler<WidgetCreatedPayload, GateHandler>(WidgetEvents.WidgetCreated, "WRK.WidgetCreated.Gate", ModuleCode.WRK);
        });
        harness.Behaviour.FailWhile = _ => true;
        await harness.SendAsync(new CreateWidget("lease", 1m));

        // A claims and hangs in the handler past its lease; B takes the event over and records the first failure.
        var stale = Task.Run(() => harness.Processor.ProcessBatchAsync(CancellationToken.None), CancellationToken.None);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromSeconds(61));
        var current = ActivatorUtilities.CreateInstance<OutboxProcessor>(harness.Services);
        var takeover = Task.Run(() => current.ProcessBatchAsync(CancellationToken.None), CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
        gate.Release.TrySetResult();

        var results = await Task.WhenAll(stale, takeover);

        results.ShouldAllBe(r => r.Claimed == 1 && r.Retried == 1, "both dispatchers failed the event once");
        (await harness.CountAsync("SELECT attempts FROM plt.outbox_message WHERE status = 'Pending'")).ShouldBe(1, "only the lease owner's retry counts");
    }
}

/// <summary>The first invocation signals and waits for the test, then fails; later invocations fail at once.</summary>
internal sealed class GateHandler(GateHandler.Gate gate) : IEventHandler<WidgetCreatedPayload>
{
    public async Task HandleAsync(EventEnvelope envelope, WidgetCreatedPayload payload, CancellationToken cancellationToken)
    {
        if (gate.Entered.TrySetResult())
        {
            await gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }

        throw new InvalidOperationException("gate handler failure");
    }

    internal sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
