using System.Data;
using CoreIns.Platform.Context;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using Dapper;

namespace CoreIns.Modules.Underwriting.Queries;

/// <summary>A rule-set version row as read for authoring.</summary>
internal sealed class RuleSetVersionRecord
{
    public string RuleSetCode { get; set; } = string.Empty;

    public string VersionNo { get; set; } = string.Empty;

    public string ProductCode { get; set; } = string.Empty;

    public string Checkpoint { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public DateTime EffectiveFrom { get; set; }

    public string DataStatus { get; set; } = string.Empty;

    public string Definition { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public int RecordVersion { get; set; }

    public string? SubmittedBy { get; set; }

    public DateTime? SubmittedAt { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    public string? DecidedBy { get; set; }

    public DateTime? DecidedAt { get; set; }

    public string? DecisionReason { get; set; }
}

/// <summary>A stored evaluation as read for simulation: its recorded hits and the facts the rules read (no personal data).</summary>
internal sealed class StoredEvaluationRecord
{
    public Guid EvaluationId { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public string Facts { get; set; } = string.Empty;

    public string Trace { get; set; } = string.Empty;
}

/// <summary>Dapper reads and writes of <c>uw.rule_set_version</c> for authoring (drafts) and of stored evaluations for simulation.</summary>
internal sealed class RuleSetAuthoringStore(DbSession session, IClock clock, RequestContext context)
{
    private const string Columns =
        "rule_set_code AS RuleSetCode, version_no AS VersionNo, product_code AS ProductCode, checkpoint AS Checkpoint, content_hash AS ContentHash, status AS Status, "
        + "effective_from AS EffectiveFrom, data_status AS DataStatus, definition::text AS Definition, created_at AS CreatedAt, created_by AS CreatedBy, "
        + "record_version AS RecordVersion, submitted_by AS SubmittedBy, submitted_at AS SubmittedAt, approval_request_id AS ApprovalRequestId, "
        + "decided_by AS DecidedBy, decided_at AS DecidedAt, decision_reason AS DecisionReason";

    public async Task<RuleSetVersionRecord?> GetAsync(string code, string version, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<RuleSetVersionRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM uw.rule_set_version WHERE rule_set_code = @code AND version_no = @version",
            new { code, version }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>The latest version of a code (by creation time; null when the code does not exist).</summary>
    public async Task<RuleSetVersionRecord?> LatestAsync(string code, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<RuleSetVersionRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM uw.rule_set_version WHERE rule_set_code = @code ORDER BY created_at DESC, version_no DESC LIMIT 1",
            new { code }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> VersionsAsync(string code, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return [.. await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT version_no FROM uw.rule_set_version WHERE rule_set_code = @code", new { code }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)];
    }

    /// <summary>Inserts a Draft version. Returns false when the code and version already exist.</summary>
    public async Task<bool> InsertDraftAsync(
        string code, string version, string productCode, string checkpoint, string hash, DateOnly effectiveFrom, string dataStatus, string definition,
        CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new
        {
            code, version, product = productCode, checkpoint, hash, dataStatus, definition, now = clock.Now.ToUtcDateTime(), actor = context.Actor.ToString(),
        });
        args.Add("fromDate", effectiveFrom, DbType.Date);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO uw.rule_set_version (rule_set_code, version_no, product_code, checkpoint, content_hash, status, effective_from, data_status, definition, created_at, created_by)
            VALUES (@code, @version, @product, @checkpoint, @hash, 'Draft', @fromDate, @dataStatus, @definition::jsonb, @now, @actor) ON CONFLICT DO NOTHING
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows == 1;
    }

    /// <summary>Replaces the definition of a Draft version. Returns false when the version is not (or no longer) a Draft.</summary>
    public async Task<bool> UpdateDraftAsync(
        string code, string version, int expectedRecordVersion, string hash, DateOnly effectiveFrom, string definition, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new { code, version, hash, definition, expected = expectedRecordVersion });
        args.Add("fromDate", effectiveFrom, DbType.Date);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE uw.rule_set_version SET content_hash = @hash, effective_from = @fromDate, definition = @definition::jsonb, record_version = record_version + 1
             WHERE rule_set_code = @code AND version_no = @version AND status = 'Draft' AND record_version = @expected
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows == 1;
    }

    /// <summary>Serialises releases of one product and checkpoint (the clash check of schedule) until the transaction ends.</summary>
    public async Task LockProductAsync(string productCode, string checkpoint, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtextextended('uw.ruleset:' || @product || ':' || @checkpoint, 0))",
            new { product = productCode, checkpoint }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Draft to Submitted with the approval request; the content hash is the compiled-table hash of the frozen definition.</summary>
    public async Task<bool> MarkSubmittedAsync(string code, string version, string hash, Guid approvalRequestId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE uw.rule_set_version SET status = 'Submitted', content_hash = @hash, approval_request_id = @approvalRequestId, submitted_by = @actor, submitted_at = @now,
                   decided_by = NULL, decided_at = NULL, decision_reason = NULL, record_version = record_version + 1
             WHERE rule_set_code = @code AND version_no = @version AND status = 'Draft'
            """, new { code, version, hash, approvalRequestId, actor = context.Actor.ToString(), now = clock.Now.ToUtcDateTime() },
            session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows == 1;
    }

    /// <summary>The checker's decision on a Submitted version: Approved, or back to Draft when rejected.</summary>
    public async Task<bool> MarkDecidedAsync(string code, string version, string newStatus, string? reason, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE uw.rule_set_version SET status = @newStatus, decided_by = @actor, decided_at = @now, decision_reason = @reason, record_version = record_version + 1
             WHERE rule_set_code = @code AND version_no = @version AND status = 'Submitted'
            """, new { code, version, newStatus, reason, actor = context.Actor.ToString(), now = clock.Now.ToUtcDateTime() },
            session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows == 1;
    }

    /// <summary>Approved to Active from a date, with the definition and hash of that date. An Active version is never updated.</summary>
    public async Task<bool> ActivateAsync(string code, string version, DateOnly effectiveFrom, string hash, string definition, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new { code, version, hash, definition });
        args.Add("fromDate", effectiveFrom, DbType.Date);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE uw.rule_set_version SET status = 'Active', content_hash = @hash, effective_from = @fromDate, definition = @definition::jsonb, record_version = record_version + 1
             WHERE rule_set_code = @code AND version_no = @version AND status = 'Approved'
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows == 1;
    }

    /// <summary>The Active version of the product and checkpoint that already takes effect on exactly the date (the order would be ambiguous), if any.</summary>
    public async Task<string?> ActiveOnAsync(string productCode, string checkpoint, DateOnly date, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new { product = productCode, checkpoint });
        args.Add("onDate", date, DbType.Date);
        return await connection.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            "SELECT rule_set_code || ' ' || version_no FROM uw.rule_set_version WHERE product_code = @product AND checkpoint = @checkpoint AND status = 'Active' AND effective_from = @onDate LIMIT 1",
            args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>The newest evaluations of the product and checkpoint that stored their facts (POL's in-process evaluations).</summary>
    public async Task<IReadOnlyList<StoredEvaluationRecord>> RecentEvaluationsAsync(
        Guid legalEntity, string productCode, string checkpoint, int limit, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return [.. await connection.QueryAsync<StoredEvaluationRecord>(new CommandDefinition(
            """
            SELECT e.evaluation_id AS EvaluationId, e.outcome AS Outcome, e.facts::text AS Facts, e.trace::text AS Trace
              FROM uw.evaluation e
              JOIN uw.rule_set_version v ON v.rule_set_code = e.rule_set_code AND v.version_no = e.rule_set_version
             WHERE e.legal_entity_id = @legalEntity AND e.facts IS NOT NULL AND e.checkpoint = @checkpoint AND v.product_code = @productCode
             ORDER BY e.created_at DESC, e.evaluation_id DESC LIMIT @limit
            """, new { legalEntity, checkpoint, productCode, limit }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)];
    }
}
