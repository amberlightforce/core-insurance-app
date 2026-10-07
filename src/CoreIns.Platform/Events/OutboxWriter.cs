using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using Npgsql;
using NpgsqlTypes;

namespace CoreIns.Platform.Events;

/// <summary>
/// Writes envelopes to <c>plt.outbox_message</c> on the caller's connection and transaction. The per-aggregate
/// sequence comes from <c>plt.aggregate_sequence</c>: an upsert that increments the aggregate's row holds that row's
/// lock until commit, so concurrent writers of one aggregate serialise and the sequence stays gap-free (a rolled-back
/// writer's increment rolls back too). Rows are locked in (type, id) order so writers of several aggregates cannot deadlock.
/// </summary>
internal static class OutboxWriter
{
    private const string ReserveSql = """
        INSERT INTO plt.aggregate_sequence AS s (aggregate_type, aggregate_id, last_sequence)
        SELECT u.t, u.i, u.c FROM unnest(@types, @ids, @counts) AS u(t, i, c) ORDER BY u.t, u.i
        ON CONFLICT (aggregate_type, aggregate_id) DO UPDATE SET last_sequence = s.last_sequence + EXCLUDED.last_sequence
        RETURNING aggregate_type, aggregate_id, last_sequence
        """;

    private const string InsertSql = """
        INSERT INTO plt.outbox_message (
            event_id, event_type, schema_version, producer, aggregate_type, aggregate_id, aggregate_sequence,
            occurred_at, recorded_at, legal_entity, jurisdiction, configuration_hash, business_keys, correlation_id,
            causation_id, actor_kind, actor_id, ai_interaction_id, origin, data_classification, set_id, set_size, set_index,
            payload, status, attempts, next_attempt_at)
        SELECT u.event_id, u.event_type, u.schema_version, u.producer, u.aggregate_type, u.aggregate_id, u.aggregate_sequence,
            u.occurred_at, u.recorded_at, u.legal_entity, u.jurisdiction, u.configuration_hash, u.business_keys::jsonb, u.correlation_id,
            u.causation_id, u.actor_kind, u.actor_id, u.ai_interaction_id, u.origin, u.data_classification, u.set_id, u.set_size, u.set_index,
            u.payload::jsonb, 'Pending', 0, u.recorded_at
        FROM unnest(@event_id, @event_type, @schema_version, @producer, @aggregate_type, @aggregate_id, @aggregate_sequence,
            @occurred_at, @recorded_at, @legal_entity, @jurisdiction, @configuration_hash, @business_keys, @correlation_id,
            @causation_id, @actor_kind, @actor_id, @ai_interaction_id, @origin, @data_classification, @set_id, @set_size, @set_index,
            @payload) WITH ORDINALITY AS u(event_id, event_type, schema_version, producer, aggregate_type, aggregate_id, aggregate_sequence,
            occurred_at, recorded_at, legal_entity, jurisdiction, configuration_hash, business_keys, correlation_id,
            causation_id, actor_kind, actor_id, ai_interaction_id, origin, data_classification, set_id, set_size, set_index,
            payload, ord)
        ORDER BY u.ord
        """;

    /// <summary>Assigns sequences and inserts the envelopes; returns them as written.</summary>
    public static async Task<IReadOnlyList<EventEnvelope>> WriteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<EventEnvelope> envelopes, Instant recordedAt,
        CancellationToken cancellationToken)
    {
        if (envelopes.Count == 0)
        {
            return [];
        }

        var groups = envelopes
            .GroupBy(e => (e.AggregateType, e.AggregateId))
            .Select(g => (g.Key.AggregateType, g.Key.AggregateId, Count: (long)g.Count()))
            .ToList();

        var last = new Dictionary<(string, string), long>();
        await using (var reserve = new NpgsqlCommand(ReserveSql, connection, transaction))
        {
            reserve.Parameters.Add(new NpgsqlParameter<string[]>("types", [.. groups.Select(g => g.AggregateType)]));
            reserve.Parameters.Add(new NpgsqlParameter<string[]>("ids", [.. groups.Select(g => g.AggregateId)]));
            reserve.Parameters.Add(new NpgsqlParameter<long[]>("counts", [.. groups.Select(g => g.Count)]));
            await using var reader = await reserve.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                last[(reader.GetString(0), reader.GetString(1))] = reader.GetInt64(2);
            }
        }

        var next = groups.ToDictionary(
            g => (g.AggregateType, g.AggregateId),
            g => last[(g.AggregateType, g.AggregateId)] - g.Count + 1);

        var written = new EventEnvelope[envelopes.Count];
        for (var i = 0; i < envelopes.Count; i++)
        {
            var envelope = envelopes[i];
            var key = (envelope.AggregateType, envelope.AggregateId);
            written[i] = envelope with { AggregateSequence = next[key]++, RecordedAt = recordedAt };
            written[i].EnsureValid();
        }

        await using var insert = new NpgsqlCommand(InsertSql, connection, transaction);
        AddArrays(insert.Parameters, written);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }

    private static void AddArrays(NpgsqlParameterCollection parameters, EventEnvelope[] rows)
    {
        parameters.Add(new NpgsqlParameter<Guid[]>("event_id", [.. rows.Select(r => r.EventId.Value)]));
        parameters.Add(Text("event_type", rows.Select(r => r.EventType.Value)));
        parameters.Add(Text("schema_version", rows.Select(r => r.SchemaVersion)));
        parameters.Add(Text("producer", rows.Select(r => r.Producer.ToString())));
        parameters.Add(Text("aggregate_type", rows.Select(r => r.AggregateType)));
        parameters.Add(Text("aggregate_id", rows.Select(r => r.AggregateId)));
        parameters.Add(new NpgsqlParameter<long[]>("aggregate_sequence", [.. rows.Select(r => r.AggregateSequence)]));
        parameters.Add(Timestamps("occurred_at", rows.Select(r => r.OccurredAt)));
        parameters.Add(Timestamps("recorded_at", rows.Select(r => r.RecordedAt)));
        parameters.Add(Text("legal_entity", rows.Select(r => r.LegalEntity.Value)));
        parameters.Add(Text("jurisdiction", rows.Select(r => r.Jurisdiction.Value)));
        parameters.Add(Text("configuration_hash", rows.Select(r => r.ConfigurationHash.ToString())));
        parameters.Add(Text("business_keys", rows.Select(r => JsonSerializer.Serialize(r.BusinessKeys, SharedKernelJson.Options))));
        parameters.Add(Text("correlation_id", rows.Select(r => r.CorrelationId.Value)));
        parameters.Add(new NpgsqlParameter("causation_id", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = rows.Select(r => r.CausationId).ToArray() });
        parameters.Add(Text("actor_kind", rows.Select(r => r.Actor.KindCode)));
        parameters.Add(Text("actor_id", rows.Select(r => r.Actor.Id)));
        parameters.Add(new NpgsqlParameter("ai_interaction_id", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            Value = rows.Select(r => r.AiInteractionId?.Value).ToArray(),
        });
        parameters.Add(Text("origin", rows.Select(r => r.Origin.ToCode())));
        parameters.Add(Text("data_classification", rows.Select(r => r.DataClassification.ToString())));
        parameters.Add(new NpgsqlParameter("set_id", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = rows.Select(r => r.Set?.SetId).ToArray() });
        parameters.Add(new NpgsqlParameter("set_size", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = rows.Select(r => r.Set?.Size).ToArray() });
        parameters.Add(new NpgsqlParameter("set_index", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = rows.Select(r => r.Set?.Index).ToArray() });
        parameters.Add(Text("payload", rows.Select(r => r.Payload.ToJsonString())));
    }

    private static NpgsqlParameter<string[]> Text(string name, IEnumerable<string> values) => new(name, [.. values]);

    private static NpgsqlParameter Timestamps(string name, IEnumerable<Instant> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = values.Select(v => v.ToUtcDateTime()).ToArray() };
}

/// <summary>Reads envelope columns back into an <see cref="EventEnvelope"/> (outbox and archive share the column names).</summary>
internal static class EnvelopeColumnsReader
{
    /// <summary>The envelope column list, in <see cref="Read"/> order.</summary>
    public const string Columns = """
        event_id, event_type, schema_version, producer, aggregate_type, aggregate_id, aggregate_sequence, occurred_at,
        recorded_at, legal_entity, jurisdiction, configuration_hash, business_keys::text, correlation_id, causation_id,
        actor_kind, actor_id, ai_interaction_id, origin, data_classification, set_id, set_size, set_index, payload::text
        """;

    /// <summary>Number of columns in <see cref="Columns"/>.</summary>
    public const int Count = 24;

    /// <summary>Reads the envelope starting at <paramref name="offset"/>.</summary>
    public static EventEnvelope Read(DbDataReader reader, int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Guid? NullableGuid(int i) => reader.IsDBNull(offset + i) ? null : reader.GetGuid(offset + i);
        int? NullableInt(int i) => reader.IsDBNull(offset + i) ? null : reader.GetInt32(offset + i);
        string Text(int i) => reader.GetString(offset + i);
        Instant Time(int i) => Instant.FromUtcDateTime(DateTime.SpecifyKind(reader.GetDateTime(offset + i), DateTimeKind.Utc));

        var setId = NullableGuid(20);
        var aiInteraction = NullableGuid(17);
        return new EventEnvelope
        {
            EventId = EventId.From(reader.GetGuid(offset)),
            EventType = EventTypeName.Parse(Text(1)),
            SchemaVersion = Text(2),
            Producer = Enum.Parse<ModuleCode>(Text(3)),
            AggregateType = Text(4),
            AggregateId = Text(5),
            AggregateSequence = reader.GetInt64(offset + 6),
            OccurredAt = Time(7),
            RecordedAt = Time(8),
            LegalEntity = LegalEntityId.Parse(Text(9)),
            Jurisdiction = Jurisdiction.Parse(Text(10).Trim()),
            ConfigurationHash = ConfigurationHash.Parse(Text(11).Trim()),
            BusinessKeys = JsonSerializer.Deserialize<BusinessKeys>(Text(12), SharedKernelJson.Options) ?? BusinessKeys.Empty,
            CorrelationId = CorrelationId.Parse(Text(13).Trim()),
            CausationId = NullableGuid(14),
            Actor = new ActorRef(ActorRef.ParseKind(Text(15)), Text(16)),
            AiInteractionId = aiInteraction is { } ai ? AiInteractionId.From(ai) : null,
            Origin = EventOrigins.Parse(Text(18)),
            DataClassification = Enum.Parse<DataClassification>(Text(19)),
            Set = setId is { } id ? new EventSet(id, NullableInt(21)!.Value, NullableInt(22)!.Value) : null,
            Payload = JsonNode.Parse(Text(23))?.AsObject() ?? [],
        };
    }
}
