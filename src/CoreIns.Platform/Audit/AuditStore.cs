using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using CoreIns.Platform.Context;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using Npgsql;
using NpgsqlTypes;

namespace CoreIns.Platform.Audit;

/// <summary>
/// Writes audit records (<c>plt.Audit.append</c>, in-process). Records appended with <see cref="Append"/> commit or
/// roll back with the unit of work; records appended with <see cref="AppendRegardlessOfOutcome"/> are written even when
/// the unit of work rolls back (a rejected command is still audited).
/// </summary>
public interface IAuditWriter
{
    /// <summary>Stages a record in the current transaction.</summary>
    void Append(AuditRecord record);

    /// <summary>Stages a record that is written whether the transaction commits or rolls back.</summary>
    void AppendRegardlessOfOutcome(AuditRecord record);
}

/// <summary>The scoped audit stage; flushes last before commit to hold the day's chain lock as briefly as possible.</summary>
internal sealed class AuditStaging(IClock clock) : ITransactionParticipant, IAuditWriter
{
    private readonly List<AuditRecord> _transactional = [];
    private readonly List<AuditRecord> _independent = [];

    public int Order => 1000;

    public void Append(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _transactional.Add(record);
    }

    public void AppendRegardlessOfOutcome(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _independent.Add(record);
    }

    public async Task BeforeCommitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        if (_transactional.Count == 0 && _independent.Count == 0)
        {
            return;
        }

        var records = _transactional.Concat(_independent).ToArray();
        _transactional.Clear();
        _independent.Clear();
        await AuditStore.AppendAsync(connection, transaction, records, clock.Now, cancellationToken).ConfigureAwait(false);
    }

    public async Task AfterRollbackAsync(DbSession session, CancellationToken cancellationToken)
    {
        _transactional.Clear();
        if (_independent.Count == 0)
        {
            return;
        }

        var transaction = await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The insert-only, hash-chained audit store (D-ARC-15). Each UTC day is one chain: record n stores
/// <c>prev_hash</c> = hash of record n−1 (a day-specific genesis value for n = 1) and <c>hash</c> = SHA-256 over the
/// RFC 8785 canonical JSON of all its stored fields including <c>prev_hash</c>. Appends serialise on the day's
/// <c>plt.audit_chain_head</c> row, taken at the end of the transaction. Any change, deletion or reordering of a
/// stored record breaks verification.
/// </summary>
internal static class AuditStore
{
    private const string LockHeadSql = """
        INSERT INTO plt.audit_chain_head (chain_date, last_sequence, last_hash) VALUES (@d, 0, @genesis) ON CONFLICT (chain_date) DO NOTHING;
        SELECT last_sequence, last_hash FROM plt.audit_chain_head WHERE chain_date = @d FOR UPDATE;
        """;

    private const string InsertSql = """
        INSERT INTO plt.audit_event (
            audit_id, chain_date, sequence, prev_hash, hash, actor_kind, actor_id, on_behalf_of, role_codes,
            authority_check_id, authority_used, operation, outcome, error_code, object_module, object_type, object_id,
            object_number, changes, reason, channel, correlation_id, causation_id, ai_interaction_id, business_keys,
            origin, legal_entity, jurisdiction, occurred_at, recorded_at)
        SELECT u.audit_id, @d, u.sequence, u.prev_hash, u.hash, u.actor_kind, u.actor_id, u.on_behalf_of,
            string_to_array(u.role_codes, chr(31)), u.authority_check_id, u.authority_used, u.operation, u.outcome, u.error_code,
            u.object_module, u.object_type, u.object_id, u.object_number, u.changes::jsonb, u.reason, u.channel,
            u.correlation_id, u.causation_id, u.ai_interaction_id, u.business_keys::jsonb, u.origin, u.legal_entity,
            u.jurisdiction, u.occurred_at, u.recorded_at
        FROM unnest(@audit_id, @sequence, @prev_hash, @hash, @actor_kind, @actor_id, @on_behalf_of, @role_codes,
            @authority_check_id, @authority_used, @operation, @outcome, @error_code, @object_module, @object_type,
            @object_id, @object_number, @changes, @reason, @channel, @correlation_id, @causation_id, @ai_interaction_id,
            @business_keys, @origin, @legal_entity, @jurisdiction, @occurred_at, @recorded_at)
            AS u(audit_id, sequence, prev_hash, hash, actor_kind, actor_id, on_behalf_of, role_codes,
            authority_check_id, authority_used, operation, outcome, error_code, object_module, object_type,
            object_id, object_number, changes, reason, channel, correlation_id, causation_id, ai_interaction_id,
            business_keys, origin, legal_entity, jurisdiction, occurred_at, recorded_at)
        """;

    /// <summary>The first <c>prev_hash</c> of a day's chain.</summary>
    public static string Genesis(DateOnly chainDate) =>
        Sha256Hash.ComputeUtf8("coreins:plt.audit_event:genesis:" + chainDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Value;

    public static async Task AppendAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<AuditRecord> records, Instant now, CancellationToken cancellationToken)
    {
        var chainDate = now.UtcDate.Value;
        long sequence;
        string previous;
        await using (var batch = new NpgsqlCommand(LockHeadSql, connection, transaction))
        {
            batch.Parameters.Add(new NpgsqlParameter<DateOnly>("d", chainDate));
            batch.Parameters.Add(new NpgsqlParameter<string>("genesis", Genesis(chainDate)));
            await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (reader.FieldCount == 0 && await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            {
                // Skip the INSERT's empty result; the SELECT ... FOR UPDATE row follows.
            }

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The audit chain head could not be locked.");
            }
            sequence = reader.GetInt64(0);
            previous = reader.GetString(1).Trim();
        }

        var rows = new List<StoredAudit>(records.Count);
        foreach (var record in records)
        {
            sequence++;
            var row = StoredAudit.From(record with { RecordedAt = now }, chainDate, sequence, previous);
            rows.Add(row);
            previous = row.Hash;
        }

        await using (var insert = new NpgsqlCommand(InsertSql, connection, transaction))
        {
            var p = insert.Parameters;
            p.Add(new NpgsqlParameter<DateOnly>("d", chainDate));
            p.Add(new NpgsqlParameter<Guid[]>("audit_id", [.. rows.Select(r => r.AuditId)]));
            p.Add(new NpgsqlParameter<long[]>("sequence", [.. rows.Select(r => r.Sequence)]));
            p.Add(Text("prev_hash", rows.Select(r => r.PrevHash)));
            p.Add(Text("hash", rows.Select(r => r.Hash)));
            p.Add(Text("actor_kind", rows.Select(r => r.ActorKind)));
            p.Add(Text("actor_id", rows.Select(r => r.ActorId)));
            p.Add(Text("on_behalf_of", rows.Select(r => r.OnBehalfOf)));
            p.Add(Text("role_codes", rows.Select(r => string.Join('\u001f', r.RoleCodes))));
            p.Add(Uuids("authority_check_id", rows.Select(r => r.AuthorityCheckId)));
            p.Add(Text("authority_used", rows.Select(r => r.AuthorityUsed)));
            p.Add(Text("operation", rows.Select(r => r.Operation)));
            p.Add(Text("outcome", rows.Select(r => r.Outcome)));
            p.Add(Text("error_code", rows.Select(r => r.ErrorCode)));
            p.Add(Text("object_module", rows.Select(r => r.ObjectModule)));
            p.Add(Text("object_type", rows.Select(r => r.ObjectType)));
            p.Add(Text("object_id", rows.Select(r => r.ObjectId)));
            p.Add(Text("object_number", rows.Select(r => r.ObjectNumber)));
            p.Add(Text("changes", rows.Select(r => r.Changes)));
            p.Add(Text("reason", rows.Select(r => r.Reason)));
            p.Add(Text("channel", rows.Select(r => r.Channel)));
            p.Add(Text("correlation_id", rows.Select(r => r.CorrelationId)));
            p.Add(Uuids("causation_id", rows.Select(r => r.CausationId)));
            p.Add(Uuids("ai_interaction_id", rows.Select(r => r.AiInteractionId)));
            p.Add(Text("business_keys", rows.Select(r => r.BusinessKeys)));
            p.Add(Text("origin", rows.Select(r => r.Origin)));
            p.Add(Text("legal_entity", rows.Select(r => r.LegalEntity)));
            p.Add(Text("jurisdiction", rows.Select(r => r.Jurisdiction)));
            p.Add(new NpgsqlParameter("occurred_at", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = rows.Select(r => r.OccurredAt.ToUtcDateTime()).ToArray() });
            p.Add(new NpgsqlParameter("recorded_at", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = rows.Select(r => r.RecordedAt.ToUtcDateTime()).ToArray() });
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var head = new NpgsqlCommand(
            "UPDATE plt.audit_chain_head SET last_sequence = @s, last_hash = @h WHERE chain_date = @d", connection, transaction);
        head.Parameters.Add(new NpgsqlParameter<long>("s", sequence));
        head.Parameters.Add(new NpgsqlParameter<string>("h", previous));
        head.Parameters.Add(new NpgsqlParameter<DateOnly>("d", chainDate));
        await head.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static NpgsqlParameter Text(string name, IEnumerable<string?> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() };

    private static NpgsqlParameter Uuids(string name, IEnumerable<Guid?> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = values.ToArray() };
}

/// <summary>An audit row exactly as stored, with the canonical hash input.</summary>
internal sealed record StoredAudit
{
    public required Guid AuditId { get; init; }

    public required DateOnly ChainDate { get; init; }

    public required long Sequence { get; init; }

    public required string PrevHash { get; init; }

    public string Hash { get; init; } = string.Empty;

    public required string ActorKind { get; init; }

    public required string ActorId { get; init; }

    public string? OnBehalfOf { get; init; }

    public required IReadOnlyList<string> RoleCodes { get; init; }

    public Guid? AuthorityCheckId { get; init; }

    public string? AuthorityUsed { get; init; }

    public required string Operation { get; init; }

    public required string Outcome { get; init; }

    public string? ErrorCode { get; init; }

    public string? ObjectModule { get; init; }

    public string? ObjectType { get; init; }

    public string? ObjectId { get; init; }

    public string? ObjectNumber { get; init; }

    public required string Changes { get; init; }

    public string? Reason { get; init; }

    public string? Channel { get; init; }

    public required string CorrelationId { get; init; }

    public Guid? CausationId { get; init; }

    public Guid? AiInteractionId { get; init; }

    public required string BusinessKeys { get; init; }

    public required string Origin { get; init; }

    public required string LegalEntity { get; init; }

    public required string Jurisdiction { get; init; }

    public required Instant OccurredAt { get; init; }

    public required Instant RecordedAt { get; init; }

    public static StoredAudit From(AuditRecord record, DateOnly chainDate, long sequence, string prevHash)
    {
        var row = new StoredAudit
        {
            AuditId = record.Id.Value,
            ChainDate = chainDate,
            Sequence = sequence,
            PrevHash = prevHash,
            ActorKind = record.Actor.KindCode,
            ActorId = record.Actor.Id,
            OnBehalfOf = record.OnBehalfOf?.ToString(),
            RoleCodes = record.Roles,
            AuthorityCheckId = record.AuthorityCheckId?.Value,
            AuthorityUsed = record.AuthorityUsed,
            Operation = record.Operation.Value,
            Outcome = record.Outcome.ToString(),
            ErrorCode = record.ErrorCode,
            ObjectModule = record.ObjectRef?.Module.ToString(),
            ObjectType = record.ObjectRef?.Type,
            ObjectId = record.ObjectRef?.Id,
            ObjectNumber = record.ObjectNumber,
            Changes = record.ChangesJson(),
            Reason = record.Reason,
            Channel = record.Channel,
            CorrelationId = record.CorrelationId.Value,
            CausationId = record.CausationId,
            AiInteractionId = record.AiInteractionId?.Value,
            BusinessKeys = record.BusinessKeysJson(),
            Origin = record.Origin.ToCode(),
            LegalEntity = record.LegalEntity.Value,
            Jurisdiction = record.Jurisdiction.Value,
            OccurredAt = record.OccurredAt,
            RecordedAt = record.RecordedAt,
        };
        return row with { Hash = row.ComputeHash() };
    }

    /// <summary>SHA-256 over the RFC 8785 canonical JSON of every stored field except <c>hash</c>.</summary>
    public string ComputeHash()
    {
        var document = new JsonObject
        {
            ["auditId"] = AuditId.ToString("D"),
            ["chainDate"] = ChainDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["sequence"] = Sequence.ToString(CultureInfo.InvariantCulture),
            ["prevHash"] = PrevHash,
            ["actorKind"] = ActorKind,
            ["actorId"] = ActorId,
            ["onBehalfOf"] = OnBehalfOf,
            ["roleCodes"] = new JsonArray([.. RoleCodes.Select(r => (JsonNode?)JsonValue.Create(r))]),
            ["authorityCheckId"] = AuthorityCheckId?.ToString("D"),
            ["authorityUsed"] = AuthorityUsed,
            ["operation"] = Operation,
            ["outcome"] = Outcome,
            ["errorCode"] = ErrorCode,
            ["objectModule"] = ObjectModule,
            ["objectType"] = ObjectType,
            ["objectId"] = ObjectId,
            ["objectNumber"] = ObjectNumber,
            ["changes"] = JsonNode.Parse(Changes),
            ["reason"] = Reason,
            ["channel"] = Channel,
            ["correlationId"] = CorrelationId,
            ["causationId"] = CausationId?.ToString("D"),
            ["aiInteractionId"] = AiInteractionId?.ToString("D"),
            ["businessKeys"] = JsonNode.Parse(BusinessKeys),
            ["origin"] = Origin,
            ["legalEntity"] = LegalEntity,
            ["jurisdiction"] = Jurisdiction,
            ["occurredAt"] = OccurredAt.ToString(),
            ["recordedAt"] = RecordedAt.ToString(),
        };
        return CanonicalJson.Hash(document).Value;
    }

    /// <summary>Reads a stored row (column order of <see cref="AuditChainVerifier"/>'s query).</summary>
    public static StoredAudit Read(DbDataReader reader)
    {
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        Guid? Uuid(int i) => reader.IsDBNull(i) ? null : reader.GetGuid(i);
        Instant Time(int i) => Instant.FromUtcDateTime(DateTime.SpecifyKind(reader.GetDateTime(i), DateTimeKind.Utc));
        return new StoredAudit
        {
            AuditId = reader.GetGuid(0),
            ChainDate = reader.GetFieldValue<DateOnly>(1),
            Sequence = reader.GetInt64(2),
            PrevHash = reader.GetString(3).Trim(),
            Hash = reader.GetString(4).Trim(),
            ActorKind = reader.GetString(5),
            ActorId = reader.GetString(6),
            OnBehalfOf = Text(7),
            RoleCodes = reader.GetFieldValue<string[]>(8),
            AuthorityCheckId = Uuid(9),
            AuthorityUsed = Text(10),
            Operation = reader.GetString(11),
            Outcome = reader.GetString(12),
            ErrorCode = Text(13),
            ObjectModule = Text(14),
            ObjectType = Text(15),
            ObjectId = Text(16),
            ObjectNumber = Text(17),
            Changes = reader.GetString(18),
            Reason = Text(19),
            Channel = Text(20),
            CorrelationId = reader.GetString(21).Trim(),
            CausationId = Uuid(22),
            AiInteractionId = Uuid(23),
            BusinessKeys = reader.GetString(24),
            Origin = reader.GetString(25),
            LegalEntity = reader.GetString(26),
            Jurisdiction = reader.GetString(27).Trim(),
            OccurredAt = Time(28),
            RecordedAt = Time(29),
        };
    }
}

/// <summary>Result of verifying one day's audit chain.</summary>
/// <param name="ChainDate">The day.</param>
/// <param name="Records">Records checked.</param>
/// <param name="IsValid">True when every link and hash verifies.</param>
/// <param name="BrokenAtSequence">First sequence that fails, if any.</param>
/// <param name="Problem">What failed.</param>
public sealed record AuditChainVerification(BusinessDate ChainDate, long Records, bool IsValid, long? BrokenAtSequence, string? Problem);

/// <summary>Verifies a day's audit chain (<c>plt.Audit.verify</c>): contiguous sequence, links and hashes, and the head.</summary>
public sealed class AuditChainVerifier(NpgsqlDataSource dataSource)
{
    private const string Sql = """
        SELECT audit_id, chain_date, sequence, prev_hash, hash, actor_kind, actor_id, on_behalf_of, role_codes,
            authority_check_id, authority_used, operation, outcome, error_code, object_module, object_type, object_id,
            object_number, changes::text, reason, channel, correlation_id, causation_id, ai_interaction_id,
            business_keys::text, origin, legal_entity, jurisdiction, occurred_at, recorded_at
        FROM plt.audit_event WHERE chain_date = @d ORDER BY sequence
        """;

    /// <summary>Verifies the chain of <paramref name="chainDate"/>.</summary>
    public async Task<AuditChainVerification> VerifyAsync(BusinessDate chainDate, CancellationToken cancellationToken)
    {
        var day = chainDate.Value;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        long expected = 1;
        var previous = AuditStore.Genesis(day);
        await using (var command = new NpgsqlCommand(Sql, connection))
        {
            command.Parameters.Add(new NpgsqlParameter<DateOnly>("d", day));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = StoredAudit.Read(reader);
                if (row.Sequence != expected)
                {
                    return new AuditChainVerification(chainDate, expected - 1, false, expected, $"sequence {expected} is missing (found {row.Sequence})");
                }

                if (!string.Equals(row.PrevHash, previous, StringComparison.Ordinal))
                {
                    return new AuditChainVerification(chainDate, expected - 1, false, row.Sequence, "prev_hash does not link to the previous record");
                }

                if (!string.Equals(row.ComputeHash(), row.Hash, StringComparison.Ordinal))
                {
                    return new AuditChainVerification(chainDate, expected - 1, false, row.Sequence, "the record's content does not match its hash");
                }

                previous = row.Hash;
                expected++;
            }
        }

        await using var head = new NpgsqlCommand("SELECT last_sequence, last_hash FROM plt.audit_chain_head WHERE chain_date = @d", connection);
        head.Parameters.Add(new NpgsqlParameter<DateOnly>("d", day));
        await using var headReader = await head.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await headReader.ReadAsync(cancellationToken).ConfigureAwait(false)
            && (headReader.GetInt64(0) != expected - 1 || !string.Equals(headReader.GetString(1).Trim(), previous, StringComparison.Ordinal)))
        {
            return new AuditChainVerification(chainDate, expected - 1, false, expected, "the chain head does not match the last record (records removed at the end?)");
        }

        return new AuditChainVerification(chainDate, expected - 1, true, null, null);
    }
}
