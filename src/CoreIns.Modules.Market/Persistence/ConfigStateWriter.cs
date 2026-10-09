using CoreIns.Modules.Market.Domain;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

namespace CoreIns.Modules.Market.Persistence;

/// <summary>
/// The one way a configuration state is appended (D-SL5-06): inside the caller's transaction, under the state advisory lock, on top of
/// the newest state. Genesis uses it, and so does SL5-MKT-ROLLBACK for PACK_ACTIVATION and PACK_ROLLBACK states. The database trigger
/// <c>tr_config_state_chain</c> refuses anything that does not follow these steps (PITFALLS 40).
/// </summary>
internal static class ConfigStateWriter
{
    /// <summary>Takes the state advisory lock until the transaction ends. Run it before reading the newest state.</summary>
    public static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(MarketStateSql.TakeLock, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The hash of the newest state, or null when there is none. Read it after <see cref="LockAsync"/>.</summary>
    public static async Task<ConfigurationHash?> NewestAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT hash FROM mkt.config_state ORDER BY seq DESC LIMIT 1", connection, transaction);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string hash ? ConfigurationHash.Parse(hash.Trim()) : null;
    }

    /// <summary>
    /// Appends the state described by <paramref name="manifest"/> and returns its hash. The manifest's parent must be the newest state
    /// (null for the first one); <paramref name="causeRef"/> is the activation that caused it.
    /// </summary>
    public static async Task<ConfigurationHash> AppendAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ConfigStateManifest manifest, Instant activatedAt, Guid? causeRef,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var hash = manifest.Hash;
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO mkt.config_state (hash, parent_hash, manifest, activated_at, cause, cause_ref)
            VALUES (@hash, @parent, @manifest, @at, @cause, @causeRef)
            """, connection, transaction);
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Char, hash.ToString());
        command.Parameters.AddWithValue("parent", NpgsqlDbType.Char, (object?)manifest.ParentHash?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("manifest", NpgsqlDbType.Jsonb, manifest.ToJson().ToJsonString());
        command.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz, activatedAt.ToUtcDateTime());
        command.Parameters.AddWithValue("cause", NpgsqlDbType.Varchar, manifest.Cause);
        command.Parameters.AddWithValue("causeRef", NpgsqlDbType.Uuid, (object?)causeRef ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return hash;
    }
}
