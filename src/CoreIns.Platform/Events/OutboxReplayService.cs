using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using Npgsql;
using NpgsqlTypes;

namespace CoreIns.Platform.Events;

/// <summary>Which archived events a replay re-delivers (all conditions combine with AND; null = no condition).</summary>
public sealed record ReplayFilter
{
    /// <summary>Recorded at or after.</summary>
    public Instant? RecordedFrom { get; init; }

    /// <summary>Recorded before.</summary>
    public Instant? RecordedTo { get; init; }

    /// <summary>Only this aggregate type.</summary>
    public string? AggregateType { get; init; }

    /// <summary>Only this aggregate (with <see cref="AggregateType"/>).</summary>
    public string? AggregateId { get; init; }

    /// <summary>Only these events.</summary>
    public IReadOnlyCollection<Guid>? EventIds { get; init; }
}

/// <summary>
/// Operations on the event log (<c>plt.Consumer.replay</c>, <c>plt.DeadLetter.replay</c>, D-ARC-02): re-delivers
/// archived events to one handler in archive order with <c>origin = REPLAY</c> (the handler's processed markers are
/// overridden, so it must be idempotent), re-runs a parked dead letter, and purges dispatched outbox rows and the
/// archive by age. The archive's replay window is independent of the outbox purge. Retention durations are
/// configuration (D-REG-07: none is invented here).
/// </summary>
public sealed class OutboxReplayService(NpgsqlDataSource dataSource, EventHandlerRegistry registry, Microsoft.Extensions.DependencyInjection.IServiceScopeFactory scopes, IClock clock)
{
    private readonly HandlerInvoker _invoker = new(scopes);

    /// <summary>Re-delivers the matching archived events to <paramref name="handlerName"/>; returns how many were delivered.</summary>
    public async Task<int> ReplayAsync(string handlerName, ReplayFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var handler = registry.Find(handlerName) ?? throw new InvalidOperationException($"No event handler named '{handlerName}'.");

        var sql = $"""
            SELECT {EnvelopeColumnsReader.Columns} FROM plt.event_archive
            WHERE (@from::timestamptz IS NULL OR recorded_at >= @from)
              AND (@to::timestamptz IS NULL OR recorded_at < @to)
              AND (@aggregate_type::text IS NULL OR aggregate_type = @aggregate_type)
              AND (@aggregate_id::text IS NULL OR aggregate_id = @aggregate_id)
              AND (@ids::uuid[] IS NULL OR event_id = ANY(@ids))
            ORDER BY position
            """;

        var envelopes = new List<EventEnvelope>();
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = (object?)filter.RecordedFrom?.ToUtcDateTime() ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = (object?)filter.RecordedTo?.ToUtcDateTime() ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("aggregate_type", NpgsqlDbType.Text) { Value = (object?)filter.AggregateType ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("aggregate_id", NpgsqlDbType.Text) { Value = (object?)filter.AggregateId ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = (object?)filter.EventIds?.ToArray() ?? DBNull.Value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                envelopes.Add(EnvelopeColumnsReader.Read(reader));
            }
        }

        var delivered = 0;
        foreach (var envelope in envelopes.Where(e => string.Equals(e.RoutingKey, handler.RoutingKey, StringComparison.Ordinal)))
        {
            await _invoker.InvokeAsync(handler, envelope, replay: true, clock, cancellationToken).ConfigureAwait(false);
            delivered++;
        }

        return delivered;
    }

    /// <summary>Re-runs a parked handler for its event. Returns true when it succeeded (the dead letter becomes Replayed).</summary>
    public async Task<bool> ReplayDeadLetterAsync(Guid deadLetterId, CancellationToken cancellationToken)
    {
        string handlerName;
        EventEnvelope envelope;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = new NpgsqlCommand(
            $"""
            SELECT d.handler, {string.Join(", ", EnvelopeColumnsReader.Columns.Split(',').Select(c => "a." + c.Trim()))}
            FROM plt.outbox_dead_letter d JOIN plt.event_archive a ON a.event_id = d.event_id
            WHERE d.dead_letter_id = @id AND d.status = 'Parked'
            """,
            connection))
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("id", deadLetterId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"No parked dead letter {deadLetterId}.");
            }

            handlerName = reader.GetString(0);
            envelope = EnvelopeColumnsReader.Read(reader, 1);
        }

        var handler = registry.Find(handlerName) ?? throw new InvalidOperationException($"No event handler named '{handlerName}'.");
        string status;
        string? error = null;
        try
        {
            await _invoker.InvokeAsync(handler, envelope, replay: false, clock, cancellationToken).ConfigureAwait(false);
            status = "Replayed";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status = "Parked";
            error = ErrorText.Of(ex);
        }

        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var update = new NpgsqlCommand(
            """
            UPDATE plt.outbox_dead_letter
            SET status = @status,
                attempts = attempts + 1,
                resolved_at = CASE WHEN @status = 'Replayed' THEN @now ELSE resolved_at END,
                resolution_reason = CASE WHEN @status = 'Replayed' THEN 'replayed' ELSE resolution_reason END,
                error_message = coalesce(@error, error_message)
            WHERE dead_letter_id = @id
            """,
            connection))
        {
            update.Parameters.Add(new NpgsqlParameter<string>("status", status));
            update.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = clock.Now.ToUtcDateTime() });
            update.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
            update.Parameters.Add(new NpgsqlParameter<Guid>("id", deadLetterId));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return status == "Replayed";
    }

    /// <summary>Deletes dispatched outbox rows dispatched before <paramref name="dispatchedBefore"/> (they stay in the archive).</summary>
    public async Task<int> PurgeDispatchedAsync(Instant dispatchedBefore, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            DELETE FROM plt.outbox_message m
            WHERE m.status = 'Dispatched' AND m.dispatched_at < @before
              AND EXISTS (SELECT 1 FROM plt.event_archive a WHERE a.event_id = m.event_id)
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.TimestampTz) { Value = dispatchedBefore.ToUtcDateTime() });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes archived events recorded before <paramref name="recordedBefore"/> (end of the replay window).</summary>
    public async Task<int> PurgeArchiveAsync(Instant recordedBefore, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("DELETE FROM plt.event_archive WHERE recorded_at < @before", connection);
        command.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.TimestampTz) { Value = recordedBefore.ToUtcDateTime() });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
