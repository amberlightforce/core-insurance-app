using System.Collections.Concurrent;
using System.Data;
using CoreIns.Modules.Underwriting.Domain;
using CoreIns.Platform.Context;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using Dapper;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Underwriting.Queries;

/// <summary>Module options (<c>Underwriting:*</c>).</summary>
internal sealed class UnderwritingOptions
{
    public const string Section = "Underwriting";

    /// <summary>Product codes for which the illustrative motor rule set is published (stands in for rule-set authoring and approval).</summary>
    public List<string> SeedProductCodes { get; set; } = ["MOTOR-GR"];
}

/// <summary>A stored rule set version as read.</summary>
internal sealed class RuleSetRecord
{
    public string Hash { get; set; } = string.Empty;

    public string Definition { get; set; } = string.Empty;
}

/// <summary>An issue row as read, with the actor of the evaluation that raised it.</summary>
internal sealed class IssueRecord
{
    public Guid IssueId { get; set; }

    public Guid JobId { get; set; }

    public string IssueType { get; set; } = string.Empty;

    public string IssueKey { get; set; } = string.Empty;

    public string BlockingPoint { get; set; } = string.Empty;

    public string Severity { get; set; } = string.Empty;

    public string Lane { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string RuleId { get; set; } = string.Empty;

    public string MessageEn { get; set; } = string.Empty;

    public string MessageEl { get; set; } = string.Empty;

    public int RecordVersion { get; set; }

    public DateTime CreatedAt { get; set; }

    public string RaisedBy { get; set; } = string.Empty;

    public string? CloseReason { get; set; }

    public string? Fingerprint { get; set; }

    public string? DecisionFingerprint { get; set; }

    public string? Decision { get; set; }

    public string? DecidedBy { get; set; }

    public DateTime? DecidedAt { get; set; }

    public string? DecisionReason { get; set; }

    public string? DecisionMessage { get; set; }

    public Guid? AuthorityCheckId { get; set; }

    /// <summary>The reconciliation's view (char(64) values come back padded only when shorter; trimmed for safety).</summary>
    public ReconcilableIssue ForReconciliation() => new(IssueId, IssueKey, Status, Fingerprint?.Trim(), DecisionFingerprint?.Trim());
}

/// <summary>Dapper reads and writes of the uw schema on the scope's connection and transaction.</summary>
internal sealed class UnderwritingStore(DbSession session, IClock clock, RequestContext context, IOptions<UnderwritingOptions> options)
{
    private static readonly ConcurrentDictionary<string, CompiledRuleSet> Cache = new(StringComparer.Ordinal);

    /// <summary>Publishes the built-in illustrative rule set when it is not stored yet (idempotent, content-addressed).</summary>
    public async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var productCode in options.Value.SeedProductCodes)
        {
            foreach (var dto in new[] { BuiltInRuleSets.Quote(productCode), BuiltInRuleSets.Bind(productCode) })
            {
            var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM uw.rule_set_version WHERE rule_set_code = @code AND version_no = @version AND product_code = @product)",
                new { code = dto.Code, version = dto.Version, product = productCode }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (exists)
            {
                continue;
            }

            var compiled = CompiledRuleSet.Compile(dto);
            var args = new DynamicParameters(new
            {
                code = dto.Code, version = dto.Version, product = productCode, checkpoint = dto.Checkpoint, hash = compiled.Hash, status = "Active", dataStatus = dto.DataStatus,
                definition = RuleSetJson.Serialize(dto), now = clock.Now.ToUtcDateTime(), actor = context.Actor.ToString(),
            });
            args.Add("fromDate", DateOnly.ParseExact(dto.EffectiveFrom, "yyyy-MM-dd"), DbType.Date);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO uw.rule_set_version (rule_set_code, version_no, product_code, checkpoint, content_hash, status, effective_from, data_status, definition, created_at, created_by)
                VALUES (@code, @version, @product, @checkpoint, @hash, @status, @fromDate, @dataStatus, @definition::jsonb, @now, @actor) ON CONFLICT DO NOTHING
                """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The active rule set of the product effective on <paramref name="date"/> (REQ-UW-043); null when none.</summary>
    public async Task<CompiledRuleSet?> ResolveAsync(string productCode, string checkpoint, DateOnly date, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new { product = productCode, checkpoint });
        args.Add("asOf", date, DbType.Date);
        var row = await connection.QueryFirstOrDefaultAsync<RuleSetRecord>(new CommandDefinition(
            """
            SELECT content_hash AS Hash, definition::text AS Definition FROM uw.rule_set_version
             WHERE product_code = @product AND checkpoint = @checkpoint AND status = 'Active' AND effective_from <= @asOf
             ORDER BY effective_from DESC, version_no DESC LIMIT 1
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is not { } found)
        {
            return null;
        }

        var compiled = Cache.GetOrAdd(found.Hash, _ => CompiledRuleSet.Compile(RuleSetJson.Deserialize(found.Definition)));
        return string.Equals(compiled.Hash, found.Hash, StringComparison.Ordinal) ? compiled : null;
    }

    public async Task InsertEvaluationAsync(
        Guid evaluationId, Guid legalEntity, Guid jobId, string checkpoint, CompiledRuleSet ruleSet, string snapshotRef, string? snapshotHash,
        string outcome, string lane, string trace, string actor, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO uw.evaluation (evaluation_id, legal_entity_id, job_id, checkpoint, rule_set_code, rule_set_version, rule_set_hash, snapshot_ref, snapshot_hash, outcome, lane, trace, created_at, created_by)
            VALUES (@evaluationId, @legalEntity, @jobId, @checkpoint, @code, @version, @hash, @snapshotRef, @snapshotHash, @outcome, @lane, @trace::jsonb, @now, @actor)
            """,
            new
            {
                evaluationId, legalEntity, jobId, checkpoint, code = ruleSet.Dto.Code, version = ruleSet.Dto.Version, hash = ruleSet.Hash, snapshotRef, snapshotHash,
                outcome, lane, trace, now = clock.Now.ToUtcDateTime(), actor,
            }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private const string IssueColumns =
        """
        i.issue_id AS IssueId, i.job_id AS JobId, i.issue_type AS IssueType, i.issue_key AS IssueKey, i.blocking_point AS BlockingPoint,
        i.severity AS Severity, i.lane AS Lane, i.status AS Status, i.rule_id AS RuleId, i.message_en AS MessageEn, i.message_el AS MessageEl,
        i.record_version AS RecordVersion, i.created_at AS CreatedAt, e.created_by AS RaisedBy, i.close_reason AS CloseReason,
        i.fingerprint AS Fingerprint, i.decision_fingerprint AS DecisionFingerprint, i.decision AS Decision, i.decided_by AS DecidedBy,
        i.decided_at AS DecidedAt, i.decision_reason AS DecisionReason, i.decision_message AS DecisionMessage, i.authority_check_id AS AuthorityCheckId
        """;

    /// <summary>The job's non-terminal issues (Open, Approved, ApprovedWithConditions, Rejected), oldest first.</summary>
    public async Task<IReadOnlyList<IssueRecord>> ActiveIssuesAsync(Guid legalEntity, Guid jobId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<IssueRecord>(new CommandDefinition(
            $"""
            SELECT {IssueColumns}
              FROM uw.issue i JOIN uw.evaluation e ON e.evaluation_id = i.raised_evaluation_id
             WHERE i.legal_entity_id = @legalEntity AND i.job_id = @jobId AND i.status IN ('Open', 'Approved', 'ApprovedWithConditions', 'Rejected')
             ORDER BY i.created_at, i.issue_id
            """, new { legalEntity, jobId }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
    }

    /// <summary>
    /// Issues of a job (any status unless one is asked for) or, without a job, the legal entity's issues in one status (default
    /// Open: the referral queue), oldest first; keyset paging on (created_at, issue_id).
    /// </summary>
    public async Task<IReadOnlyList<IssueRecord>> ListIssuesAsync(
        Guid legalEntity, Guid? jobId, string? status, (DateTime CreatedAt, Guid IssueId)? after, int limit, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new { legalEntity, status = status ?? (jobId is null ? IssueStatus.Open : null), limit });
        args.Add("jobId", jobId, DbType.Guid);
        args.Add("afterAt", after?.CreatedAt, DbType.DateTime);
        args.Add("afterId", after?.IssueId, DbType.Guid);
        return (await connection.QueryAsync<IssueRecord>(new CommandDefinition(
            $"""
            SELECT {IssueColumns}
              FROM uw.issue i JOIN uw.evaluation e ON e.evaluation_id = i.raised_evaluation_id
             WHERE i.legal_entity_id = @legalEntity
               AND (@jobId::uuid IS NULL OR i.job_id = @jobId::uuid)
               AND (@status::text IS NULL OR i.status = @status::text)
               AND (@afterAt::timestamptz IS NULL OR (i.created_at, i.issue_id) > (@afterAt::timestamptz, @afterId::uuid))
             ORDER BY i.created_at, i.issue_id
             LIMIT @limit
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
    }

    /// <summary>The issues with the given ids in the legal entity, locked for the decision (SELECT … FOR UPDATE).</summary>
    public async Task<IReadOnlyList<IssueRecord>> LockIssuesAsync(Guid legalEntity, IReadOnlyCollection<Guid> issueIds, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<IssueRecord>(new CommandDefinition(
            $"""
            SELECT {IssueColumns}
              FROM uw.issue i JOIN uw.evaluation e ON e.evaluation_id = i.raised_evaluation_id
             WHERE i.legal_entity_id = @legalEntity AND i.issue_id = ANY(@issueIds)
             ORDER BY i.issue_id
               FOR UPDATE OF i
            """, new { legalEntity, issueIds = issueIds.ToArray() }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
    }

    /// <summary>Everyone who ran an evaluation for the job (SOD-UW-02: the quoting and binding side of the referral).</summary>
    public async Task<IReadOnlyList<string>> EvaluatorsAsync(Guid legalEntity, Guid jobId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT created_by FROM uw.evaluation WHERE legal_entity_id = @legalEntity AND job_id = @jobId",
            new { legalEntity, jobId }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
    }

    public async Task InsertIssueAsync(
        Guid issueId, Guid legalEntity, Guid jobId, string issueType, string issueKey, string blockingPoint, string severity, string lane, string ruleId,
        string messageEn, string messageEl, Guid evaluationId, string fingerprint, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO uw.issue (issue_id, legal_entity_id, job_id, issue_type, issue_key, blocking_point, severity, lane, status, rule_id, message_en, message_el, raised_evaluation_id, record_version, created_at, fingerprint)
            VALUES (@issueId, @legalEntity, @jobId, @issueType, @issueKey, @blockingPoint, @severity, @lane, 'Open', @ruleId, @messageEn, @messageEl, @evaluationId, 1, @now, @fingerprint)
            """,
            new { issueId, legalEntity, jobId, issueType, issueKey, blockingPoint, severity, lane, ruleId, messageEn, messageEl, evaluationId, now = clock.Now.ToUtcDateTime(), fingerprint },
            session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Stores the current fingerprint of an Open issue; a change bumps the record version (a decision prepared on the old facts becomes stale).</summary>
    public async Task RefreshFingerprintAsync(Guid issueId, string fingerprint, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE uw.issue SET fingerprint = @fingerprint, record_version = record_version + 1 WHERE issue_id = @issueId AND status = 'Open' AND fingerprint IS DISTINCT FROM @fingerprint",
            new { issueId, fingerprint }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Closes an Open or Rejected issue.</summary>
    public async Task CloseIssueAsync(Guid issueId, Guid evaluationId, string reason, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE uw.issue SET status = 'Closed', closed_evaluation_id = @evaluationId, close_reason = @reason, closed_at = @now, record_version = record_version + 1 WHERE issue_id = @issueId AND status IN ('Open', 'Rejected')",
            new { issueId, evaluationId, reason, now = clock.Now.ToUtcDateTime() }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Invalidates an approval (Approved → Invalidated, REQ-UW-093); the decision fields stay as history.</summary>
    public async Task InvalidateIssueAsync(Guid issueId, Guid evaluationId, string reason, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE uw.issue SET status = 'Invalidated', closed_evaluation_id = @evaluationId, close_reason = @reason, closed_at = @now, record_version = record_version + 1 WHERE issue_id = @issueId AND status IN ('Approved', 'ApprovedWithConditions')",
            new { issueId, evaluationId, reason, now = clock.Now.ToUtcDateTime() }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Records a decision on an Open issue at the version read; false when the issue changed meanwhile.</summary>
    public async Task<bool> DecideAsync(
        Guid issueId, int recordVersion, string status, string decision, string decisionFingerprint, string decidedBy, string reason, string? message, Guid authorityCheckId,
        CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE uw.issue SET status = @status, decision = @decision, decision_fingerprint = @decisionFingerprint, decided_by = @decidedBy, decided_at = @now,
                   decision_reason = @reason, decision_message = @message, authority_check_id = @authorityCheckId, record_version = record_version + 1
             WHERE issue_id = @issueId AND status = 'Open' AND record_version = @recordVersion
            """,
            new { issueId, recordVersion, status, decision, decisionFingerprint, decidedBy, now = clock.Now.ToUtcDateTime(), reason, message, authorityCheckId },
            session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false) == 1;
    }

    /// <summary>Issues of the job that block at or before the given point: Open or Rejected (BR-UW-001; a PRE_QUOTE issue still blocks PRE_BIND).</summary>
    public async Task<IReadOnlyList<IssueRecord>> BlockingAsync(Guid legalEntity, Guid jobId, string blockingPoint, CancellationToken cancellationToken)
    {
        var rank = blockingPoint switch { "PRE_QUOTE" => 1, "PRE_BIND" => 2, "PRE_ISSUE" => 3, _ => 0 };
        return (await ActiveIssuesAsync(legalEntity, jobId, cancellationToken).ConfigureAwait(false))
            .Where(i => IssueStatus.Blocks(i.Status))
            .Where(i => i.BlockingPoint switch { "PRE_QUOTE" => 1, "PRE_BIND" => 2, "PRE_ISSUE" => 3, _ => 99 } <= rank)
            .ToList();
    }
}
