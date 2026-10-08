using System.Text.Json;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Contracts.Common;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using Npgsql;
using NpgsqlTypes;

namespace CoreIns.Platform.Approvals;

/// <summary>One row of <c>plt.approval_request</c> (PRD-14 §3.3 ApprovalRequest, SL2-PLT subset).</summary>
internal sealed record ApprovalRow
{
    public required Guid RequestId { get; init; }

    public required string LegalEntity { get; init; }

    public required string ApprovalType { get; init; }

    public required ObjectRef ObjectRef { get; init; }

    public required Sha256Hash PayloadHash { get; init; }

    public required ApprovalStatus Status { get; init; }

    public required ActorRef Maker { get; init; }

    /// <summary>Makers of earlier content versions of the same subject (REQ-PLT-115 editors), as <c>KIND:id</c>.</summary>
    public required IReadOnlyList<string> Editors { get; init; }

    public required string AuthorityType { get; init; }

    public Money? AuthorityAmount { get; init; }

    public IReadOnlyDictionary<string, string> AuthorityCodes { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public required string ReferralRole { get; init; }

    public string? Reason { get; init; }

    public string? DiffJson { get; init; }

    public Guid? Supersedes { get; init; }

    public required Instant RequestedAt { get; init; }

    public string? Decision { get; init; }

    public ActorRef? Checker { get; init; }

    public string? Comment { get; init; }

    public Guid? AuthorityCheckId { get; init; }

    public Instant? DecidedAt { get; init; }

    public required int Version { get; init; }

    /// <summary>The contract view of the request.</summary>
    public ApprovalView ToView() => new()
    {
        RequestId = RequestId,
        Type = ApprovalType,
        ObjectRef = ObjectRef,
        PayloadHash = PayloadHash,
        Status = Status,
        Maker = ApprovalStore.ToActor(Maker),
        Authority = new ApprovalAuthority
        {
            Type = AuthorityType,
            Amount = AuthorityAmount,
            Codes = AuthorityCodes.Count == 0 ? null : AuthorityCodes,
        },
        ReferralRole = ReferralRole,
        Reason = Reason,
        Diff = DiffJson is null ? null : JsonDocument.Parse(DiffJson).RootElement.Clone(),
        Supersedes = Supersedes,
        RequestedAt = RequestedAt,
        Version = Version,
    };

    /// <summary>The decision, if any.</summary>
    public ApprovalDecisionView? ToDecision() => Decision is null || Checker is null || DecidedAt is null || AuthorityCheckId is null
        ? null
        : new ApprovalDecisionView
        {
            Decision = Decision == ApprovalStore.Approved ? ApprovalDecisionView.DecisionValue.Approved : ApprovalDecisionView.DecisionValue.Rejected,
            Checker = ApprovalStore.ToActor(Checker),
            Comment = Comment,
            AuthorityCheckId = new AuthorityCheckId(AuthorityCheckId.Value),
            DecidedAt = DecidedAt.Value,
        };
}

/// <summary>
/// Set-based SQL on <c>plt.approval_request</c> over the scope's connection and transaction (the platform's runtime
/// access pattern; <see cref="Persistence.PlatformDbContext"/> only versions the table). Every read is filtered by the
/// caller's legal entity: another entity's request is "not found".
/// </summary>
internal static class ApprovalStore
{
    public const string Pending = "PendingApproval";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Withdrawn = "Withdrawn";

    private const string Columns =
        "request_id, legal_entity, approval_type, object_module, object_type, object_id, payload_hash, status, maker_kind, maker_id, editors, "
        + "authority_type, authority_amount, authority_currency, authority_codes::text, referral_role, reason, diff::text, supersedes, requested_at, "
        + "decision, checker_kind, checker_id, decision_comment, authority_check_id, decided_at, version";

    public static string StatusCode(ApprovalStatus status) => status switch
    {
        ApprovalStatus.PendingApproval => Pending,
        ApprovalStatus.Approved => Approved,
        ApprovalStatus.Rejected => Rejected,
        ApprovalStatus.Withdrawn => Withdrawn,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static string ActorKey(ActorRef actor) => $"{actor.KindCode}:{actor.Id}";

    public static Actor ToActor(ActorRef actor) => new()
    {
        Kind = actor.Kind switch
        {
            ActorKind.User => Actor.KindValue.User,
            ActorKind.Service => Actor.KindValue.Service,
            _ => Actor.KindValue.AiAgent,
        },
        Id = actor.Id,
    };

    public static async Task<ApprovalRow?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string legalEntity, Guid requestId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM plt.approval_request WHERE request_id = @id AND legal_entity = @le", connection, transaction);
        command.Parameters.AddWithValue("id", requestId);
        command.Parameters.AddWithValue("le", legalEntity);
        var rows = await ReadAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>The pending request of a subject, row-locked (a concurrent request for the same subject waits).</summary>
    public static async Task<ApprovalRow?> LockPendingAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string legalEntity, string approvalType, ObjectRef subject, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM plt.approval_request WHERE legal_entity = @le AND approval_type = @type AND object_module = @module "
            + "AND object_type = @otype AND object_id = @oid AND status = 'PendingApproval' FOR UPDATE",
            connection,
            transaction);
        command.Parameters.AddWithValue("le", legalEntity);
        command.Parameters.AddWithValue("type", approvalType);
        command.Parameters.AddWithValue("module", subject.Module.ToString());
        command.Parameters.AddWithValue("otype", subject.Type);
        command.Parameters.AddWithValue("oid", subject.Id);
        var rows = await ReadAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// Inserts a pending request; false when another pending request of the same subject won the race (the partial
    /// unique index, without aborting the caller's transaction).
    /// </summary>
    public static async Task<bool> TryInsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ApprovalRow row, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO plt.approval_request (request_id, legal_entity, approval_type, object_module, object_type, object_id, payload_hash, status, "
            + "maker_kind, maker_id, editors, authority_type, authority_amount, authority_currency, authority_codes, referral_role, reason, diff, "
            + "supersedes, requested_at, version) VALUES (@id, @le, @type, @module, @otype, @oid, @hash, 'PendingApproval', @mkind, @mid, @editors, "
            + "@atype, @amount, @currency, @codes::jsonb, @role, @reason, @diff::jsonb, @supersedes, @at, 1) "
            + "ON CONFLICT (legal_entity, approval_type, object_module, object_type, object_id) WHERE status = 'PendingApproval' DO NOTHING",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", row.RequestId);
        command.Parameters.AddWithValue("le", row.LegalEntity);
        command.Parameters.AddWithValue("type", row.ApprovalType);
        command.Parameters.AddWithValue("module", row.ObjectRef.Module.ToString());
        command.Parameters.AddWithValue("otype", row.ObjectRef.Type);
        command.Parameters.AddWithValue("oid", row.ObjectRef.Id);
        command.Parameters.AddWithValue("hash", row.PayloadHash.Value);
        command.Parameters.AddWithValue("mkind", row.Maker.KindCode);
        command.Parameters.AddWithValue("mid", row.Maker.Id);
        command.Parameters.Add(new NpgsqlParameter("editors", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = row.Editors.ToArray() });
        command.Parameters.AddWithValue("atype", row.AuthorityType);
        command.Parameters.Add(new NpgsqlParameter("amount", NpgsqlDbType.Numeric) { Value = (object?)row.AuthorityAmount?.Amount ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("currency", NpgsqlDbType.Text) { Value = (object?)row.AuthorityAmount?.Currency.Code ?? DBNull.Value });
        command.Parameters.AddWithValue("codes", JsonSerializer.Serialize(row.AuthorityCodes, SharedKernelJson.Options));
        command.Parameters.AddWithValue("role", row.ReferralRole);
        command.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = (object?)row.Reason ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("diff", NpgsqlDbType.Text) { Value = (object?)row.DiffJson ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("supersedes", NpgsqlDbType.Uuid) { Value = (object?)row.Supersedes ?? DBNull.Value });
        command.Parameters.AddWithValue("at", row.RequestedAt.ToUtcDateTime());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>Withdraws a pending request (superseded); false when it is no longer pending at <paramref name="version"/>.</summary>
    public static async Task<bool> TryWithdrawAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid requestId, int version, Instant at, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE plt.approval_request SET status = 'Withdrawn', decided_at = @at, version = version + 1 "
            + "WHERE request_id = @id AND status = 'PendingApproval' AND version = @version",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", requestId);
        command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("at", at.ToUtcDateTime());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Records the decision only if the request is still pending at the version and content hash the checker saw:
    /// of two racing decisions the second finds no pending row after the first commits (READ COMMITTED re-check) and
    /// gets false, never an error.
    /// </summary>
    public static async Task<bool> TryDecideAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        int version,
        Sha256Hash payloadHash,
        string decision,
        ActorRef checker,
        string? comment,
        Guid authorityCheckId,
        Instant at,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE plt.approval_request SET status = @decision, decision = @decision, checker_kind = @ckind, checker_id = @cid, "
            + "decision_comment = @comment, authority_check_id = @check, decided_at = @at, version = version + 1 "
            + "WHERE request_id = @id AND status = 'PendingApproval' AND version = @version AND payload_hash = @hash",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", requestId);
        command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("hash", payloadHash.Value);
        command.Parameters.AddWithValue("decision", decision);
        command.Parameters.AddWithValue("ckind", checker.KindCode);
        command.Parameters.AddWithValue("cid", checker.Id);
        command.Parameters.Add(new NpgsqlParameter("comment", NpgsqlDbType.Text) { Value = (object?)comment ?? DBNull.Value });
        command.Parameters.AddWithValue("check", authorityCheckId);
        command.Parameters.AddWithValue("at", at.ToUtcDateTime());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// The approvals inbox: requests in <paramref name="status"/> referred to one of <paramref name="roles"/>, excluding
    /// those the caller made or edited (REQ-PLT-115), oldest first; one row more than the page tells whether a next page exists.
    /// </summary>
    public static async Task<IReadOnlyList<ApprovalRow>> ListAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string legalEntity,
        string status,
        IReadOnlyCollection<string> roles,
        ActorRef caller,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM plt.approval_request WHERE legal_entity = @le AND status = @status AND referral_role = ANY(@roles) "
            + "AND NOT (maker_kind = @ckind AND maker_id = @cid) AND NOT (@ckey = ANY(editors)) "
            + "ORDER BY requested_at, request_id OFFSET @offset LIMIT @limit",
            connection,
            transaction);
        command.Parameters.AddWithValue("le", legalEntity);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.Add(new NpgsqlParameter("roles", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = roles.ToArray() });
        command.Parameters.AddWithValue("ckind", caller.KindCode);
        command.Parameters.AddWithValue("cid", caller.Id);
        command.Parameters.AddWithValue("ckey", ActorKey(caller));
        command.Parameters.AddWithValue("offset", offset);
        command.Parameters.AddWithValue("limit", limit + 1);
        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ApprovalRow>> ReadAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<ApprovalRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ApprovalRow
            {
                RequestId = reader.GetGuid(0),
                LegalEntity = reader.GetString(1),
                ApprovalType = reader.GetString(2),
                ObjectRef = new ObjectRef(Enum.Parse<ModuleCode>(reader.GetString(3)), reader.GetString(4), reader.GetString(5)),
                PayloadHash = Sha256Hash.Parse(reader.GetString(6)),
                Status = reader.GetString(7) switch
                {
                    Pending => ApprovalStatus.PendingApproval,
                    Approved => ApprovalStatus.Approved,
                    Rejected => ApprovalStatus.Rejected,
                    _ => ApprovalStatus.Withdrawn,
                },
                Maker = new ActorRef(ActorRef.ParseKind(reader.GetString(8)), reader.GetString(9)),
                Editors = reader.GetFieldValue<string[]>(10),
                AuthorityType = reader.GetString(11),
                AuthorityAmount = reader.IsDBNull(12) ? null : Money.Of(reader.GetDecimal(12), reader.GetString(13)),
                AuthorityCodes = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(14), SharedKernelJson.Options)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal),
                ReferralRole = reader.GetString(15),
                Reason = reader.IsDBNull(16) ? null : reader.GetString(16),
                DiffJson = reader.IsDBNull(17) ? null : reader.GetString(17),
                Supersedes = reader.IsDBNull(18) ? null : reader.GetGuid(18),
                RequestedAt = Instant.FromUtcDateTime(reader.GetFieldValue<DateTime>(19)),
                Decision = reader.IsDBNull(20) ? null : reader.GetString(20),
                Checker = reader.IsDBNull(21) ? null : new ActorRef(ActorRef.ParseKind(reader.GetString(21)), reader.GetString(22)),
                Comment = reader.IsDBNull(23) ? null : reader.GetString(23),
                AuthorityCheckId = reader.IsDBNull(24) ? null : reader.GetGuid(24),
                DecidedAt = reader.IsDBNull(25) ? null : Instant.FromUtcDateTime(reader.GetFieldValue<DateTime>(25)),
                Version = reader.GetInt32(26),
            });
        }

        return rows;
    }
}
