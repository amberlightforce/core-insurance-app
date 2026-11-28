using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using Dapper;

namespace CoreIns.Modules.Underwriting.Queries;

/// <summary>SQL of the issue lifecycle commands (BO-UW-04): reopen, manual issues and manual close.</summary>
internal sealed class IssueLifecycleStore(DbSession session, IClock clock)
{
    /// <summary>The newest evaluation of the job (a manual issue is anchored to it); null when the job was never evaluated.</summary>
    public async Task<Guid?> LatestEvaluationAsync(Guid legalEntity, Guid jobId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT evaluation_id FROM uw.evaluation WHERE legal_entity_id = @legalEntity AND job_id = @jobId ORDER BY created_at DESC, evaluation_id DESC LIMIT 1",
            new { legalEntity, jobId }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Approved, ApprovedWithConditions or Rejected to Invalidated / Closed with <paramref name="reason"/>; false when the row changed meanwhile.</summary>
    public async Task<bool> RetireForReopenAsync(Guid issueId, int recordVersion, string newStatus, string reason, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE uw.issue SET status = @newStatus, close_reason = @reason, closed_at = @now, record_version = record_version + 1
             WHERE issue_id = @issueId AND record_version = @recordVersion AND status IN ('Approved', 'ApprovedWithConditions', 'Rejected')
            """,
            new { issueId, recordVersion, newStatus, reason, now = clock.Now.ToUtcDateTime() }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false) == 1;
    }

    /// <summary>A new Open issue for the key of <paramref name="oldIssueId"/>: same rule, messages, fingerprint and raising evaluation, no decision.</summary>
    public async Task CopyAsOpenAsync(Guid oldIssueId, Guid newIssueId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO uw.issue (issue_id, legal_entity_id, job_id, issue_type, issue_key, blocking_point, severity, lane, status, rule_id, message_en, message_el,
                                  raised_evaluation_id, record_version, created_at, fingerprint)
            SELECT @newIssueId, legal_entity_id, job_id, issue_type, issue_key, blocking_point, severity, lane, 'Open', rule_id, message_en, message_el,
                   raised_evaluation_id, 1, @now, fingerprint
              FROM uw.issue WHERE issue_id = @oldIssueId
            """,
            new { oldIssueId, newIssueId, now = clock.Now.ToUtcDateTime() }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Closes an issue that does not block (Open non-blocking, Approved) at the version read; false when it changed meanwhile.</summary>
    public async Task<bool> CloseAsync(Guid issueId, int recordVersion, string reason, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE uw.issue SET status = 'Closed', close_reason = @reason, closed_at = @now, record_version = record_version + 1
             WHERE issue_id = @issueId AND record_version = @recordVersion AND status IN ('Open', 'Approved', 'ApprovedWithConditions')
            """,
            new { issueId, recordVersion, reason, now = clock.Now.ToUtcDateTime() }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false) == 1;
    }
}
