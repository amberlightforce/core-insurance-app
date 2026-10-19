using CoreIns.Platform.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace CoreIns.Modules.Policy.Services.Dsar;

/// <summary>Small SQL helpers for the DSAR participant on the scope's connection. Parameters only; no subject value is interpolated.</summary>
internal static class DsarSql
{
    /// <summary>Runs a query that returns one jsonb array (<c>jsonb_agg</c>) for a party id and returns its text.</summary>
    public static async Task<string> JsonArrayAsync(DbSession session, string sql, Guid partyId, CancellationToken ct)
    {
        var connection = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb)::text FROM ({sql}) t", connection, session.Transaction);
        command.Parameters.AddWithValue("p", NpgsqlDbType.Uuid, partyId);
        return (string)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>Executes a statement with the party id as <c>@p</c> and returns the affected row count.</summary>
    public static async Task<int> ExecuteAsync(DbSession session, string sql, Guid partyId, CancellationToken ct, params (string Name, object Value)[] extra)
    {
        var connection = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection, session.Transaction);
        command.Parameters.AddWithValue("p", NpgsqlDbType.Uuid, partyId);
        foreach (var (name, value) in extra)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Runs a count query with the party id as <c>@p</c>.</summary>
    public static async Task<int> CountAsync(DbSession session, string sql, Guid partyId, CancellationToken ct)
    {
        var connection = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection, session.Transaction);
        command.Parameters.AddWithValue("p", NpgsqlDbType.Uuid, partyId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Records (idempotently) that a record class of the subject is restricted; the first legal basis is kept.</summary>
    public static Task<int> RestrictAsync(DbSession session, Guid partyId, string recordClass, string legalBasis, string retentionEnd, string reason, string actor, CancellationToken ct) =>
        ExecuteAsync(
            session,
            "INSERT INTO pol.dsar_restriction (restriction_id, party_id, record_class, legal_basis, retention_end, reason, created_at, created_by) " +
            "VALUES (gen_random_uuid(), @p, @c, @b, @e, @r, now(), @a) ON CONFLICT (party_id, record_class) DO NOTHING",
            partyId,
            ct,
            ("c", recordClass), ("b", legalBasis), ("e", retentionEnd), ("r", reason), ("a", actor));

    /// <summary>True when any record class of the subject is restricted for the given class (reads feeding marketing/notification check this).</summary>
    public static async Task<bool> IsRestrictedAsync(DbSession session, Guid partyId, string recordClass, CancellationToken ct)
    {
        var connection = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pol.dsar_restriction WHERE party_id = @p AND record_class = @c)", connection, session.Transaction);
        command.Parameters.AddWithValue("p", NpgsqlDbType.Uuid, partyId);
        command.Parameters.AddWithValue("c", recordClass);
        return (bool)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }
}
