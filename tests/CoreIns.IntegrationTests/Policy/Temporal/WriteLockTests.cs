using CoreIns.SharedKernel;

namespace CoreIns.IntegrationTests.Policy.Temporal;

/// <summary>
/// The writer side of D-SL3-03 (a): lock, then stamp. Several real writers (own scope, connection and transaction each) hit one
/// policy through a barrier; the lock serialises them, the record times are strictly increasing, and nobody gets a server error.
/// </summary>
public sealed class WriteLockTests(PostgresFixture database) : TemporalTestBase(database)
{
    private static readonly TimeSpan Micro = TimeSpan.FromTicks(10);

    [Fact]
    public async Task REQ_POL_002_concurrent_writers_on_one_policy_get_strictly_increasing_record_times_and_none_fails()
    {
        const int writers = 8;
        var p = await BindAsync();
        var before = await WatermarkAsync(p.PolicyId);

        // Every replica's clock reads the same instant, after the watermark: the +1 µs rule is the only thing keeping times apart.
        var shared = new FakeClock(Instant.FromUtcDateTime(before.ToUtcDateTime().AddHours(1)));
        var barrier = new AsyncBarrier(writers);
        var tasks = Enumerable.Range(0, writers).Select(_ => Task.Run(async () =>
        {
            await barrier.SignalAndWaitAsync();
            return await WriteAsync(p.PolicyId, shared, TimeSpan.FromSeconds(30));
        }, Ct)).ToArray();
        var results = await Task.WhenAll(tasks);

        results.ShouldAllBe(r => r.IsSuccess, string.Join("; ", results.Where(r => r.IsFailure).Select(r => r.Error!.ToString())));
        var times = results.Select(r => r.Value.ToUtcDateTime()).Order().ToArray();
        times.Distinct().Count().ShouldBe(writers);
        for (var i = 0; i < writers; i++)
        {
            times[i].ShouldBe(shared.Now.ToUtcDateTime().AddTicks(10 * i), $"writer {i}");
        }

        (await WatermarkAsync(p.PolicyId)).ShouldBe(Instant.FromUtcDateTime(times[^1]));
    }

    [Fact]
    public async Task REQ_POL_086_the_loser_of_a_race_gets_POL_ERR_STALE_not_an_exception()
    {
        var p = await BindAsync();
        var winnerHasLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var losersDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new FakeClock(Instant.FromUtcDateTime((await WatermarkAsync(p.PolicyId)).ToUtcDateTime().AddHours(1)));

        var winner = Task.Run(() => WriteAsync(p.PolicyId, clock, TimeSpan.FromSeconds(30), whileOpen: async _ =>
        {
            winnerHasLock.SetResult();
            await losersDone.Task; // keeps the transaction (and the row lock) open until the losers gave up
        }), Ct);
        await winnerHasLock.Task;

        // The lock wait here is the shortest the option allows (1 s): the winner does not release before the losers are done.
        var barrier = new AsyncBarrier(3);
        var losers = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            await barrier.SignalAndWaitAsync();
            return await WriteAsync(p.PolicyId, clock, TimeSpan.FromSeconds(1));
        }, Ct)));
        losersDone.SetResult();
        var won = await winner;

        won.IsSuccess.ShouldBeTrue();
        foreach (var loser in losers)
        {
            loser.IsFailure.ShouldBeTrue("the lock was held for longer than the wait");
            loser.Error!.Code.ToString().ShouldBe("POL-ERR-STALE");
        }

        // The losers wrote nothing: the watermark is the winner's alone.
        (await WatermarkAsync(p.PolicyId)).ShouldBe(won.Value);

        // And the policy is writable again.
        (await WriteAsync(p.PolicyId, clock, TimeSpan.FromSeconds(5))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task REQ_POL_002_a_writer_whose_clock_is_behind_still_stamps_above_the_watermark()
    {
        var p = await BindAsync();
        var watermark = await WatermarkAsync(p.PolicyId);
        var behind = new FakeClock(Instant.FromUtcDateTime(watermark.ToUtcDateTime().AddHours(-1)));

        var first = await WriteAsync(p.PolicyId, behind, TimeSpan.FromSeconds(5));
        first.Value.ShouldBe(watermark + Micro);

        var second = await WriteAsync(p.PolicyId, behind, TimeSpan.FromSeconds(5));
        second.Value.ShouldBe(watermark + Micro + Micro);
        (await WatermarkAsync(p.PolicyId)).ShouldBe(second.Value);
    }

    [Fact]
    public async Task REQ_POL_002_a_clock_ahead_of_the_watermark_stamps_at_the_clock_truncated_to_the_microsecond()
    {
        var p = await BindAsync();
        var watermark = await WatermarkAsync(p.PolicyId);
        var ahead = new FakeClock(Instant.FromUtcDateTime(watermark.ToUtcDateTime().AddMinutes(5).AddTicks(7)));

        var written = await WriteAsync(p.PolicyId, ahead, TimeSpan.FromSeconds(5));

        written.Value.ToUtcDateTime().ShouldBe(watermark.ToUtcDateTime().AddMinutes(5));
    }

    [Fact]
    public async Task REQ_POL_002_a_rolled_back_writer_leaves_the_watermark_where_it_was()
    {
        var p = await BindAsync();
        var watermark = await WatermarkAsync(p.PolicyId);
        var clock = new FakeClock(Instant.FromUtcDateTime(watermark.ToUtcDateTime().AddHours(1)));

        await Should.ThrowAsync<InvalidOperationException>(() => WriteAsync(
            p.PolicyId, clock, TimeSpan.FromSeconds(5), whileOpen: _ => throw new InvalidOperationException("command failed after taking the lock")));

        (await WatermarkAsync(p.PolicyId)).ShouldBe(watermark);
    }

    [Fact]
    public async Task REQ_POL_002_an_unknown_policy_is_not_found_not_a_crash()
    {
        var clock = new FakeClock(Instant.FromUtcDateTime(DateTime.UtcNow));
        var result = await WriteAsync(Guid.CreateVersion7(), clock, TimeSpan.FromSeconds(5));
        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ToString().ShouldBe("POL-ERR-NOT-FOUND");
    }
}
