using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel;
using Npgsql;

namespace CoreIns.IntegrationTests.Platform;

/// <summary>Transactional outbox on a real PostgreSQL 17 (D-ARC-02, ADR §2 rule 7).</summary>
public sealed class OutboxTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() =>
        await database.ExecuteAsSuperuserAsync(
            "TRUNCATE plt.outbox_message, plt.aggregate_sequence, plt.processed_event, plt.outbox_dead_letter, plt.event_archive, plt.idempotency_record",
            CancellationToken.None);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_event_commits_with_the_domain_change_in_one_transaction()
    {
        await using var harness = await PlatformHarness.CreateAsync(database);

        var ok = await harness.SendAsync(new CreateWidget("committed", 10m));
        var refused = await harness.SendAsync(new CreateWidget("refused", 10m, Fail: "result"));
        await Should.ThrowAsync<InvalidOperationException>(() => harness.SendAsync(new CreateWidget("thrown", 10m, Fail: "throw")));
        var dryRun = await harness.SendAsync(new CreateWidget("dry", 10m), dryRun: true);

        ok.IsSuccess.ShouldBeTrue();
        refused.IsFailure.ShouldBeTrue();
        dryRun.IsSuccess.ShouldBeTrue();
        (await harness.CountAsync("SELECT count(*) FROM tst.widget WHERE name IN ('committed')")).ShouldBe(1);
        (await harness.CountAsync("SELECT count(*) FROM tst.widget WHERE name IN ('refused', 'thrown', 'dry')")).ShouldBe(0);
        (await harness.CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{ok.Value.WidgetId}'")).ShouldBe(1);
        (await harness.CountAsync("SELECT count(*) FROM plt.outbox_message")).ShouldBe(1);
    }

    [Fact]
    public async Task The_envelope_is_complete_and_the_database_guards_it()
    {
        await using var harness = await PlatformHarness.CreateAsync(database);
        var created = (await harness.SendAsync(new CreateWidget("envelope", 12.5m))).Value;

        await using (var command = harness.DataSource.CreateCommand(
            $"SELECT {EnvelopeColumnsReader.Columns} FROM plt.outbox_message WHERE aggregate_id = '{created.WidgetId}'"))
        await using (var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
            var envelope = EnvelopeColumnsReader.Read(reader);
            envelope.Validate(WidgetEvents.WidgetCreated).ShouldBeEmpty();
            envelope.AggregateSequence.ShouldBe(1);
            envelope.Actor.ShouldBe(CoreIns.SharedKernel.Identifiers.ActorRef.User("user-1"));
            envelope.ConfigurationHash.ShouldBe(PlatformHarness.Hash);
            envelope.LegalEntity.Value.ShouldBe("GR-TEST");
            envelope.Origin.ShouldBe(EventOrigin.Live);
            envelope.AiInteractionId.ShouldBeNull();
            envelope.PayloadAs<WidgetCreatedPayload>().Amount.ShouldBe(Money.Of(12.5m, "EUR"));
            envelope.Payload["amount"]!["amount"]!.GetValue<string>().ShouldBe("12.5");
        }

        // The table refuses what the C# guard refuses: index above set_size, partial set fields, empty business keys.
        foreach (var bad in new[]
                 {
                     "set_id = gen_random_uuid(), set_size = 2, set_index = 3",
                     "set_id = gen_random_uuid(), set_size = NULL, set_index = 1",
                     "business_keys = '{}'::jsonb",
                     "aggregate_sequence = 0",
                     "origin = 'BATCH'",
                 })
        {
            var error = await Should.ThrowAsync<PostgresException>(() => database.ExecuteAsSuperuserAsync(
                $"UPDATE plt.outbox_message SET {bad} WHERE aggregate_id = '{created.WidgetId}'", TestContext.Current.CancellationToken));
            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }
    }

    [Fact]
    public async Task Concurrent_writers_of_one_aggregate_get_a_gap_free_sequence()
    {
        await using var harness = await PlatformHarness.CreateAsync(database);
        var aggregate = Guid.CreateVersion7();

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() =>
            harness.SendAsync(new CreateWidget($"w{i}", i, aggregate, Fail: i % 4 == 0 ? "result" : string.Empty)))));

        results.Count(r => r.IsSuccess).ShouldBe(30);
        var sequences = new List<long>();
        await using (var command = harness.DataSource.CreateCommand(
            $"SELECT aggregate_sequence FROM plt.outbox_message WHERE aggregate_id = '{aggregate}' ORDER BY position"))
        await using (var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                sequences.Add(reader.GetInt64(0));
            }
        }

        sequences.ShouldBe(Enumerable.Range(1, 30).Select(i => (long)i), "sequence is gap-free and follows commit order");
        (await harness.CountAsync($"SELECT last_sequence FROM plt.aggregate_sequence WHERE aggregate_id = '{aggregate}'")).ShouldBe(30);
    }

    [Fact]
    public async Task Events_are_delivered_once_per_handler_in_aggregate_order_and_archived()
    {
        await using var harness = await PlatformHarness.CreateAsync(database, outbox: o => o.BatchSize = 7);
        var aggregates = Enumerable.Range(0, 6).Select(_ => Guid.CreateVersion7()).ToArray();
        await Task.WhenAll(aggregates.Select(a => Task.Run(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                (await harness.SendAsync(new CreateWidget($"o{i}", i, a))).IsSuccess.ShouldBeTrue();
            }
        })));

        var drained = await harness.Processor.DrainAsync(TestContext.Current.CancellationToken);

        drained.Dispatched.ShouldBe(60);
        harness.Log.Deliveries.Count.ShouldBe(60);
        foreach (var aggregate in aggregates)
        {
            harness.Log.Deliveries.Where(d => d.AggregateId == aggregate.ToString()).Select(d => d.Sequence)
                .ShouldBe(Enumerable.Range(1, 10).Select(i => (long)i), $"aggregate {aggregate} in order");
        }

        (await harness.CountAsync("SELECT count(*) FROM plt.outbox_message WHERE status = 'Pending'")).ShouldBe(0);
        (await harness.CountAsync("SELECT count(*) FROM plt.event_archive")).ShouldBe(60);
        (await harness.CountAsync($"SELECT count(*) FROM plt.processed_event WHERE handler = '{PlatformHarness.HandlerName}'")).ShouldBe(60);

        // Idempotent consumption: a re-delivered event (e.g. after a lost lease) is skipped by the processed marker.
        await database.ExecuteAsSuperuserAsync("UPDATE plt.outbox_message SET status = 'Pending', dispatched_at = NULL", TestContext.Current.CancellationToken);
        var again = await harness.Processor.DrainAsync(TestContext.Current.CancellationToken);
        again.Dispatched.ShouldBe(60);
        harness.Log.Deliveries.Count.ShouldBe(60);
    }

    [Fact]
    public async Task A_failing_event_holds_back_later_events_of_its_aggregate_and_is_retried_with_backoff()
    {
        await using var harness = await PlatformHarness.CreateAsync(database, outbox: o =>
        {
            o.MaxAttempts = 5;
            o.HandlerBatchSize = 1;
        });
        var blocked = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        for (var i = 0; i < 3; i++)
        {
            await harness.SendAsync(new CreateWidget($"b{i}", i, blocked));
            await harness.SendAsync(new CreateWidget($"x{i}", i, other));
        }

        harness.Behaviour.FailWhile = e => e.AggregateId == blocked.ToString() && e.AggregateSequence == 2;
        var first = await harness.Processor.DrainAsync(TestContext.Current.CancellationToken);

        first.Retried.ShouldBe(1);
        harness.Log.Deliveries.Where(d => d.AggregateId == blocked.ToString()).Select(d => d.Sequence).ShouldBe([1L]);
        harness.Log.Deliveries.Count(d => d.AggregateId == other.ToString()).ShouldBe(3);
        (await harness.CountAsync($"SELECT attempts FROM plt.outbox_message WHERE aggregate_id = '{blocked}' AND aggregate_sequence = 2")).ShouldBe(1);

        // Not yet due: nothing moves. After the 1 s backoff and a fix, the aggregate continues in order.
        (await harness.Processor.ProcessBatchAsync(TestContext.Current.CancellationToken)).Dispatched.ShouldBe(0);
        harness.Behaviour.FailWhile = _ => false;
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await harness.Processor.DrainAsync(TestContext.Current.CancellationToken);

        harness.Log.Deliveries.Where(d => d.AggregateId == blocked.ToString()).Select(d => d.Sequence).ShouldBe([1L, 2L, 3L]);
    }

    [Fact]
    public async Task A_failing_micro_batch_falls_back_to_single_events_and_commits_each_effect_once()
    {
        await using var harness = await PlatformHarness.CreateAsync(database, outbox: o =>
        {
            o.HandlerBatchSize = 50;
            o.MaxDegreeOfParallelism = 2;
        });
        var aggregates = Enumerable.Range(0, 20).Select(_ => Guid.CreateVersion7()).ToArray();
        foreach (var aggregate in aggregates)
        {
            for (var i = 0; i < 3; i++)
            {
                await harness.SendAsync(new CreateWidget($"m{i}", i, aggregate));
            }
        }

        var poisoned = aggregates[7].ToString();
        harness.Behaviour.FailWhile = e => e.AggregateId == poisoned && e.AggregateSequence == 2;
        var first = await harness.Processor.DrainAsync(TestContext.Current.CancellationToken);

        first.Retried.ShouldBe(1);
        (await harness.CountAsync("SELECT count(*) FROM tst.delivery")).ShouldBe(58, "every event but the failing one and the one waiting behind it");
        (await harness.CountAsync($"SELECT count(*) FROM plt.processed_event WHERE handler = '{PlatformHarness.HandlerName}'")).ShouldBe(58);

        harness.Behaviour.FailWhile = _ => false;
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await harness.Processor.DrainAsync(TestContext.Current.CancellationToken);

        (await harness.CountAsync("SELECT count(*) FROM tst.delivery")).ShouldBe(60);
        (await harness.CountAsync("SELECT count(*) FROM plt.outbox_message WHERE status = 'Pending'")).ShouldBe(0);
    }

    [Fact]
    public async Task A_handler_that_keeps_failing_is_dead_lettered_and_can_be_replayed()
    {
        await using var harness = await PlatformHarness.CreateAsync(database, outbox: o =>
        {
            o.MaxAttempts = 3;
            o.HandlerBatchSize = 1;
        });
        var aggregate = Guid.CreateVersion7();
        await harness.SendAsync(new CreateWidget("poison", 1m, aggregate));
        await harness.SendAsync(new CreateWidget("after", 2m, aggregate));
        harness.Behaviour.FailWhile = e => e.AggregateSequence == 1 && e.AggregateId == aggregate.ToString();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await harness.Processor.DrainAsync(TestContext.Current.CancellationToken);
            harness.Clock.Advance(TimeSpan.FromMinutes(5));
        }

        (await harness.CountAsync("SELECT count(*) FROM plt.outbox_dead_letter WHERE status = 'Parked'")).ShouldBe(1);
        (await harness.CountAsync("SELECT count(*) FROM plt.outbox_message WHERE event_type = 'DeadLetterParked' AND producer = 'PLT'")).ShouldBe(1);
        (await harness.CountAsync($"SELECT count(*) FROM plt.event_archive WHERE aggregate_id = '{aggregate}'")).ShouldBe(2);
        harness.Log.Deliveries.Where(d => d.AggregateId == aggregate.ToString()).Select(d => d.Sequence).ShouldBe([2L], "the aggregate continues after parking");

        harness.Behaviour.FailWhile = _ => false;
        var deadLetter = Guid.Empty;
        await using (var command = harness.DataSource.CreateCommand("SELECT dead_letter_id FROM plt.outbox_dead_letter"))
        {
            deadLetter = (Guid)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }

        var replay = harness.Services.GetService(typeof(OutboxReplayService)) as OutboxReplayService;
        (await replay!.ReplayDeadLetterAsync(deadLetter, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await harness.CountAsync("SELECT count(*) FROM plt.outbox_dead_letter WHERE status = 'Replayed'")).ShouldBe(1);
        harness.Log.Deliveries.Where(d => d.AggregateId == aggregate.ToString()).Select(d => d.Sequence).ShouldBe([2L, 1L]);
    }

    [Fact]
    public async Task Replay_redelivers_archived_events_marked_as_replay_and_purge_keeps_the_archive()
    {
        await using var harness = await PlatformHarness.CreateAsync(database);
        var aggregate = Guid.CreateVersion7();
        for (var i = 0; i < 4; i++)
        {
            await harness.SendAsync(new CreateWidget($"r{i}", i, aggregate));
        }

        await harness.Processor.DrainAsync(TestContext.Current.CancellationToken);
        var replay = (OutboxReplayService)harness.Services.GetService(typeof(OutboxReplayService))!;

        var replayed = await replay.ReplayAsync(PlatformHarness.HandlerName, new ReplayFilter { AggregateType = "Widget", AggregateId = aggregate.ToString() },
            TestContext.Current.CancellationToken);

        replayed.ShouldBe(4);
        harness.Log.Deliveries.Where(d => d.Origin == EventOrigin.Replay).Select(d => d.Sequence).ShouldBe([1L, 2L, 3L, 4L]);
        (await harness.CountAsync($"SELECT sum(replay_count) FROM plt.processed_event WHERE handler = '{PlatformHarness.HandlerName}'")).ShouldBe(4);

        harness.Clock.Advance(TimeSpan.FromDays(8));
        (await replay.PurgeDispatchedAsync(harness.Clock.Now.Minus(TimeSpan.FromDays(7)), TestContext.Current.CancellationToken)).ShouldBe(4);
        (await harness.CountAsync("SELECT count(*) FROM plt.outbox_message")).ShouldBe(0);
        (await harness.CountAsync("SELECT count(*) FROM plt.event_archive")).ShouldBe(4);
    }
}
