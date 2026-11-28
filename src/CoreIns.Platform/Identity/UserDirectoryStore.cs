using CoreIns.Platform.Contracts.Api;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Npgsql;

namespace CoreIns.Platform.Identity;

/// <summary>A <c>plt.user_profile</c> row (BO-PLT-05): the directory entry of an Entra identity. No credentials (D-ARC-03).</summary>
internal sealed record UserRow
{
    public required Guid UserId { get; init; }
    public required string LegalEntity { get; init; }
    public required string EntraOid { get; init; }
    public required string DisplayName { get; init; }
    public string? Email { get; init; }
    public required string Language { get; init; }
    public required string Status { get; init; }
    public required string CreatedBy { get; init; }
    public required Instant CreatedAt { get; init; }
    public required Instant UpdatedAt { get; init; }
    public Instant? LastSeenAt { get; init; }

    public UserView ToView(IReadOnlyList<RoleAssignmentView>? assignments = null) => new()
    {
        UserId = global::CoreIns.SharedKernel.Identifiers.UserId.From(UserId),
        EntraOid = EntraOid,
        DisplayName = DisplayName,
        Email = Email,
        Language = Language == "en" ? UserLanguage.En : UserLanguage.El,
        Status = Enum.Parse<UserStatus>(Status),
        LegalEntity = LegalEntity,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        LastSeenAt = LastSeenAt,
        RoleAssignments = assignments,
    };
}

/// <summary>A <c>plt.role_assignment</c> row.</summary>
internal sealed record RoleAssignmentRow
{
    public required Guid AssignmentId { get; init; }
    public required Guid UserId { get; init; }
    public required string LegalEntity { get; init; }
    public required string Role { get; init; }
    public string? Scope { get; init; }
    public Instant? ValidFrom { get; init; }
    public Instant? ValidTo { get; init; }
    public required string Status { get; init; }
    public required string GrantedBy { get; init; }
    public string? ApprovedBy { get; init; }
    public Guid? ApprovalRequestId { get; init; }
    public required Instant GrantedAt { get; init; }
    public Instant? RevokedAt { get; init; }
    public string? RevokedBy { get; init; }

    public RoleAssignmentView ToView() => new()
    {
        AssignmentId = AssignmentId,
        UserId = global::CoreIns.SharedKernel.Identifiers.UserId.From(UserId),
        Role = Role,
        Scope = Scope,
        ValidFrom = ValidFrom,
        ValidTo = ValidTo,
        Status = Status == "Revoked" ? RoleAssignmentStatus.Revoked : RoleAssignmentStatus.Active,
        GrantedBy = GrantedBy,
        ApprovedBy = ApprovedBy,
        ApprovalRequestId = ApprovalRequestId is { } id ? global::CoreIns.SharedKernel.Identifiers.ApprovalRequestId.From(id) : null,
        GrantedAt = GrantedAt,
        RevokedAt = RevokedAt,
        RevokedBy = RevokedBy,
    };
}

/// <summary>SQL of the user directory. Every statement is scoped to a legal entity.</summary>
internal static class UserDirectoryStore
{
    public const string Active = "Active";
    public const string Suspended = "Suspended";
    public const string Disabled = "Disabled";

    private const string UserColumns =
        "user_id, legal_entity, entra_oid, display_name, email, language, status, created_by, created_at, updated_at, last_seen_at";

    private const string AssignmentColumns =
        "assignment_id, user_id, legal_entity, role, scope, valid_from, valid_to, status, granted_by, approved_by, approval_request_id, granted_at, revoked_at, revoked_by";

    public static string LanguageCode(UserLanguage language) => language == UserLanguage.En ? "en" : "el";

    public static async Task<UserRow?> FindAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string legalEntity, Guid userId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT {UserColumns} FROM plt.user_profile WHERE legal_entity = @le AND user_id = @id", connection, transaction);
        command.Parameters.AddWithValue("le", legalEntity);
        command.Parameters.AddWithValue("id", userId);
        return await ReadOneAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<UserRow?> FindByOidAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string legalEntity, string entraOid, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT {UserColumns} FROM plt.user_profile WHERE legal_entity = @le AND entra_oid = @oid", connection, transaction);
        command.Parameters.AddWithValue("le", legalEntity);
        command.Parameters.AddWithValue("oid", entraOid);
        return await ReadOneAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Inserts the entry; false when the Entra identity already has one in the legal entity.</summary>
    public static async Task<bool> TryInsertAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, UserRow row, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO plt.user_profile (user_id, legal_entity, entra_oid, display_name, email, language, status, created_by, created_at, updated_at, last_seen_at)
            VALUES (@id, @le, @oid, @name, @email, @lang, @status, @by, @now, @now, @seen)
            ON CONFLICT (legal_entity, entra_oid) DO NOTHING
            """,
            connection, transaction);
        command.Parameters.AddWithValue("id", row.UserId);
        command.Parameters.AddWithValue("le", row.LegalEntity);
        command.Parameters.AddWithValue("oid", row.EntraOid);
        command.Parameters.AddWithValue("name", row.DisplayName);
        command.Parameters.AddWithValue("email", (object?)row.Email ?? DBNull.Value);
        command.Parameters.AddWithValue("lang", row.Language);
        command.Parameters.AddWithValue("status", row.Status);
        command.Parameters.AddWithValue("by", row.CreatedBy);
        command.Parameters.AddWithValue("now", row.CreatedAt.ToUtcDateTime());
        command.Parameters.AddWithValue("seen", (object?)row.LastSeenAt?.ToUtcDateTime() ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public static async Task UpdateProfileAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, string displayName, string? email, string language, Instant now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE plt.user_profile SET display_name = @name, email = @email, language = @lang, updated_at = @now WHERE user_id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("name", displayName);
        command.Parameters.AddWithValue("email", (object?)email ?? DBNull.Value);
        command.Parameters.AddWithValue("lang", language);
        command.Parameters.AddWithValue("now", now.ToUtcDateTime());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Conditional status change: true when the row still had <paramref name="from"/> as its status.</summary>
    public static async Task<bool> TrySetStatusAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, string[] from, string to, string? reason, Instant now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE plt.user_profile SET status = @to, status_reason = @reason, updated_at = @now WHERE user_id = @id AND status = ANY(@from)", connection, transaction);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("now", now.ToUtcDateTime());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public static async Task TouchAsync(NpgsqlConnection connection, Guid userId, Instant now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("UPDATE plt.user_profile SET last_seen_at = @now WHERE user_id = @id AND (last_seen_at IS NULL OR last_seen_at < @old)", connection);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("now", now.ToUtcDateTime());
        command.Parameters.AddWithValue("old", now.ToUtcDateTime().AddTicks(-5 * TimeSpan.TicksPerMinute));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<UserRow>> SearchAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string legalEntity, string? text, string? status, int offset, int take, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT {UserColumns} FROM plt.user_profile
            WHERE legal_entity = @le
              AND (@status::text IS NULL OR status = @status)
              AND (@q::text IS NULL OR display_name ILIKE @q ESCAPE '\' OR email ILIKE @q ESCAPE '\' OR entra_oid ILIKE @q ESCAPE '\')
            ORDER BY lower(display_name), user_id
            OFFSET @offset LIMIT @take
            """,
            connection, transaction);
        command.Parameters.AddWithValue("le", legalEntity);
        command.Parameters.AddWithValue("status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("q", string.IsNullOrWhiteSpace(text) ? DBNull.Value : "%" + EscapeLike(text.Trim()) + "%");
        command.Parameters.AddWithValue("offset", offset);
        command.Parameters.AddWithValue("take", take);
        var rows = new List<UserRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadUser(reader));
        }

        return rows;
    }

    public static async Task<IReadOnlyList<RoleAssignmentRow>> AssignmentsOfAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT {AssignmentColumns} FROM plt.role_assignment WHERE user_id = @id ORDER BY granted_at, assignment_id", connection, transaction);
        command.Parameters.AddWithValue("id", userId);
        var rows = new List<RoleAssignmentRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadAssignment(reader));
        }

        return rows;
    }

    public static async Task<RoleAssignmentRow?> FindAssignmentAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string legalEntity, Guid assignmentId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT {AssignmentColumns} FROM plt.role_assignment WHERE legal_entity = @le AND assignment_id = @id", connection, transaction);
        command.Parameters.AddWithValue("le", legalEntity);
        command.Parameters.AddWithValue("id", assignmentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAssignment(reader) : null;
    }

    /// <summary>Inserts an active assignment; false when the user already holds the same role and scope.</summary>
    public static async Task<bool> TryInsertAssignmentAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, RoleAssignmentRow row, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO plt.role_assignment (assignment_id, user_id, legal_entity, role, scope, valid_from, valid_to, status, granted_by, approved_by, approval_request_id, granted_at)
            VALUES (@id, @user, @le, @role, @scope, @from, @to, 'Active', @by, @approver, @approval, @now)
            ON CONFLICT DO NOTHING
            """,
            connection, transaction);
        command.Parameters.AddWithValue("id", row.AssignmentId);
        command.Parameters.AddWithValue("user", row.UserId);
        command.Parameters.AddWithValue("le", row.LegalEntity);
        command.Parameters.AddWithValue("role", row.Role);
        command.Parameters.AddWithValue("scope", (object?)row.Scope ?? DBNull.Value);
        command.Parameters.AddWithValue("from", (object?)row.ValidFrom?.ToUtcDateTime() ?? DBNull.Value);
        command.Parameters.AddWithValue("to", (object?)row.ValidTo?.ToUtcDateTime() ?? DBNull.Value);
        command.Parameters.AddWithValue("by", row.GrantedBy);
        command.Parameters.AddWithValue("approver", (object?)row.ApprovedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("approval", (object?)row.ApprovalRequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("now", row.GrantedAt.ToUtcDateTime());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public static async Task<bool> TryRevokeAssignmentAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid assignmentId, string revokedBy, string reason, Instant now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE plt.role_assignment SET status = 'Revoked', revoked_at = @now, revoked_by = @by, revoke_reason = @reason WHERE assignment_id = @id AND status = 'Active'", connection, transaction);
        command.Parameters.AddWithValue("id", assignmentId);
        command.Parameters.AddWithValue("by", revokedBy);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("now", now.ToUtcDateTime());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static string EscapeLike(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static async Task<UserRow?> ReadOneAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadUser(reader) : null;
    }

    private static UserRow ReadUser(NpgsqlDataReader reader) => new()
    {
        UserId = reader.GetGuid(0),
        LegalEntity = reader.GetString(1),
        EntraOid = reader.GetString(2),
        DisplayName = reader.GetString(3),
        Email = reader.IsDBNull(4) ? null : reader.GetString(4),
        Language = reader.GetString(5),
        Status = reader.GetString(6),
        CreatedBy = reader.GetString(7),
        CreatedAt = Instant.FromUtcDateTime(reader.GetFieldValue<DateTime>(8)),
        UpdatedAt = Instant.FromUtcDateTime(reader.GetFieldValue<DateTime>(9)),
        LastSeenAt = reader.IsDBNull(10) ? null : Instant.FromUtcDateTime(reader.GetFieldValue<DateTime>(10)),
    };

    private static RoleAssignmentRow ReadAssignment(NpgsqlDataReader reader) => new()
    {
        AssignmentId = reader.GetGuid(0),
        UserId = reader.GetGuid(1),
        LegalEntity = reader.GetString(2),
        Role = reader.GetString(3),
        Scope = reader.IsDBNull(4) ? null : reader.GetString(4),
        ValidFrom = reader.IsDBNull(5) ? null : Instant.FromUtcDateTime(reader.GetFieldValue<DateTime>(5)),
        ValidTo = reader.IsDBNull(6) ? null : Instant.FromUtcDateTime(reader.GetFieldValue<DateTime>(6)),
        Status = reader.GetString(7),
        GrantedBy = reader.GetString(8),
        ApprovedBy = reader.IsDBNull(9) ? null : reader.GetString(9),
        ApprovalRequestId = reader.IsDBNull(10) ? null : reader.GetGuid(10),
        GrantedAt = Instant.FromUtcDateTime(reader.GetFieldValue<DateTime>(11)),
        RevokedAt = reader.IsDBNull(12) ? null : Instant.FromUtcDateTime(reader.GetFieldValue<DateTime>(12)),
        RevokedBy = reader.IsDBNull(13) ? null : reader.GetString(13),
    };
}
