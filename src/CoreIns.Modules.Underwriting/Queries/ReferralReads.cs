using System.Data;
using CoreIns.Platform.Persistence;
using Dapper;

namespace CoreIns.Modules.Underwriting.Queries;

/// <summary>A referred job in a workbench queue with its sort key.</summary>
internal sealed class ReferralJobRecord
{
    public Guid JobId { get; set; }

    public DateTime SortAt { get; set; }
}

/// <summary>Jobs per workbench queue.</summary>
internal sealed class ReferralCountsRecord
{
    public long Open { get; set; }

    public long ApprovedToday { get; set; }

    public long Rejected { get; set; }

    public long DecidedByMeToday { get; set; }
}

/// <summary>The derived risk facts of the job's latest evaluation from POL.</summary>
internal sealed class EvaluationFactsRecord
{
    public string Facts { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public string RuleSetCode { get; set; } = string.Empty;

    public string RuleSetVersion { get; set; } = string.Empty;

    public string Trace { get; set; } = "{}";
}

/// <summary>The workbench queues of the referral workbench (D-USR-13), derived from <c>uw.issue</c> per job and legal entity.</summary>
internal enum ReferralQueue
{
    Open,
    ApprovedToday,
    Rejected,
    DecidedByMeToday,
}

/// <summary>
/// Dapper reads of the referral workbench over <c>uw.issue</c> and <c>uw.evaluation</c>, always filtered by the legal entity.
/// A job is in a queue when one of its issues is: Open (OPEN), Rejected (REJECTED), approved on the business day
/// (APPROVED_TODAY) or decided by the caller on the business day (DECIDED_BY_ME_TODAY).
/// </summary>
internal sealed class ReferralReads(DbSession session)
{
    private const string Jobs =
        """
        WITH jobs AS (
            SELECT i.job_id,
                   bool_or(i.status = 'Open') AS has_open,
                   bool_or(i.status = 'Rejected') AS has_rejected,
                   bool_or(i.decision = 'APPROVE' AND i.decided_at >= @dayStart AND i.decided_at < @dayEnd) AS approved_today,
                   bool_or(i.decided_by = @me AND i.decided_at >= @dayStart AND i.decided_at < @dayEnd) AS mine_today,
                   coalesce(min(i.created_at) FILTER (WHERE i.status IN ('Open', 'Rejected')), min(i.created_at)) AS raised_at,
                   max(i.decided_at) AS last_decided_at
              FROM uw.issue i
             WHERE i.legal_entity_id = @legalEntity
             GROUP BY i.job_id)
        """;

    /// <summary>One page of the queue: OPEN and REJECTED oldest raised first; the decided queues latest decision first.</summary>
    public async Task<IReadOnlyList<ReferralJobRecord>> JobsAsync(
        Guid legalEntity, ReferralQueue queue, string me, DateTime dayStart, DateTime dayEnd, (DateTime SortAt, Guid JobId)? after, int limit,
        CancellationToken cancellationToken)
    {
        var (filter, sort, descending) = queue switch
        {
            ReferralQueue.Open => ("has_open", "raised_at", false),
            ReferralQueue.Rejected => ("has_rejected", "raised_at", false),
            ReferralQueue.ApprovedToday => ("approved_today", "last_decided_at", true),
            _ => ("mine_today", "last_decided_at", true),
        };
        var keyset = descending ? "<" : ">";
        var order = descending ? "DESC" : "ASC";
        var args = Window(legalEntity, me, dayStart, dayEnd);
        args.Add("limit", limit);
        args.Add("afterAt", after?.SortAt, DbType.DateTime);
        args.Add("afterId", after?.JobId, DbType.Guid);
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<ReferralJobRecord>(new CommandDefinition(
            $"""
            {Jobs}
            SELECT job_id AS JobId, {sort} AS SortAt FROM jobs
             WHERE {filter}
               AND (@afterAt::timestamptz IS NULL OR ({sort}, job_id) {keyset} (@afterAt::timestamptz, @afterId::uuid))
             ORDER BY {sort} {order}, job_id {order}
             LIMIT @limit
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
    }

    public async Task<ReferralCountsRecord> CountsAsync(Guid legalEntity, string me, DateTime dayStart, DateTime dayEnd, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleAsync<ReferralCountsRecord>(new CommandDefinition(
            $"""
            {Jobs}
            SELECT count(*) FILTER (WHERE has_open) AS Open, count(*) FILTER (WHERE approved_today) AS ApprovedToday,
                   count(*) FILTER (WHERE has_rejected) AS Rejected, count(*) FILTER (WHERE mine_today) AS DecidedByMeToday
              FROM jobs
            """, Window(legalEntity, me, dayStart, dayEnd), session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Every issue of the given jobs in the legal entity, oldest first.</summary>
    public async Task<IReadOnlyList<(Guid JobId, IssueRecord Issue)>> IssuesOfJobsAsync(Guid legalEntity, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<IssueRecord>(new CommandDefinition(
            $"""
            SELECT {UnderwritingStore.IssueColumns}
              FROM uw.issue i JOIN uw.evaluation e ON e.evaluation_id = i.raised_evaluation_id
             WHERE i.legal_entity_id = @legalEntity AND i.job_id = ANY(@jobIds)
             ORDER BY i.created_at, i.issue_id
            """, new { legalEntity, jobIds = jobIds.ToArray() }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => (r.JobId, r))];
    }

    /// <summary>The jobs among <paramref name="jobIds"/> that <paramref name="actor"/> evaluated or worked on as POL reported it (SOD-UW-02).</summary>
    public async Task<IReadOnlySet<Guid>> WorkedOnAsync(Guid legalEntity, IReadOnlyCollection<Guid> jobIds, string actor, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT DISTINCT job_id FROM uw.evaluation
             WHERE legal_entity_id = @legalEntity AND job_id = ANY(@jobIds) AND (created_by = @actor OR @actor = ANY(job_participants))
            """, new { legalEntity, jobIds = jobIds.ToArray(), actor }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToHashSet();
    }

    /// <summary>The facts of the job's latest evaluation that recorded them (POL's in-process evaluation), or null.</summary>
    public async Task<EvaluationFactsRecord?> LatestFactsAsync(Guid legalEntity, Guid jobId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<EvaluationFactsRecord>(new CommandDefinition(
            """
            SELECT facts::text AS Facts, created_at AS CreatedAt, rule_set_code AS RuleSetCode, rule_set_version AS RuleSetVersion, trace::text AS Trace
              FROM uw.evaluation
             WHERE legal_entity_id = @legalEntity AND job_id = @jobId AND facts IS NOT NULL
             ORDER BY created_at DESC, evaluation_id DESC LIMIT 1
            """, new { legalEntity, jobId }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static DynamicParameters Window(Guid legalEntity, string me, DateTime dayStart, DateTime dayEnd)
    {
        var args = new DynamicParameters(new { legalEntity, me });
        // UTC DateTimes bind as timestamptz (Npgsql), the type of decided_at.
        args.Add("dayStart", DateTime.SpecifyKind(dayStart, DateTimeKind.Utc));
        args.Add("dayEnd", DateTime.SpecifyKind(dayEnd, DateTimeKind.Utc));
        return args;
    }
}
