using Npgsql;
using NpgsqlTypes;

namespace CoreIns.Platform.DataProtection.Keys;

/// <summary>
/// The durable key ring: <c>plt.data_key</c> (created by the platform's EF Core migration). Versions are unique per
/// (legal entity, purpose, version), so two replicas creating the same version cannot both win
/// (<see cref="DataKeyConflictException"/>). Always read from the primary (D-ARC-23a rule 3).
/// </summary>
public sealed class PostgresDataKeyStore(NpgsqlDataSource dataSource) : IDataKeyStore
{
    private const string UniqueViolation = "23505";

    public async ValueTask<IReadOnlyList<WrappedDataKey>> ListAsync(LegalEntityId legalEntity, KeyPurpose purpose, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT version, kek_id, wrapped_key, status, created_at, demoted_at, retiring_at
              FROM plt.data_key WHERE legal_entity_id = @le AND purpose = @purpose ORDER BY version
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid>("le", legalEntity.Value));
        command.Parameters.Add(new NpgsqlParameter<int>("purpose", (int)purpose));
        var keys = new List<WrappedDataKey>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            keys.Add(new WrappedDataKey(
                legalEntity,
                purpose,
                reader.GetInt32(0),
                reader.GetString(1),
                (byte[])reader.GetValue(2),
                (DataKeyStatus)reader.GetInt32(3),
                Utc(reader.GetDateTime(4)),
                reader.IsDBNull(5) ? null : Utc(reader.GetDateTime(5)),
                reader.IsDBNull(6) ? null : Utc(reader.GetDateTime(6))));
        }

        return keys;
    }

    public async ValueTask AddAsync(WrappedDataKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO plt.data_key (legal_entity_id, purpose, version, kek_id, wrapped_key, status, created_at, demoted_at, retiring_at)
            VALUES (@le, @purpose, @version, @kek, @wrapped, @status, @created, @demoted, @retiring)
            """);
        Bind(command, key);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
        {
            throw new DataKeyConflictException($"Data key version {key.Version} already exists.", ex);
        }
    }

    public async ValueTask UpdateAsync(WrappedDataKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var command = dataSource.CreateCommand(
            """
            UPDATE plt.data_key
               SET kek_id = @kek, wrapped_key = @wrapped, status = @status, created_at = @created, demoted_at = @demoted, retiring_at = @retiring
             WHERE legal_entity_id = @le AND purpose = @purpose AND version = @version
            """);
        Bind(command, key);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new KeyNotFoundException($"Data key version {key.Version} does not exist.");
        }
    }

    private static void Bind(NpgsqlCommand command, WrappedDataKey key)
    {
        command.Parameters.Add(new NpgsqlParameter<Guid>("le", key.LegalEntity.Value));
        command.Parameters.Add(new NpgsqlParameter<int>("purpose", (int)key.Purpose));
        command.Parameters.Add(new NpgsqlParameter<int>("version", key.Version));
        command.Parameters.Add(new NpgsqlParameter<string>("kek", key.KeyEncryptionKeyId));
        command.Parameters.Add(new NpgsqlParameter<byte[]>("wrapped", key.WrappedKey.ToArray()));
        command.Parameters.Add(new NpgsqlParameter<int>("status", (int)key.Status));
        command.Parameters.Add(new NpgsqlParameter("created", NpgsqlDbType.TimestampTz) { Value = key.CreatedAt.UtcDateTime });
        command.Parameters.Add(new NpgsqlParameter("demoted", NpgsqlDbType.TimestampTz) { Value = (object?)key.DemotedAt?.UtcDateTime ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("retiring", NpgsqlDbType.TimestampTz) { Value = (object?)key.RetiringAt?.UtcDateTime ?? DBNull.Value });
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
