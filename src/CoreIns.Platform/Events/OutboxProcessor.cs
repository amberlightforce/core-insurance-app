using System.Collections.Concurrent;
using CoreIns.Platform.Context;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace CoreIns.Platform.Events;

/// <summary>Outbox dispatcher settings (<c>Platform:Outbox</c>). Retry defaults follow REQ-PLT-143 / BR-PLT-019.</summary>
public sealed class OutboxOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Platform:Outbox";

    /// <summary>Messages claimed per batch.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Aggregates processed in parallel (events of one aggregate are always sequential).</summary>
    public int MaxDegreeOfParallelism { get; set; } = 8;

    /// <summary>
    /// Events one handler processes in a single transaction (processed markers and effects commit together). A failing
    /// micro-batch is rolled back and its events are retried one by one, so failures stay isolated. 1 disables batching.
    /// </summary>
    public int HandlerBatchSize { get; set; } = 100;

    /// <summary>Wait between polls when the outbox is empty.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a claim is exclusive; an expired lease is reclaimed (handlers are idempotent).</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Attempts before failing handlers are parked in the dead-letter table (REQ-PLT-143 default 8).</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>First retry delay; doubles per attempt (BR-PLT-019 base 1 s).</summary>
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest retry delay (BR-PLT-019 cap 5 min).</summary>
    public TimeSpan BackoffCap { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The delay before attempt <paramref name="attempt"/> + 1 (attempt is 1-based), exponential and capped.</summary>
    public TimeSpan Backoff(int attempt)
    {
        var shift = Math.Clamp(attempt - 1, 0, 30);
        var ticks = BackoffBase.Ticks;
        return ticks > BackoffCap.Ticks >> shift ? BackoffCap : TimeSpan.FromTicks(ticks << shift);
    }
}

/// <summary>What one dispatch round did.</summary>
/// <param name="Claimed">Messages claimed.</param>
/// <param name="Dispatched">Messages completed (all handlers done or parked) and archived.</param>
/// <param name="Retried">Messages scheduled for another attempt.</param>
/// <param name="Parked">Handler failures parked in the dead-letter table.</param>
/// <param name="Released">Claims given back because an earlier event of the aggregate is still pending.</param>
public sealed record OutboxBatchResult(int Claimed, int Dispatched, int Retried, int Parked, int Released)
{
    /// <summary>An empty round.</summary>
    public static OutboxBatchResult Empty { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>
/// The outbox dispatcher (worker role, D-ARC-02): claims pending messages with <c>FOR UPDATE SKIP LOCKED</c> and a
/// lease, delivers each aggregate's events strictly in <c>aggregate_sequence</c> order to every registered in-process
/// handler, retries failures with exponential backoff (later events of that aggregate wait), parks handlers that keep
/// failing in <c>plt.outbox_dead_letter</c> (raising <c>DeadLetterParked</c>), and archives every completed message in
/// <c>plt.event_archive</c>. Several dispatchers may run at once.
/// <para><b>Dead-letter semantics (D-ARC-26).</b> Retries before parking keep the aggregate's order: later events of the
/// aggregate wait. Parking does <b>not</b> hold the aggregate: after the last attempt the failing handler's event is
/// parked and the aggregate's later events are delivered. A parked event that is replayed later therefore arrives
/// <b>out of order</b> (after events with a higher <c>aggregateSequence</c>). Handlers whose effects depend on order
/// must compare <see cref="EventEnvelope.AggregateSequence"/> with the last sequence they applied for the aggregate and
/// ignore or reconcile stale events.</para>
/// </summary>
public sealed partial class OutboxProcessor
{
    // Only claimable runs: a message is skipped while an earlier undispatched event of its aggregate is in backoff or
    // leased by another dispatcher, so a blocked aggregate never fills the batch and starves the others. The blocked
    // aggregates come from two small index ranges (backoff, live leases), so the claim stays a walk of the pending
    // queue in position order however long the backlog is.
    private const string ClaimSql = $"""
        WITH blocked AS MATERIALIZED (
            SELECT b.aggregate_type, b.aggregate_id, min(b.aggregate_sequence) AS from_sequence
            FROM (
                SELECT aggregate_type, aggregate_id, aggregate_sequence FROM plt.outbox_message
                WHERE status = 'Pending' AND next_attempt_at > @now
                UNION ALL
                SELECT aggregate_type, aggregate_id, aggregate_sequence FROM plt.outbox_message
                WHERE status = 'Pending' AND lease_until >= @now) b
            GROUP BY b.aggregate_type, b.aggregate_id),
        candidates AS (
            SELECT m.event_id AS claim_id FROM plt.outbox_message m
            WHERE m.status = 'Pending' AND m.next_attempt_at <= @now AND (m.lease_until IS NULL OR m.lease_until < @now)
              AND NOT EXISTS (
                SELECT 1 FROM blocked x
                WHERE x.aggregate_type = m.aggregate_type AND x.aggregate_id = m.aggregate_id AND x.from_sequence < m.aggregate_sequence)
            ORDER BY m.position
            LIMIT @batch
            FOR UPDATE OF m SKIP LOCKED)
        UPDATE plt.outbox_message m SET lease_until = @lease_until, lease_owner = @owner
        FROM candidates c WHERE m.event_id = c.claim_id
        RETURNING m.position, m.attempts, {EnvelopeColumnsReader.Columns}
        """;

    private const string HeadsSql = """
        SELECT aggregate_type, aggregate_id, min(aggregate_sequence)
        FROM plt.outbox_message
        WHERE status = 'Pending' AND (aggregate_type, aggregate_id) IN (SELECT * FROM unnest(@types, @ids))
        GROUP BY aggregate_type, aggregate_id
        """;

    private const string CompleteSql = """
        WITH done AS (
            UPDATE plt.outbox_message
            SET status = 'Dispatched', dispatched_at = @now, lease_until = NULL, lease_owner = NULL, last_error = NULL
            WHERE event_id = ANY(@ids) AND status = 'Pending'
            RETURNING *)
        INSERT INTO plt.event_archive (
            event_id, event_type, schema_version, producer, aggregate_type, aggregate_id, aggregate_sequence, occurred_at,
            recorded_at, legal_entity, jurisdiction, configuration_hash, business_keys, correlation_id, causation_id,
            actor_kind, actor_id, ai_interaction_id, origin, data_classification, set_id, set_size, set_index, payload,
            position, archived_at)
        SELECT event_id, event_type, schema_version, producer, aggregate_type, aggregate_id, aggregate_sequence, occurred_at,
            recorded_at, legal_entity, jurisdiction, configuration_hash, business_keys, correlation_id, causation_id,
            actor_kind, actor_id, ai_interaction_id, origin, data_classification, set_id, set_size, set_index, payload,
            position, @now
        FROM done
        ON CONFLICT (event_id) DO NOTHING
        """;

    private const string RetrySql = """
        UPDATE plt.outbox_message m
        SET attempts = m.attempts + 1, next_attempt_at = u.next_at, last_error = u.error, lease_until = NULL, lease_owner = NULL
        FROM unnest(@ids, @next_at, @errors) AS u(id, next_at, error)
        WHERE m.event_id = u.id AND m.status = 'Pending' AND m.lease_owner = @owner
        """;

    private const string ReleaseSql = """
        UPDATE plt.outbox_message SET lease_until = NULL, lease_owner = NULL
        WHERE event_id = ANY(@ids) AND lease_owner = @owner AND status = 'Pending'
        """;

    private const string ParkSql = """
        INSERT INTO plt.outbox_dead_letter (
            dead_letter_id, handler, event_id, event_type, aggregate_type, aggregate_id, error_class, error_message,
            attempts, parked_at, status)
        SELECT u.id, u.handler, u.event_id, u.event_type, u.aggregate_type, u.aggregate_id, u.error_class, u.error_message,
            u.attempts, @now, 'Parked'
        FROM unnest(@id, @handler, @event_id, @event_type, @aggregate_type, @aggregate_id, @error_class, @error_message, @attempts)
            AS u(id, handler, event_id, event_type, aggregate_type, aggregate_id, error_class, error_message, attempts)
        ON CONFLICT DO NOTHING
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly HandlerInvoker _invoker;
    private readonly EventHandlerRegistry _registry;
    private readonly IClock _clock;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxProcessor> _logger;
    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    /// <summary>Creates the processor.</summary>
    public OutboxProcessor(
        NpgsqlDataSource dataSource,
        IServiceScopeFactory scopes,
        EventHandlerRegistry registry,
        IClock clock,
        IOptions<OutboxOptions> options,
        ILogger<OutboxProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dataSource = dataSource;
        _registry = registry;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
        _invoker = new HandlerInvoker(scopes);
    }

    /// <summary>Runs dispatch rounds until a round claims nothing; returns the totals (tests, benchmark, catch-up).</summary>
    public async Task<OutboxBatchResult> DrainAsync(CancellationToken cancellationToken)
    {
        var total = OutboxBatchResult.Empty;
        while (true)
        {
            var round = await ProcessBatchAsync(cancellationToken).ConfigureAwait(false);
            total = new OutboxBatchResult(
                total.Claimed + round.Claimed, total.Dispatched + round.Dispatched, total.Retried + round.Retried,
                total.Parked + round.Parked, total.Released + round.Released);
            if (round.Claimed == 0 || (round.Dispatched == 0 && round.Retried == 0 && round.Parked == 0))
            {
                return total;
            }
        }
    }

    /// <summary>One dispatch round.</summary>
    public async Task<OutboxBatchResult> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var now = _clock.Now;
        var claimed = await ClaimAsync(now, cancellationToken).ConfigureAwait(false);
        if (claimed.Count == 0)
        {
            return OutboxBatchResult.Empty;
        }

        var heads = await HeadsAsync(claimed, cancellationToken).ConfigureAwait(false);
        var runs = new List<List<ClaimedMessage>>();
        var released = new List<Guid>();
        foreach (var aggregate in claimed.GroupBy(c => (c.Envelope.AggregateType, c.Envelope.AggregateId)))
        {
            var ordered = aggregate.OrderBy(c => c.Envelope.AggregateSequence).ToList();
            var expected = heads.TryGetValue(aggregate.Key, out var head) ? head : ordered[0].Envelope.AggregateSequence;
            var run = new List<ClaimedMessage>();
            foreach (var message in ordered)
            {
                if (message.Envelope.AggregateSequence == expected)
                {
                    run.Add(message);
                    expected++;
                }
                else
                {
                    released.Add(message.Envelope.EventId.Value);
                }
            }

            if (run.Count > 0)
            {
                runs.Add(run);
            }
        }

        var outcome = new RoundOutcome();
        await Parallel.ForEachAsync(
            Lanes(runs),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.MaxDegreeOfParallelism), CancellationToken = cancellationToken },
            async (lane, ct) => await ProcessLaneAsync(lane, outcome, ct).ConfigureAwait(false)).ConfigureAwait(false);

        released.AddRange(outcome.Released);
        await FinishAsync(outcome, released, cancellationToken).ConfigureAwait(false);
        return new OutboxBatchResult(claimed.Count, outcome.Completed.Count, outcome.Retries.Count, outcome.Parked.Count, released.Count);
    }

    /// <summary>Splits the aggregate runs into lanes (one per parallel worker), each with at most one micro-batch of runs.</summary>
    private List<List<List<ClaimedMessage>>> Lanes(List<List<ClaimedMessage>> runs)
    {
        var dop = Math.Max(1, _options.MaxDegreeOfParallelism);
        var size = Math.Max(1, Math.Min(Math.Max(1, _options.HandlerBatchSize), (runs.Count + dop - 1) / dop));
        return [.. runs.Chunk(size).Select(chunk => chunk.ToList())];
    }

    /// <summary>
    /// Processes a lane of aggregate runs in waves: wave k holds the k-th event of every run still active. Within a wave
    /// every handler runs, one micro-batch transaction per handler (markers and effects commit together); a failing
    /// micro-batch is retried event by event. Wave k+1 starts only after wave k is done for every handler, so all of a
    /// module's handlers — whatever event types they subscribe to — see each aggregate's events in sequence order. An
    /// event that still fails is retried later (the rest of its run is released) or, after the last attempt, parked
    /// (its aggregate continues, D-ARC-26).
    /// </summary>
    private async Task ProcessLaneAsync(List<List<ClaimedMessage>> lane, RoundOutcome outcome, CancellationToken cancellationToken)
    {
        var stopped = new HashSet<int>();
        for (var k = 0; ; k++)
        {
            var wave = lane.Select((run, index) => (Run: index, Message: k < run.Count && !stopped.Contains(index) ? run[k] : null))
                .Where(w => w.Message is not null)
                .Select(w => (w.Run, Message: w.Message!))
                .ToList();
            if (wave.Count == 0)
            {
                return;
            }

            var failures = new Dictionary<Guid, List<(EventHandlerRegistration Handler, Exception Error)>>();
            var handlers = wave.Select(w => w.Message.Envelope.RoutingKey).Distinct(StringComparer.Ordinal).SelectMany(_registry.For).Distinct().ToList();
            foreach (var handler in handlers)
            {
                var own = wave.Select(w => w.Message.Envelope)
                    .Where(e => string.Equals(e.RoutingKey, handler.RoutingKey, StringComparison.Ordinal))
                    .ToList();
                if (_options.HandlerBatchSize > 1 && own.Count > 1)
                {
                    try
                    {
                        await _invoker.InvokeBatchAsync(handler, own, _clock, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        LogBatchFailed(_logger, ex, handler.HandlerName, own.Count);
                    }
                }

                foreach (var envelope in own)
                {
                    try
                    {
                        await _invoker.InvokeAsync(handler, envelope, replay: false, _clock, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        LogHandlerFailed(_logger, ex, handler.HandlerName, envelope.EventType.Value, envelope.EventId.Value);
                        if (!failures.TryGetValue(envelope.EventId.Value, out var list))
                        {
                            failures[envelope.EventId.Value] = list = [];
                        }

                        list.Add((handler, ex));
                    }
                }
            }

            foreach (var (run, message) in wave)
            {
                if (!failures.TryGetValue(message.Envelope.EventId.Value, out var failed))
                {
                    outcome.Completed.Add(message.Envelope.EventId.Value);
                    continue;
                }

                var attempt = message.Attempts + 1;
                if (attempt >= _options.MaxAttempts)
                {
                    foreach (var (handler, error) in failed)
                    {
                        outcome.Parked.Add(new ParkedFailure(message.Envelope, handler.HandlerName, error, attempt));
                    }

                    outcome.Completed.Add(message.Envelope.EventId.Value);
                    continue;
                }

                var summary = string.Join("; ", failed.Select(f => $"{f.Handler.HandlerName}: {ErrorText.Of(f.Error)}"));
                outcome.Retries.Add((message.Envelope.EventId.Value, _clock.Now.Plus(_options.Backoff(attempt)), ErrorText.Truncate(summary)));

                // Order per aggregate: later events wait for this one.
                stopped.Add(run);
                foreach (var later in lane[run].Skip(k + 1))
                {
                    outcome.Released.Add(later.Envelope.EventId.Value);
                }
            }
        }
    }

    private async Task<List<ClaimedMessage>> ClaimAsync(Instant now, CancellationToken cancellationToken)
    {
        var claimed = new List<ClaimedMessage>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(ClaimSql, connection);
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.ToUtcDateTime() });
        command.Parameters.Add(new NpgsqlParameter("lease_until", NpgsqlDbType.TimestampTz) { Value = now.Plus(_options.LeaseDuration).ToUtcDateTime() });
        command.Parameters.Add(new NpgsqlParameter<int>("batch", Math.Max(1, _options.BatchSize)));
        command.Parameters.Add(new NpgsqlParameter<string>("owner", _owner));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            claimed.Add(new ClaimedMessage(reader.GetInt64(0), reader.GetInt32(1), EnvelopeColumnsReader.Read(reader, 2)));
        }

        return claimed;
    }

    private async Task<Dictionary<(string, string), long>> HeadsAsync(List<ClaimedMessage> claimed, CancellationToken cancellationToken)
    {
        var aggregates = claimed.Select(c => (c.Envelope.AggregateType, c.Envelope.AggregateId)).Distinct().ToList();
        var heads = new Dictionary<(string, string), long>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(HeadsSql, connection);
        command.Parameters.Add(new NpgsqlParameter<string[]>("types", [.. aggregates.Select(a => a.AggregateType)]));
        command.Parameters.Add(new NpgsqlParameter<string[]>("ids", [.. aggregates.Select(a => a.AggregateId)]));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            heads[(reader.GetString(0), reader.GetString(1))] = reader.GetInt64(2);
        }

        return heads;
    }

    private async Task FinishAsync(RoundOutcome outcome, List<Guid> released, CancellationToken cancellationToken)
    {
        var now = _clock.Now;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (!outcome.Parked.IsEmpty)
        {
            await ParkAsync(connection, transaction, [.. outcome.Parked], now, cancellationToken).ConfigureAwait(false);
        }

        if (!outcome.Completed.IsEmpty)
        {
            await using var complete = new NpgsqlCommand(CompleteSql, connection, transaction);
            complete.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", [.. outcome.Completed]));
            complete.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.ToUtcDateTime() });
            await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!outcome.Retries.IsEmpty)
        {
            var retries = outcome.Retries.ToArray();
            await using var retry = new NpgsqlCommand(RetrySql, connection, transaction);
            retry.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", [.. retries.Select(r => r.EventId)]));
            retry.Parameters.Add(new NpgsqlParameter("next_at", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
            {
                Value = retries.Select(r => r.NextAttemptAt.ToUtcDateTime()).ToArray(),
            });
            retry.Parameters.Add(new NpgsqlParameter<string[]>("errors", [.. retries.Select(r => r.Error)]));
            retry.Parameters.Add(new NpgsqlParameter<string>("owner", _owner));
            await retry.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (released.Count > 0)
        {
            await using var release = new NpgsqlCommand(ReleaseSql, connection, transaction);
            release.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", [.. released]));
            release.Parameters.Add(new NpgsqlParameter<string>("owner", _owner));
            await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ParkAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ParkedFailure[] parked, Instant now, CancellationToken cancellationToken)
    {
        await using (var park = new NpgsqlCommand(ParkSql, connection, transaction))
        {
            park.Parameters.Add(new NpgsqlParameter<Guid[]>("id", [.. parked.Select(_ => Guid.CreateVersion7())]));
            park.Parameters.Add(new NpgsqlParameter<string[]>("handler", [.. parked.Select(p => p.Handler)]));
            park.Parameters.Add(new NpgsqlParameter<Guid[]>("event_id", [.. parked.Select(p => p.Envelope.EventId.Value)]));
            park.Parameters.Add(new NpgsqlParameter<string[]>("event_type", [.. parked.Select(p => p.Envelope.EventType.Value)]));
            park.Parameters.Add(new NpgsqlParameter<string[]>("aggregate_type", [.. parked.Select(p => p.Envelope.AggregateType)]));
            park.Parameters.Add(new NpgsqlParameter<string[]>("aggregate_id", [.. parked.Select(p => p.Envelope.AggregateId)]));
            park.Parameters.Add(new NpgsqlParameter<string[]>("error_class", [.. parked.Select(p => ErrorText.ClassOf(p.Error))]));
            park.Parameters.Add(new NpgsqlParameter<string[]>("error_message", [.. parked.Select(p => ErrorText.Of(p.Error))]));
            park.Parameters.Add(new NpgsqlParameter<int[]>("attempts", [.. parked.Select(p => p.Attempts)]));
            park.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.ToUtcDateTime() });
            await park.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var notices = parked.Select(p => PlatformEvents.DeadLetterParkedEnvelope(p.Envelope, p.Handler, ErrorText.ClassOf(p.Error), now)).ToList();
        await OutboxWriter.WriteAsync(connection, transaction, notices, now, cancellationToken).ConfigureAwait(false);
        foreach (var p in parked)
        {
            LogParked(_logger, p.Handler, p.Envelope.EventType.Value, p.Envelope.EventId.Value, p.Attempts);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event handler {Handler} failed for {EventType} {EventId}")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string handler, string eventType, Guid eventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Event handler {Handler} failed a micro-batch of {Count}; retrying event by event")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception, string handler, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Event handler {Handler} parked {EventType} {EventId} after {Attempts} attempts")]
    private static partial void LogParked(ILogger logger, string handler, string eventType, Guid eventId, int attempts);

    private sealed record ClaimedMessage(long Position, int Attempts, EventEnvelope Envelope);

    private sealed record ParkedFailure(EventEnvelope Envelope, string Handler, Exception Error, int Attempts);

    private sealed class RoundOutcome
    {
        public ConcurrentBag<Guid> Completed { get; } = [];

        public ConcurrentBag<(Guid EventId, Instant NextAttemptAt, string Error)> Retries { get; } = [];

        public ConcurrentBag<ParkedFailure> Parked { get; } = [];

        public ConcurrentBag<Guid> Released { get; } = [];
    }
}

/// <summary>Invokes one handler for one event in its own scope and transaction, recording <c>plt.processed_event</c>.</summary>
internal sealed class HandlerInvoker(IServiceScopeFactory scopes)
{
    private const string MarkSql = """
        INSERT INTO plt.processed_event (handler, event_id, processed_at, replay_count) VALUES (@handler, @event_id, @now, 0)
        ON CONFLICT (handler, event_id) DO NOTHING
        """;

    private const string MarkReplaySql = """
        INSERT INTO plt.processed_event (handler, event_id, processed_at, replay_count) VALUES (@handler, @event_id, @now, 1)
        ON CONFLICT (handler, event_id) DO UPDATE SET processed_at = EXCLUDED.processed_at, replay_count = processed_event.replay_count + 1
        """;

    private const string MarkBatchSql = """
        INSERT INTO plt.processed_event (handler, event_id, processed_at, replay_count)
        SELECT @handler, id, @now, 0 FROM unnest(@ids) AS id
        ON CONFLICT (handler, event_id) DO NOTHING
        RETURNING event_id
        """;

    /// <summary>
    /// Runs one handler over several events (in the given order) in one scope and one transaction: markers for all of
    /// them, the handler for those not yet processed, one commit. Throws when any invocation fails (nothing commits).
    /// </summary>
    public async Task InvokeBatchAsync(
        EventHandlerRegistration handler, IReadOnlyList<EventEnvelope> envelopes, IClock clock, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var services = scope.ServiceProvider;
            var context = services.GetRequiredService<RequestContext>();
            var session = services.GetRequiredService<DbSession>();
            var transaction = await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var fresh = new HashSet<Guid>();
                await using (var mark = new NpgsqlCommand(MarkBatchSql, transaction.Connection, transaction.Transaction))
                {
                    mark.Parameters.Add(new NpgsqlParameter<string>("handler", handler.HandlerName));
                    mark.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", [.. envelopes.Select(e => e.EventId.Value)]));
                    mark.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = clock.Now.ToUtcDateTime() });
                    await using var reader = await mark.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        fresh.Add(reader.GetGuid(0));
                    }
                }

                foreach (var envelope in envelopes.Where(e => fresh.Contains(e.EventId.Value)))
                {
                    Prepare(context, handler, envelope, replay: false);
                    await handler.Invoke(services, envelope, cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void Prepare(RequestContext context, EventHandlerRegistration handler, EventEnvelope envelope, bool replay)
    {
        context.Actor = ActorRef.Service(handler.HandlerName);
        context.CorrelationId = envelope.CorrelationId;
        context.CausationId = envelope.EventId.Value;
        context.LegalEntity = envelope.LegalEntity;
        context.Jurisdiction = envelope.Jurisdiction;
        context.ConfigurationHash = envelope.ConfigurationHash;
        context.Origin = replay ? EventOrigin.Replay : envelope.Origin;
        context.Channel = null;
    }

    /// <summary>Returns false when the handler had already processed the event (and <paramref name="replay"/> is false).</summary>
    public async Task<bool> InvokeAsync(
        EventHandlerRegistration handler, EventEnvelope envelope, bool replay, IClock clock, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var services = scope.ServiceProvider;
            Prepare(services.GetRequiredService<RequestContext>(), handler, envelope, replay);

            var session = services.GetRequiredService<DbSession>();
            var transaction = await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await using (var mark = new NpgsqlCommand(replay ? MarkReplaySql : MarkSql, transaction.Connection, transaction.Transaction))
                {
                    mark.Parameters.Add(new NpgsqlParameter<string>("handler", handler.HandlerName));
                    mark.Parameters.Add(new NpgsqlParameter<Guid>("event_id", envelope.EventId.Value));
                    mark.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = clock.Now.ToUtcDateTime() });
                    if (await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                    {
                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        return false;
                    }
                }

                var delivered = replay ? envelope with { Origin = EventOrigin.Replay } : envelope;
                await handler.Invoke(services, delivered, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
        }
    }
}

/// <summary>Error text for dead letters and retries: exception type plus a truncated message (no stack traces).</summary>
internal static class ErrorText
{
    public static string ClassOf(Exception error) => error.GetType().FullName ?? error.GetType().Name;

    public static string Of(Exception error) => Truncate($"{error.GetType().Name}: {error.Message}");

    public static string Truncate(string text) => text.Length <= 500 ? text : text[..500];
}

/// <summary>The worker's background loop around <see cref="OutboxProcessor"/>.</summary>
public sealed partial class OutboxDispatcherService(OutboxProcessor processor, IOptions<OutboxOptions> options, ILogger<OutboxDispatcherService> logger)
    : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = options.Value.PollInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            OutboxBatchResult result;
            try
            {
                result = await processor.ProcessBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                LogRoundFailed(logger, ex);
                result = OutboxBatchResult.Empty;
            }

            if (result.Claimed == 0 || result.Claimed == result.Released)
            {
                try
                {
                    await Task.Delay(poll, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox dispatch round failed; retrying after the poll interval")]
    private static partial void LogRoundFailed(ILogger logger, Exception exception);
}
