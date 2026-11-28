using CoreIns.Modules.Underwriting.Domain;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using Dapper;

namespace CoreIns.Modules.Underwriting.Queries;

/// <summary>A <c>uw.policy_hold</c> row as read (BO-UW-09).</summary>
internal sealed class PolicyHoldRecord
{
    public Guid HoldId { get; set; }

    public Guid LegalEntityId { get; set; }

    public string HoldCode { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public DateTime EffectiveFrom { get; set; }

    public DateTime? EffectiveTo { get; set; }

    public string[] JobTypes { get; set; } = [];

    public Guid? CopiedFrom { get; set; }

    public DateTime CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime? ActivatedAt { get; set; }

    public string? ActivatedBy { get; set; }

    public DateTime? ReleasedAt { get; set; }

    public string? ReleasedBy { get; set; }

    public string? ReleaseReason { get; set; }

    public int RecordVersion { get; set; }

    public DateOnly From => DateOnly.FromDateTime(EffectiveFrom);

    public DateOnly? To => EffectiveTo is { } to ? DateOnly.FromDateTime(to) : null;
}

/// <summary>A hold with its scope values.</summary>
internal sealed record HoldWithScopes(PolicyHoldRecord Hold, IReadOnlyList<(string Dimension, string Value)> Scopes)
{
    public HoldDefinition Definition => new(Hold.HoldId, Hold.HoldCode, Hold.Description, Hold.From, Hold.To, Hold.JobTypes, Scopes);
}

/// <summary>Dapper reads and writes of <c>uw.policy_hold</c> and <c>uw.hold_scope</c>; every statement is filtered by the legal entity.</summary>
internal sealed class PolicyHoldStore(DbSession session, IClock clock)
{
    private const string Columns =
        """
        hold_id AS HoldId, legal_entity_id AS LegalEntityId, hold_code AS HoldCode, description AS Description, status AS Status,
        effective_from AS EffectiveFrom, effective_to AS EffectiveTo, job_types AS JobTypes, copied_from AS CopiedFrom,
        created_at AS CreatedAt, created_by AS CreatedBy, activated_at AS ActivatedAt, activated_by AS ActivatedBy,
        released_at AS ReleasedAt, released_by AS ReleasedBy, release_reason AS ReleaseReason, record_version AS RecordVersion
        """;

    public DateTime Now => clock.Now.ToUtcDateTime();

    public async Task<HoldWithScopes?> GetAsync(Guid legalEntity, Guid id, bool forUpdate, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var hold = await connection.QueryFirstOrDefaultAsync<PolicyHoldRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM uw.policy_hold WHERE legal_entity_id = @legalEntity AND hold_id = @id {(forUpdate ? "FOR UPDATE" : string.Empty)}",
            new { legalEntity, id }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (hold is null)
        {
            return null;
        }

        var scopes = await ScopesAsync([id], cancellationToken).ConfigureAwait(false);
        return new HoldWithScopes(hold, scopes.TryGetValue(id, out var list) ? list : []);
    }

    public async Task<bool> CodeExistsAsync(Guid legalEntity, string code, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM uw.policy_hold WHERE legal_entity_id = @legalEntity AND hold_code = @code)",
            new { legalEntity, code }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>The Active holds of the legal entity whose window covers the date, with scopes.</summary>
    public async Task<IReadOnlyList<HoldWithScopes>> ActiveAsync(Guid legalEntity, DateOnly effective, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var holds = (await connection.QueryAsync<PolicyHoldRecord>(new CommandDefinition(
            $"""
            SELECT {Columns} FROM uw.policy_hold
             WHERE legal_entity_id = @legalEntity AND status = 'Active' AND effective_from <= @effective AND (effective_to IS NULL OR effective_to >= @effective)
             ORDER BY hold_code
            """,
            new { legalEntity, effective = effective.ToDateTime(TimeOnly.MinValue) }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var scopes = await ScopesAsync([.. holds.Select(h => h.HoldId)], cancellationToken).ConfigureAwait(false);
        return [.. holds.Select(h => new HoldWithScopes(h, scopes.TryGetValue(h.HoldId, out var s) ? s : []))];
    }

    public async Task InsertAsync(PolicyHoldRecord row, IReadOnlyList<(string Dimension, string Value)> scopes, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO uw.policy_hold (hold_id, legal_entity_id, hold_code, description, status, effective_from, effective_to, job_types, copied_from, created_at, created_by, record_version)
            VALUES (@HoldId, @LegalEntityId, @HoldCode, @Description, @Status, @EffectiveFrom, @EffectiveTo, @JobTypes, @CopiedFrom, @CreatedAt, @CreatedBy, 1)
            """,
            row, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await InsertScopesAsync(row.HoldId, scopes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Changes a Draft hold; a null argument leaves the field alone, a scope list replaces the stored one.</summary>
    public async Task UpdateAsync(
        Guid id, string? description, DateOnly? from, DateOnly? to, bool setTo, string[]? jobTypes, IReadOnlyList<(string Dimension, string Value)>? scopes, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE uw.policy_hold
               SET description = COALESCE(@description::text, description), effective_from = COALESCE(@from::date, effective_from),
                   effective_to = CASE WHEN @setTo::boolean THEN @to::date ELSE effective_to END, job_types = COALESCE(@jobTypes::text[], job_types), record_version = record_version + 1
             WHERE hold_id = @id
            """,
            new { id, description, from = from?.ToDateTime(TimeOnly.MinValue), to = to?.ToDateTime(TimeOnly.MinValue), setTo, jobTypes }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (scopes is not null)
        {
            await connection.ExecuteAsync(new CommandDefinition("UPDATE uw.hold_scope SET retired_at = @now WHERE hold_id = @id AND retired_at IS NULL", new { id, now = Now }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await InsertScopesAsync(id, scopes, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ActivateAsync(Guid id, string by, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE uw.policy_hold SET status = 'Active', activated_at = @now, activated_by = @by, record_version = record_version + 1 WHERE hold_id = @id",
            new { id, by, now = Now }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(Guid id, string by, string reason, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE uw.policy_hold SET status = 'Released', released_at = @now, released_by = @by, release_reason = @reason, record_version = record_version + 1 WHERE hold_id = @id",
            new { id, by, reason, now = Now }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task InsertScopesAsync(Guid holdId, IReadOnlyList<(string Dimension, string Value)> scopes, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO uw.hold_scope (scope_id, hold_id, dimension, value) VALUES (@ScopeId, @holdId, @Dimension, @Value) ON CONFLICT DO NOTHING",
            scopes.Select(s => new { ScopeId = Guid.CreateVersion7(), holdId, s.Dimension, s.Value }).ToList(), session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task<Dictionary<Guid, IReadOnlyList<(string Dimension, string Value)>>> ScopesAsync(Guid[] holdIds, CancellationToken cancellationToken)
    {
        if (holdIds.Length == 0)
        {
            return [];
        }

        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ScopeRecord>(new CommandDefinition(
            "SELECT hold_id AS HoldId, dimension AS Dimension, value AS Value FROM uw.hold_scope WHERE hold_id = ANY(@holdIds) AND retired_at IS NULL ORDER BY dimension, value",
            new { holdIds }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.GroupBy(r => r.HoldId).ToDictionary(g => g.Key, g => (IReadOnlyList<(string Dimension, string Value)>)[.. g.Select(r => (r.Dimension, r.Value))]);
    }

    private sealed class ScopeRecord
    {
        public Guid HoldId { get; set; }

        public string Dimension { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;
    }
}
