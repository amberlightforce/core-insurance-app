using System.Data;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using Dapper;

namespace CoreIns.Modules.Underwriting.Queries;

/// <summary>A <c>uw.contingency</c> row as read (BO-UW-08).</summary>
internal sealed class ContingencyRecord
{
    public Guid ContingencyId { get; set; }

    public Guid LegalEntityId { get; set; }

    public Guid TermId { get; set; }

    public Guid? JobId { get; set; }

    public string ContingencyType { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string? CustomerText { get; set; }

    public DateTime DueDate { get; set; }

    public string Owner { get; set; } = string.Empty;

    public string BlockingPoint { get; set; } = string.Empty;

    public string ConsequenceCode { get; set; } = string.Empty;

    public string? ConsequenceIssueType { get; set; }

    public string Status { get; set; } = string.Empty;

    public DateTime? OverdueAt { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public string? ResolvedBy { get; set; }

    public string? ResolutionNote { get; set; }

    public string? EvidenceRef { get; set; }

    public DateTime CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public int RecordVersion { get; set; }

    public DateOnly Due => DateOnly.FromDateTime(DueDate);
}

/// <summary>Filters of the contingency list.</summary>
internal sealed record ContingencyFilter(Guid? TermId, Guid? JobId, string? Status, bool? Overdue, string? Owner, string? Type);

/// <summary>Dapper reads and writes of <c>uw.contingency</c>; every statement is filtered by the legal entity.</summary>
internal sealed class ContingencyStore(DbSession session, IClock clock)
{
    private const string Columns =
        """
        contingency_id AS ContingencyId, legal_entity_id AS LegalEntityId, term_id AS TermId, job_id AS JobId, contingency_type AS ContingencyType,
        description AS Description, customer_text AS CustomerText, due_date AS DueDate, owner AS Owner, blocking_point AS BlockingPoint,
        consequence_code AS ConsequenceCode, consequence_issue_type AS ConsequenceIssueType, status AS Status, overdue_at AS OverdueAt,
        resolved_at AS ResolvedAt, resolved_by AS ResolvedBy, resolution_note AS ResolutionNote, evidence_ref AS EvidenceRef,
        created_at AS CreatedAt, created_by AS CreatedBy, record_version AS RecordVersion
        """;

    public DateTime Now => clock.Now.ToUtcDateTime();

    public DateOnly Today => DateOnly.FromDateTime(Now);

    public async Task<ContingencyRecord?> GetAsync(Guid legalEntity, Guid id, bool forUpdate, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<ContingencyRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM uw.contingency WHERE legal_entity_id = @legalEntity AND contingency_id = @id {(forUpdate ? "FOR UPDATE" : string.Empty)}",
            new { legalEntity, id }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task InsertAsync(ContingencyRecord row, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new
        {
            row.ContingencyId, row.LegalEntityId, row.TermId, row.ContingencyType, row.Description, row.CustomerText, dueDate = row.Due, row.Owner, row.BlockingPoint,
            row.ConsequenceCode, row.ConsequenceIssueType, row.CreatedBy, now = row.CreatedAt,
        });
        args.Add("jobId", row.JobId, DbType.Guid);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO uw.contingency (contingency_id, legal_entity_id, term_id, job_id, contingency_type, description, customer_text, due_date, owner, blocking_point,
                                        consequence_code, consequence_issue_type, status, created_at, created_by, record_version)
            VALUES (@ContingencyId, @LegalEntityId, @TermId, @jobId, @ContingencyType, @Description, @CustomerText, @dueDate, @Owner, @BlockingPoint,
                    @ConsequenceCode, @ConsequenceIssueType, 'Open', @now, @CreatedBy, 1)
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task UpdateTextAsync(Guid id, string? description, string? customerText, string? owner, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE uw.contingency SET description = COALESCE(@description, description), customer_text = COALESCE(@customerText, customer_text),
                                      owner = COALESCE(@owner, owner), record_version = record_version + 1
             WHERE contingency_id = @id
            """, new { id, description, customerText, owner }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Closes an Open contingency as Satisfied, Waived or ConsequenceApplied.</summary>
    public async Task ResolveAsync(Guid id, string status, string by, string? note, string? evidenceRef, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE uw.contingency SET status = @status, resolved_at = @now, resolved_by = @by, resolution_note = @note, evidence_ref = @evidenceRef,
                                      record_version = record_version + 1
             WHERE contingency_id = @id
            """, new { id, status, by, note, evidenceRef, now = Now }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Moves the due date later and clears the overdue mark, so the contingency can go overdue again.</summary>
    public async Task ExtendAsync(Guid id, DateOnly newDueDate, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE uw.contingency SET due_date = @newDueDate, overdue_at = NULL, record_version = record_version + 1 WHERE contingency_id = @id",
            new { id, newDueDate }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Open contingencies past their due date that were not marked yet, locked so a concurrent sweep skips them.</summary>
    public async Task<IReadOnlyList<ContingencyRecord>> DueAsync(Guid legalEntity, DateOnly today, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<ContingencyRecord>(new CommandDefinition(
            $"""
            SELECT {Columns} FROM uw.contingency
             WHERE legal_entity_id = @legalEntity AND status = 'Open' AND overdue_at IS NULL AND due_date < @today
             ORDER BY due_date, contingency_id LIMIT 500 FOR UPDATE SKIP LOCKED
            """, new { legalEntity, today }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
    }

    public async Task MarkOverdueAsync(Guid id, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE uw.contingency SET overdue_at = @now, record_version = record_version + 1 WHERE contingency_id = @id",
            new { id, now = Now }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Open contingencies of the job that block at or before the point (an Open one blocks even when overdue).</summary>
    public async Task<IReadOnlyList<ContingencyRecord>> BlockingAsync(Guid legalEntity, Guid jobId, string blockingPoint, CancellationToken cancellationToken)
    {
        var rank = blockingPoint switch { "PRE_QUOTE" => 1, "PRE_BIND" => 2, "PRE_ISSUE" => 3, _ => 0 };
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<ContingencyRecord>(new CommandDefinition(
            $"""
            SELECT {Columns} FROM uw.contingency
             WHERE legal_entity_id = @legalEntity AND job_id = @jobId AND status = 'Open'
               AND (CASE blocking_point WHEN 'PRE_QUOTE' THEN 1 WHEN 'PRE_BIND' THEN 2 WHEN 'PRE_ISSUE' THEN 3 ELSE 99 END) <= @rank
             ORDER BY due_date, contingency_id
            """, new { legalEntity, jobId, rank }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
    }

    public async Task<IReadOnlyList<ContingencyRecord>> ListAsync(
        Guid legalEntity, ContingencyFilter filter, (DateTime CreatedAt, Guid Id)? after, int limit, DateOnly today, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new { legalEntity, status = filter.Status, owner = filter.Owner, type = filter.Type, limit, today });
        args.Add("termId", filter.TermId, DbType.Guid);
        args.Add("jobId", filter.JobId, DbType.Guid);
        args.Add("overdue", filter.Overdue, DbType.Boolean);
        args.Add("afterAt", after?.CreatedAt, DbType.DateTime);
        args.Add("afterId", after?.Id, DbType.Guid);
        return (await connection.QueryAsync<ContingencyRecord>(new CommandDefinition(
            $"""
            SELECT {Columns} FROM uw.contingency
             WHERE legal_entity_id = @legalEntity
               AND (@termId::uuid IS NULL OR term_id = @termId::uuid)
               AND (@jobId::uuid IS NULL OR job_id = @jobId::uuid)
               AND (@status::text IS NULL OR status = @status::text)
               AND (@owner::text IS NULL OR owner = @owner::text)
               AND (@type::text IS NULL OR contingency_type = @type::text)
               AND (@overdue::boolean IS NULL OR ((status = 'Open' AND due_date < @today::date) = @overdue::boolean))
               AND (@afterAt::timestamptz IS NULL OR (created_at, contingency_id) < (@afterAt::timestamptz, @afterId::uuid))
             ORDER BY created_at DESC, contingency_id DESC LIMIT @limit
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
    }
}
