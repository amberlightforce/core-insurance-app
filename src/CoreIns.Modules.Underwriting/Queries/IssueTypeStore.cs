using System.Globalization;
using System.Text;
using CoreIns.Modules.Underwriting.Commands;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Results;
using Dapper;

namespace CoreIns.Modules.Underwriting.Queries;

/// <summary>An issue-type row as read.</summary>
internal sealed class IssueTypeRecord
{
    public string Code { get; set; } = string.Empty;

    public string NameEl { get; set; } = string.Empty;

    public string NameEn { get; set; } = string.Empty;

    public string Severity { get; set; } = string.Empty;

    public string DefaultLane { get; set; } = string.Empty;

    public string BlockingPoint { get; set; } = string.Empty;

    public bool Active { get; set; }

    public int RecordVersion { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary>Dapper reads and writes of the issue-type catalogue <c>uw.issue_type</c> (BO-UW-02).</summary>
internal sealed class IssueTypeStore(DbSession session, IClock clock, RequestContext context)
{
    private const string Columns =
        "code AS Code, name_el AS NameEl, name_en AS NameEn, severity AS Severity, default_lane AS DefaultLane, blocking_point AS BlockingPoint, "
        + "active AS Active, record_version AS RecordVersion, updated_at AS UpdatedAt, updated_by AS UpdatedBy";

    public async Task<IssueTypeRecord?> GetAsync(string code, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<IssueTypeRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM uw.issue_type WHERE code = @code", new { code }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IssueTypeRecord>> SearchAsync(string? text, bool activeOnly, string? after, int limit, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var pattern = text is null ? null : "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        return [.. await connection.QueryAsync<IssueTypeRecord>(new CommandDefinition(
            $"""
            SELECT {Columns} FROM uw.issue_type
             WHERE (@after::text IS NULL OR code > @after)
               AND (NOT @activeOnly OR active)
               AND (@pattern::text IS NULL OR code ILIKE @pattern OR name_en ILIKE @pattern OR name_el ILIKE @pattern)
             ORDER BY code LIMIT @limit
            """, new { after, activeOnly, pattern, limit }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)];
    }

    /// <summary>Creates the issue type, or changes it when <paramref name="expectedRecordVersion"/> (null on create) is the stored version. Returns the new record version, or null when the version did not match.</summary>
    public async Task<int?> UpsertAsync(IssueTypeRecord row, int? expectedRecordVersion, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new { row.Code, row.NameEl, row.NameEn, row.Severity, row.DefaultLane, row.BlockingPoint, row.Active, now = clock.Now.ToUtcDateTime(), actor = context.Actor.ToString(), expected = expectedRecordVersion };
        if (expectedRecordVersion is null)
        {
            var inserted = await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO uw.issue_type (code, name_el, name_en, severity, default_lane, blocking_point, active, record_version, updated_at, updated_by)
                VALUES (@Code, @NameEl, @NameEn, @Severity, @DefaultLane, @BlockingPoint, @Active, 1, @now, @actor) ON CONFLICT DO NOTHING
                """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return inserted == 1 ? 1 : null;
        }

        return await connection.QueryFirstOrDefaultAsync<int?>(new CommandDefinition(
            """
            UPDATE uw.issue_type SET name_el = @NameEl, name_en = @NameEn, severity = @Severity, default_lane = @DefaultLane, blocking_point = @BlockingPoint,
                   active = @Active, record_version = record_version + 1, updated_at = @now, updated_by = @actor
             WHERE code = @Code AND record_version = @expected RETURNING record_version
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}

/// <summary><c>uw.IssueType.search</c> and <c>get</c>: the catalogue reads.</summary>
internal sealed class IssueTypeQueries(IssueTypeStore store)
{
    public static IssueTypeItem Item(IssueTypeRecord row) => new()
    {
        Code = row.Code,
        NameEl = row.NameEl,
        NameEn = row.NameEn,
        Severity = IssueTypeCodes.Severity(row.Severity),
        DefaultLane = IssueTypeCodes.Lane(row.DefaultLane),
        BlockingPoint = IssueTypeCodes.Blocking(row.BlockingPoint),
        Active = row.Active,
        RecordVersion = row.RecordVersion,
        UpdatedAt = DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
        UpdatedBy = row.UpdatedBy,
    };

    public async Task<Result<IssueTypeSearchPage>> SearchAsync(string? q, bool? activeOnly, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        string? after = null;
        if (limit is < 1 or > 100 || (cursor is not null && !TryDecode(cursor, out after)))
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", "cursor is malformed or limit is outside 1..100.");
        }

        var size = limit ?? 50;
        var rows = await store.SearchAsync(string.IsNullOrWhiteSpace(q) ? null : q.Trim(), activeOnly ?? false, after, size + 1, cancellationToken).ConfigureAwait(false);
        var page = rows.Take(size).ToList();
        return new IssueTypeSearchPage
        {
            Items = [.. page.Select(Item)],
            NextCursor = rows.Count > size ? Convert.ToBase64String(Encoding.UTF8.GetBytes(page[^1].Code)) : null,
            Limit = size,
        };
    }

    public async Task<Result<IssueTypeItem>> GetAsync(string id, CancellationToken cancellationToken)
    {
        var row = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return row is null ? DomainError.Of(ModuleCode.UW, "NOT-FOUND", $"Issue type {id} does not exist.") : Item(row);
    }

    private static bool TryDecode(string cursor, out string? code)
    {
        try
        {
            code = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return code.Length is > 0 and <= 64;
        }
        catch (FormatException)
        {
            code = null;
            return false;
        }
    }
}
