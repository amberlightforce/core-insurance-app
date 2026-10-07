using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

namespace CoreIns.Platform.Idempotency;

/// <summary>A stored idempotency record.</summary>
/// <param name="RequestHash">SHA-256 of the canonical request.</param>
/// <param name="Completed">True when the original result is stored.</param>
/// <param name="ResponseStatus">Stored status (HTTP status, or 200 for a command result).</param>
/// <param name="ContentType">Stored content type.</param>
/// <param name="Body">Stored body.</param>
/// <param name="CreatedAt">When the key was first used.</param>
/// <param name="Headers">Stored response headers to replay (Location, ETag, Content-Location, Retry-After), or null.</param>
public sealed record IdempotencyEntry(
    string RequestHash, bool Completed, int? ResponseStatus, string? ContentType, byte[]? Body, Instant CreatedAt,
    IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// <c>plt.idempotency_record</c> (contract §3.5.3, D-API-01): key → original result, kept 7 days. Scopes keep keys of
/// different callers and operations apart (<c>http:&lt;principal&gt;:&lt;method&gt; &lt;path&gt;</c>,
/// <c>cmd:&lt;operation&gt;:&lt;actor&gt;</c>). All methods take the caller's connection and transaction, so a command's
/// record commits atomically with its effects.
/// </summary>
public static class IdempotencyStore
{
    /// <summary>Retention of records (contract §3.5.3: at least 7 days).</summary>
    public static TimeSpan Retention { get; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Inserts an in-progress record. Returns true when this caller owns the key; false when a record exists (a concurrent
    /// insert of the same key waits for the other transaction to finish first).
    /// </summary>
    public static async Task<bool> TryBeginAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string scope, IdempotencyKey key, Sha256Hash requestHash, Instant now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO plt.idempotency_record (scope, idempotency_key, request_hash, status, created_at, expires_at)
            VALUES (@scope, @key, @hash, 'InProgress', @now, @expires)
            ON CONFLICT (scope, idempotency_key) DO NOTHING
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<string>("scope", scope));
        command.Parameters.Add(new NpgsqlParameter<Guid>("key", key.Value));
        command.Parameters.Add(new NpgsqlParameter<string>("hash", requestHash.Value));
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.ToUtcDateTime() });
        command.Parameters.Add(new NpgsqlParameter("expires", NpgsqlDbType.TimestampTz) { Value = now.Plus(Retention).ToUtcDateTime() });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>Reads a record that has not expired; null when none.</summary>
    public static async Task<IdempotencyEntry?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string scope, IdempotencyKey key, Instant now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand(
            """
            SELECT request_hash, status, response_status, response_content_type, response_body, created_at, response_headers::text
            FROM plt.idempotency_record
            WHERE scope = @scope AND idempotency_key = @key AND expires_at > @now
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<string>("scope", scope));
        command.Parameters.Add(new NpgsqlParameter<Guid>("key", key.Value));
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.ToUtcDateTime() });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new IdempotencyEntry(
            reader.GetString(0).Trim(),
            reader.GetString(1) == "Completed",
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<byte[]>(4),
            Instant.FromUtcDateTime(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)),
            reader.IsDBNull(6) ? null : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(6)));
    }

    /// <summary>Stores the original result.</summary>
    public static async Task CompleteAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string scope, IdempotencyKey key, int status, string? contentType, byte[] body,
        Instant now, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand(
            """
            UPDATE plt.idempotency_record
            SET status = 'Completed', response_status = @status, response_content_type = @type, response_body = @body, completed_at = @now,
                response_headers = @headers::jsonb
            WHERE scope = @scope AND idempotency_key = @key
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<int>("status", status));
        command.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Text) { Value = (object?)contentType ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter<byte[]>("body", body));
        command.Parameters.Add(new NpgsqlParameter("headers", NpgsqlDbType.Text)
        {
            Value = headers is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(headers) : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.ToUtcDateTime() });
        command.Parameters.Add(new NpgsqlParameter<string>("scope", scope));
        command.Parameters.Add(new NpgsqlParameter<Guid>("key", key.Value));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a record (the request failed in a retryable way, so the key may be used again).</summary>
    public static async Task ReleaseAsync(
        NpgsqlConnection connection, string scope, IdempotencyKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand(
            "DELETE FROM plt.idempotency_record WHERE scope = @scope AND idempotency_key = @key AND status = 'InProgress'", connection);
        command.Parameters.Add(new NpgsqlParameter<string>("scope", scope));
        command.Parameters.Add(new NpgsqlParameter<Guid>("key", key.Value));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes over a record whose in-progress owner died (older than <paramref name="staleBefore"/>) or that expired.</summary>
    public static async Task<bool> TakeOverStaleAsync(
        NpgsqlConnection connection, string scope, IdempotencyKey key, Sha256Hash requestHash, Instant staleBefore, Instant now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand(
            """
            UPDATE plt.idempotency_record
            SET request_hash = @hash, status = 'InProgress', created_at = @now, expires_at = @expires,
                response_status = NULL, response_content_type = NULL, response_body = NULL, response_headers = NULL, completed_at = NULL
            WHERE scope = @scope AND idempotency_key = @key
              AND ((status = 'InProgress' AND created_at < @stale) OR expires_at <= @now)
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter<string>("hash", requestHash.Value));
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.ToUtcDateTime() });
        command.Parameters.Add(new NpgsqlParameter("expires", NpgsqlDbType.TimestampTz) { Value = now.Plus(Retention).ToUtcDateTime() });
        command.Parameters.Add(new NpgsqlParameter("stale", NpgsqlDbType.TimestampTz) { Value = staleBefore.ToUtcDateTime() });
        command.Parameters.Add(new NpgsqlParameter<string>("scope", scope));
        command.Parameters.Add(new NpgsqlParameter<Guid>("key", key.Value));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>Deletes expired records; returns how many.</summary>
    public static async Task<int> PurgeExpiredAsync(NpgsqlDataSource dataSource, Instant now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("DELETE FROM plt.idempotency_record WHERE expires_at <= @now", connection);
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.ToUtcDateTime() });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
