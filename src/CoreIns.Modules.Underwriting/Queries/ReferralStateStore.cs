using CoreIns.Platform.Persistence;
using Dapper;

namespace CoreIns.Modules.Underwriting.Queries;

/// <summary>A <c>uw.referral_state</c> row as read and written (BO-UW-05).</summary>
internal sealed class ReferralStateRecord
{
    public Guid LegalEntityId { get; set; }

    public Guid JobId { get; set; }

    public string Lifecycle { get; set; } = "Active";

    public bool AwaitingInfo { get; set; }

    public string? InfoComment { get; set; }

    public DateTime? SlaPausedAt { get; set; }

    public DateTime? SlaDue { get; set; }

    public string Priority { get; set; } = "NORMAL";

    public string? RequiredRole { get; set; }

    public string? ReferredBy { get; set; }

    public string RequestComment { get; set; } = string.Empty;

    public string RequestedBy { get; set; } = string.Empty;

    public DateTime RequestedAt { get; set; }

    public string? LockOwner { get; set; }

    public DateTime? LockAcquiredAt { get; set; }

    public DateTime? LockExpiresAt { get; set; }

    public int RecordVersion { get; set; } = 1;

    public DateTime UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;

    /// <summary>The holder of the review lock at <paramref name="now"/>; null when nobody holds it or the lease expired.</summary>
    public string? LiveLockOwner(DateTime now) => LockOwner is not null && LockExpiresAt > now ? LockOwner : null;

    public bool IsActive => string.Equals(Lifecycle, "Active", StringComparison.Ordinal);

    public void ClearLock()
    {
        LockOwner = null;
        LockAcquiredAt = null;
        LockExpiresAt = null;
    }
}

/// <summary>Dapper reads and writes of <c>uw.referral_state</c>; every row is filtered by the legal entity.</summary>
internal sealed class ReferralStateStore(DbSession session)
{
    private const string Columns =
        """
        legal_entity_id AS LegalEntityId, job_id AS JobId, lifecycle AS Lifecycle, awaiting_info AS AwaitingInfo, info_comment AS InfoComment,
        sla_paused_at AS SlaPausedAt, sla_due AS SlaDue, priority AS Priority, required_role AS RequiredRole, referred_by AS ReferredBy,
        request_comment AS RequestComment, requested_by AS RequestedBy, requested_at AS RequestedAt, lock_owner AS LockOwner,
        lock_acquired_at AS LockAcquiredAt, lock_expires_at AS LockExpiresAt, record_version AS RecordVersion, updated_at AS UpdatedAt,
        updated_by AS UpdatedBy
        """;

    public async Task<ReferralStateRecord?> GetAsync(Guid legalEntity, Guid jobId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<ReferralStateRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM uw.referral_state WHERE legal_entity_id = @legalEntity AND job_id = @jobId",
            new { legalEntity, jobId }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<Guid, ReferralStateRecord>> GetManyAsync(Guid legalEntity, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken)
    {
        if (jobIds.Count == 0)
        {
            return new Dictionary<Guid, ReferralStateRecord>();
        }

        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ReferralStateRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM uw.referral_state WHERE legal_entity_id = @legalEntity AND job_id = ANY(@jobIds)",
            new { legalEntity, jobIds = jobIds.ToArray() }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToDictionary(r => r.JobId);
    }

    public async Task InsertAsync(ReferralStateRecord r, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO uw.referral_state (legal_entity_id, job_id, lifecycle, awaiting_info, info_comment, sla_paused_at, sla_due, priority, required_role, referred_by,
                   request_comment, requested_by, requested_at, lock_owner, lock_acquired_at, lock_expires_at, record_version, updated_at, updated_by)
            VALUES (@LegalEntityId, @JobId, @Lifecycle, @AwaitingInfo, @InfoComment, @SlaPausedAt, @SlaDue, @Priority, @RequiredRole, @ReferredBy,
                   @RequestComment, @RequestedBy, @RequestedAt, @LockOwner, @LockAcquiredAt, @LockExpiresAt, 1, @UpdatedAt, @UpdatedBy)
            """, r, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        r.RecordVersion = 1;
    }

    /// <summary>Writes the record when its <c>RecordVersion</c> is still the stored one (then bumps it); false when someone changed it meanwhile.</summary>
    public async Task<bool> UpdateAsync(ReferralStateRecord r, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE uw.referral_state
               SET lifecycle = @Lifecycle, awaiting_info = @AwaitingInfo, info_comment = @InfoComment, sla_paused_at = @SlaPausedAt, sla_due = @SlaDue,
                   priority = @Priority, required_role = @RequiredRole, referred_by = @ReferredBy, request_comment = @RequestComment,
                   requested_by = @RequestedBy, requested_at = @RequestedAt, lock_owner = @LockOwner, lock_acquired_at = @LockAcquiredAt,
                   lock_expires_at = @LockExpiresAt, updated_at = @UpdatedAt, updated_by = @UpdatedBy, record_version = record_version + 1
             WHERE legal_entity_id = @LegalEntityId AND job_id = @JobId AND record_version = @RecordVersion
            """, r, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (rows == 1)
        {
            r.RecordVersion++;
        }

        return rows == 1;
    }
}
