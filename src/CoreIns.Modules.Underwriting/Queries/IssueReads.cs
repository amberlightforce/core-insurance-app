using System.Data;
using CoreIns.Platform.Persistence;
using Dapper;

namespace CoreIns.Modules.Underwriting.Queries;

/// <summary>The rule set (code, version, content hash) of the evaluation that raised an issue.</summary>
internal sealed class IssueRuleSetRecord
{
    public string Code { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Hash { get; set; } = string.Empty;
}

/// <summary>One event of an issue's approval history as read: a decision, or the invalidation of an approval.</summary>
internal sealed class ApprovalHistoryRecord
{
    public Guid IssueId { get; set; }

    public Guid JobId { get; set; }

    public string IssueKey { get; set; } = string.Empty;

    public string IssueType { get; set; } = string.Empty;

    /// <summary>0 = decided, 1 = invalidated (the order of two events at the same instant).</summary>
    public int Kind { get; set; }

    public DateTime At { get; set; }

    public string? Decision { get; set; }

    public string? Actor { get; set; }

    public string? Reason { get; set; }

    public string? Fingerprint { get; set; }

    public Guid? AuthorityCheckId { get; set; }

    public Guid? InvalidatedByEvaluationId { get; set; }
}

/// <summary>
/// Dapper reads of the issue reads (BO-UW-03) over <c>uw.issue</c>, <c>uw.evaluation</c>, always filtered by the legal entity.
/// Read only: no read takes a lock or writes.
/// </summary>
internal sealed class IssueReads(DbSession session)
{
    private static readonly string[] ChannelStatuses = ["Open", "Approved", "ApprovedWithConditions", "Rejected"];

    /// <summary>One issue of the legal entity, or null.</summary>
    public async Task<IssueRecord?> GetAsync(Guid legalEntity, Guid issueId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<IssueRecord>(new CommandDefinition(
            $"""
            SELECT {UnderwritingStore.IssueColumns}
              FROM uw.issue i JOIN uw.evaluation e ON e.evaluation_id = i.raised_evaluation_id
             WHERE i.legal_entity_id = @legalEntity AND i.issue_id = @issueId
            """, new { legalEntity, issueId }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>The rule set the evaluation that raised the issue ran.</summary>
    public async Task<IssueRuleSetRecord?> RuleSetOfAsync(Guid legalEntity, Guid issueId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<IssueRuleSetRecord>(new CommandDefinition(
            """
            SELECT e.rule_set_code AS Code, e.rule_set_version AS Version, e.rule_set_hash AS Hash
              FROM uw.issue i JOIN uw.evaluation e ON e.evaluation_id = i.raised_evaluation_id
             WHERE i.legal_entity_id = @legalEntity AND i.issue_id = @issueId
            """, new { legalEntity, issueId }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>The issues of a job a channel may show (Open, Approved, ApprovedWithConditions, Rejected), oldest first; keyset on (created_at, issue_id).</summary>
    public async Task<IReadOnlyList<IssueRecord>> ChannelIssuesAsync(
        Guid legalEntity, Guid jobId, (DateTime CreatedAt, Guid IssueId)? after, int limit, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new { legalEntity, jobId, statuses = ChannelStatuses, limit });
        args.Add("afterAt", after?.CreatedAt, DbType.DateTime);
        args.Add("afterId", after?.IssueId, DbType.Guid);
        return (await connection.QueryAsync<IssueRecord>(new CommandDefinition(
            $"""
            SELECT {UnderwritingStore.IssueColumns}
              FROM uw.issue i JOIN uw.evaluation e ON e.evaluation_id = i.raised_evaluation_id
             WHERE i.legal_entity_id = @legalEntity AND i.job_id = @jobId AND i.status = ANY(@statuses)
               AND (@afterAt::timestamptz IS NULL OR (i.created_at, i.issue_id) > (@afterAt::timestamptz, @afterId::uuid))
             ORDER BY i.created_at, i.issue_id
             LIMIT @limit
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
    }

    /// <summary>
    /// The decisions and approval invalidations of the legal entity's issues (a job and/or an issue key), oldest first; keyset on
    /// (at, issue_id, kind). An invalidation is the issue's close with status Invalidated; the decision stays on the row as history.
    /// </summary>
    public async Task<IReadOnlyList<ApprovalHistoryRecord>> HistoryAsync(
        Guid legalEntity, Guid? jobId, string? issueKey, (DateTime At, Guid IssueId, int Kind)? after, int limit, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new { legalEntity, issueKey, limit });
        args.Add("jobId", jobId, DbType.Guid);
        args.Add("afterAt", after?.At, DbType.DateTime);
        args.Add("afterId", after?.IssueId, DbType.Guid);
        args.Add("afterKind", after?.Kind, DbType.Int32);
        return (await connection.QueryAsync<ApprovalHistoryRecord>(new CommandDefinition(
            """
            WITH h AS (
                SELECT i.issue_id AS IssueId, i.job_id AS JobId, i.issue_key AS IssueKey, i.issue_type AS IssueType, 0 AS Kind, i.decided_at AS At,
                       i.decision AS Decision, i.decided_by AS Actor, i.decision_reason AS Reason, i.decision_fingerprint AS Fingerprint,
                       i.authority_check_id AS AuthorityCheckId, NULL::uuid AS InvalidatedByEvaluationId
                  FROM uw.issue i
                 WHERE i.legal_entity_id = @legalEntity AND i.decision IS NOT NULL AND i.decided_at IS NOT NULL
                   AND (@jobId::uuid IS NULL OR i.job_id = @jobId::uuid) AND (@issueKey::text IS NULL OR i.issue_key = @issueKey::text)
                UNION ALL
                SELECT i.issue_id, i.job_id, i.issue_key, i.issue_type, 1, i.closed_at,
                       NULL, NULL, i.close_reason, i.decision_fingerprint, NULL::uuid, i.closed_evaluation_id
                  FROM uw.issue i
                 WHERE i.legal_entity_id = @legalEntity AND i.status = 'Invalidated' AND i.closed_at IS NOT NULL
                   AND (@jobId::uuid IS NULL OR i.job_id = @jobId::uuid) AND (@issueKey::text IS NULL OR i.issue_key = @issueKey::text)
            )
            SELECT * FROM h
             WHERE @afterAt::timestamptz IS NULL OR (h.At, h.IssueId, h.Kind) > (@afterAt::timestamptz, @afterId::uuid, @afterKind::int)
             ORDER BY h.At, h.IssueId, h.Kind
             LIMIT @limit
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
    }
}
