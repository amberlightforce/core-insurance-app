using System.Data;
using System.Text.Json;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using Dapper;

namespace CoreIns.Modules.Underwriting.Queries;

/// <summary>A <c>uw.decline</c> row as read (BO-UW-07); the JSON columns are read as text.</summary>
internal sealed class DeclineRecord
{
    public Guid DeclineId { get; set; }

    public Guid LegalEntityId { get; set; }

    public string DeclineNumber { get; set; } = string.Empty;

    public Guid? JobId { get; set; }

    public Guid? IntakeId { get; set; }

    public string Scope { get; set; } = string.Empty;

    public string ReasonCodes { get; set; } = "[]";

    public string Pairs { get; set; } = "[]";

    public string? Wording { get; set; }

    public bool Automated { get; set; }

    public string Status { get; set; } = string.Empty;

    public string LetterData { get; set; } = "{}";

    public string LetterStatus { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime? ReviewRequestedAt { get; set; }

    public string? ReviewRequestedBy { get; set; }

    public string? ReviewRequester { get; set; }

    public string? ReviewStatement { get; set; }

    public DateTime? ReviewDecidedAt { get; set; }

    public string? ReviewDecidedBy { get; set; }

    public string? ReviewOutcome { get; set; }

    public string? ReviewReason { get; set; }

    public int RecordVersion { get; set; }
}

/// <summary>The newest evaluation of a job as far as a decline needs it.</summary>
internal sealed record EvaluationOutcome(Guid EvaluationId, string Outcome, string[] MatchedRules);

/// <summary>Filters of the refusal register (REQ-UW-229..231).</summary>
internal sealed record RegisterFilter(DateTime? From, DateTime? To, string? Status, bool? Automated, string? Scope, string? Reason);

/// <summary>Dapper reads and writes of <c>uw.decline</c>; every statement is filtered by the legal entity.</summary>
internal sealed class DeclineStore(DbSession session, IClock clock)
{
    private const string Columns =
        """
        decline_id AS DeclineId, legal_entity_id AS LegalEntityId, decline_number AS DeclineNumber, job_id AS JobId, intake_id AS IntakeId, scope AS Scope,
        reason_codes::text AS ReasonCodes, pairs::text AS Pairs, wording AS Wording, automated AS Automated, status AS Status, letter_data::text AS LetterData,
        letter_status AS LetterStatus, created_at AS CreatedAt, created_by AS CreatedBy, review_requested_at AS ReviewRequestedAt,
        review_requested_by AS ReviewRequestedBy, review_requester AS ReviewRequester, review_statement AS ReviewStatement, review_decided_at AS ReviewDecidedAt,
        review_decided_by AS ReviewDecidedBy, review_outcome AS ReviewOutcome, review_reason AS ReviewReason, record_version AS RecordVersion
        """;

    private const string FilterSql =
        """
        legal_entity_id = @legalEntity
        AND (@from::timestamptz IS NULL OR created_at >= @from::timestamptz)
        AND (@to::timestamptz IS NULL OR created_at < @to::timestamptz)
        AND (@status::text IS NULL OR status = @status::text)
        AND (@automated::boolean IS NULL OR automated = @automated::boolean)
        AND (@scope::text IS NULL OR scope = @scope::text)
        AND (@reason::text IS NULL OR reason_codes @> to_jsonb(@reason::text))
        """;

    public DateTime Now => clock.Now.ToUtcDateTime();

    /// <summary>The newest evaluation of the job with its outcome and matched rule ids; null when the job was never evaluated.</summary>
    public async Task<EvaluationOutcome?> LatestEvaluationAsync(Guid legalEntity, Guid jobId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QueryFirstOrDefaultAsync<EvaluationRowView>(new CommandDefinition(
            """
            SELECT evaluation_id AS EvaluationId, outcome AS Outcome, (trace::jsonb -> 'matchedRules')::text AS Matched
              FROM uw.evaluation WHERE legal_entity_id = @legalEntity AND job_id = @jobId ORDER BY created_at DESC, evaluation_id DESC LIMIT 1
            """,
            new { legalEntity, jobId }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : new EvaluationOutcome(row.EvaluationId, row.Outcome, string.IsNullOrEmpty(row.Matched) ? [] : JsonSerializer.Deserialize<string[]>(row.Matched) ?? []);
    }

    public async Task<DeclineRecord?> GetAsync(Guid legalEntity, Guid declineId, bool forUpdate, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<DeclineRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM uw.decline WHERE legal_entity_id = @legalEntity AND decline_id = @declineId {(forUpdate ? "FOR UPDATE" : string.Empty)}",
            new { legalEntity, declineId }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>The decline of the job or intake that still stands (not Overturned); null when none.</summary>
    public async Task<DeclineRecord?> StandingAsync(Guid legalEntity, Guid? jobId, Guid? intakeId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new { legalEntity });
        args.Add("jobId", jobId, DbType.Guid);
        args.Add("intakeId", intakeId, DbType.Guid);
        return await connection.QueryFirstOrDefaultAsync<DeclineRecord>(new CommandDefinition(
            $"""
            SELECT {Columns} FROM uw.decline
             WHERE legal_entity_id = @legalEntity AND status <> 'Overturned' AND (job_id = @jobId::uuid OR intake_id = @intakeId::uuid) LIMIT 1
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task InsertAsync(DeclineRecord row, Guid? evaluationId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new
        {
            row.DeclineId, row.LegalEntityId, row.DeclineNumber, row.Scope, row.ReasonCodes, row.Pairs, row.Wording, row.Automated, row.LetterData, row.LetterStatus,
            row.CreatedBy, now = row.CreatedAt,
        });
        args.Add("jobId", row.JobId, DbType.Guid);
        args.Add("intakeId", row.IntakeId, DbType.Guid);
        args.Add("evaluationId", evaluationId, DbType.Guid);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO uw.decline (decline_id, legal_entity_id, decline_number, job_id, intake_id, scope, reason_codes, pairs, wording, automated, status, evaluation_id,
                                    letter_data, letter_status, created_at, created_by, record_version)
            VALUES (@DeclineId, @LegalEntityId, @DeclineNumber, @jobId, @intakeId, @Scope, @ReasonCodes::jsonb, @Pairs::jsonb, @Wording, @Automated, 'Issued', @evaluationId,
                    @LetterData::jsonb, @LetterStatus, @now, @CreatedBy, 1)
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task RequestReviewAsync(Guid declineId, string requestedBy, string requester, string statement, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE uw.decline SET status = 'ReviewRequested', review_requested_at = @now, review_requested_by = @requestedBy, review_requester = @requester,
                                  review_statement = @statement, record_version = record_version + 1
             WHERE decline_id = @declineId
            """, new { declineId, requestedBy, requester, statement, now = Now }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task DecideReviewAsync(Guid declineId, string outcome, string decidedBy, string reason, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE uw.decline SET status = @status, review_decided_at = @now, review_decided_by = @decidedBy, review_outcome = @outcome, review_reason = @reason,
                                  record_version = record_version + 1
             WHERE decline_id = @declineId
            """,
            new { declineId, status = outcome == "UPHELD" ? "Upheld" : "Overturned", outcome, decidedBy, reason, now = Now }, session.Transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Register rows, newest first, after the keyset position.</summary>
    public async Task<IReadOnlyList<DeclineRecord>> QueryAsync(
        Guid legalEntity, RegisterFilter filter, (DateTime CreatedAt, Guid DeclineId)? after, int limit, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = FilterArgs(legalEntity, filter);
        args.Add("limit", limit);
        args.Add("afterAt", after?.CreatedAt, DbType.DateTime);
        args.Add("afterId", after?.DeclineId, DbType.Guid);
        return (await connection.QueryAsync<DeclineRecord>(new CommandDefinition(
            $"""
            SELECT {Columns} FROM uw.decline
             WHERE {FilterSql} AND (@afterAt::timestamptz IS NULL OR (created_at, decline_id) < (@afterAt::timestamptz, @afterId::uuid))
             ORDER BY created_at DESC, decline_id DESC LIMIT @limit
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
    }

    private static DynamicParameters FilterArgs(Guid legalEntity, RegisterFilter f)
    {
        var args = new DynamicParameters(new { legalEntity, status = f.Status, scope = f.Scope, reason = f.Reason });
        args.Add("from", f.From, DbType.DateTime);
        args.Add("to", f.To, DbType.DateTime);
        args.Add("automated", f.Automated, DbType.Boolean);
        return args;
    }

    private sealed class EvaluationRowView
    {
        public Guid EvaluationId { get; set; }

        public string Outcome { get; set; } = string.Empty;

        public string? Matched { get; set; }
    }
}
