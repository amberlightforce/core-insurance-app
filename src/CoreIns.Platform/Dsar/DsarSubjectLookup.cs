using CoreIns.Platform.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace CoreIns.Platform.Dsar;

/// <summary>
/// Read-only subject lookups for modules that hold no party id and reach a person through POL (RAT, UW). Parameters only;
/// no subject value is interpolated into SQL. Runs on the caller's <see cref="DbSession"/> connection.
/// </summary>
public static class DsarSubjectLookup
{
    /// <summary>Job ids and quote ids of the jobs whose policyholder is the party.</summary>
    public static async Task<(Guid[] JobIds, Guid[] QuoteIds)> JobsAndQuotesAsync(DbSession session, Guid partyId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var jobs = await GuidsAsync(session, "SELECT job_id FROM pol.job WHERE policyholder_party_id = @p", partyId, ct).ConfigureAwait(false);
        var quotes = await GuidsAsync(
            session, "SELECT q.quote_id FROM pol.quote_version q JOIN pol.job j ON j.job_id = q.job_id WHERE j.policyholder_party_id = @p", partyId, ct).ConfigureAwait(false);
        return (jobs, quotes);
    }

    /// <summary>Runs a query returning rows as one jsonb array text; named uuid-array parameters are passed as given.</summary>
    public static async Task<(string Json, int Count)> JsonRowsAsync(
        DbSession session, string sql, CancellationToken ct, params (string Name, Guid[] Values)[] arrays)
    {
        ArgumentNullException.ThrowIfNull(session);
        var connection = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb)::text, count(*) FROM ({sql}) t", connection, session.Transaction);
        foreach (var (name, values) in arrays)
        {
            command.Parameters.AddWithValue(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid, values);
        }

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return (reader.GetString(0), (int)reader.GetInt64(1));
    }

    private static async Task<Guid[]> GuidsAsync(DbSession session, string sql, Guid partyId, CancellationToken ct)
    {
        var connection = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection, session.Transaction);
        command.Parameters.AddWithValue("p", NpgsqlDbType.Uuid, partyId);
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return [.. ids];
    }
}
