using System.Globalization;
using Npgsql;

namespace CoreIns.Platform.DataProtection.Keys;

/// <summary>
/// Refuses key retirement while PostgreSQL transactions older than the write-transaction bound are open
/// (D-ARC-23a (1)): such a transaction may have picked a data key before its demotion and still commit rows sealed with
/// it after a retirement re-scan. Reads <c>pg_stat_activity.xact_start</c> of the current database.
/// </summary>
/// <remarks>
/// Fails closed: PostgreSQL hides other roles' <c>xact_start</c> from roles that are neither superuser nor members of
/// <c>pg_read_all_stats</c>, which would make the count silently 0, so the guard first checks that privilege and throws
/// <see cref="LongTransactionGuardPrivilegeException"/> without it. Run it against the primary.
/// </remarks>
public sealed class PostgresLongTransactionGuard(NpgsqlDataSource dataSource)
{
    /// <summary>The predefined role whose membership lets a role see every session's activity.</summary>
    public const string RequiredRole = "pg_read_all_stats";

    /// <summary>The bound of D-ARC-23a: MaxStaleForWrite + 2 × RetirementMargin.</summary>
    public static TimeSpan Bound(KeyRingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.MaxStaleForWrite + options.RetirementMargin + options.RetirementMargin;
    }

    /// <summary>
    /// Open transactions in the current database (other than this session) older than <paramref name="bound"/>.
    /// No query text is read.
    /// </summary>
    /// <exception cref="LongTransactionGuardPrivilegeException">The role cannot see other sessions' transactions.</exception>
    public async ValueTask<IReadOnlyList<LongTransaction>> ListOlderThanAsync(TimeSpan bound, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (var privilege = new NpgsqlCommand(
            $"SELECT coalesce((SELECT rolsuper FROM pg_roles WHERE rolname = current_user), false) OR pg_has_role(current_user, '{RequiredRole}', 'member'), current_user",
            connection))
        await using (var reader = await privilege.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!reader.GetBoolean(0))
            {
                throw new LongTransactionGuardPrivilegeException(
                    $"Role '{reader.GetString(1)}' is neither superuser nor a member of {RequiredRole}; it cannot see other sessions' "
                    + $"transactions, so key retirement is refused (D-ARC-23a). GRANT {RequiredRole} TO the retirement job's role.");
            }
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT pid, coalesce(usename, ''), coalesce(application_name, ''), clock_timestamp() - xact_start
            FROM pg_stat_activity
            WHERE datname = current_database()
              AND pid <> pg_backend_pid()
              AND xact_start IS NOT NULL
              AND xact_start < clock_timestamp() - make_interval(secs => $1)
            ORDER BY xact_start
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter { Value = (decimal)bound.Ticks / TimeSpan.TicksPerSecond });
        var sessions = new List<LongTransaction>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sessions.Add(new LongTransaction(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetTimeSpan(3)));
            }
        }

        return sessions;
    }

    /// <summary>Number of open transactions older than <paramref name="bound"/> (see <see cref="ListOlderThanAsync"/>).</summary>
    public async ValueTask<long> CountOlderThanAsync(TimeSpan bound, CancellationToken cancellationToken = default) =>
        (await ListOlderThanAsync(bound, cancellationToken).ConfigureAwait(false)).Count;

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> naming the blocking sessions (pid, user, application, age) while any
    /// transaction older than <paramref name="bound"/> is open.
    /// </summary>
    public async ValueTask EnsureNoneOlderThanAsync(TimeSpan bound, CancellationToken cancellationToken = default)
    {
        var sessions = await ListOlderThanAsync(bound, cancellationToken).ConfigureAwait(false);
        if (sessions.Count > 0)
        {
            var list = string.Join("; ", sessions.Select(session => string.Create(
                CultureInfo.InvariantCulture,
                $"pid {session.Pid} user '{session.UserName}' application '{session.ApplicationName}' open for {session.Age:c}")));
            throw new InvalidOperationException(
                $"{sessions.Count} database transactions older than {bound} are open; key retirement is refused until they end (D-ARC-23a): {list}.");
        }
    }
}

/// <summary>An open transaction found by the guard (no query text, by design).</summary>
public sealed record LongTransaction(int Pid, string UserName, string ApplicationName, TimeSpan Age);

/// <summary>The guard's role lacks the privilege to see other sessions (pg_read_all_stats or superuser).</summary>
public sealed class LongTransactionGuardPrivilegeException : InvalidOperationException
{
    public LongTransactionGuardPrivilegeException()
    {
    }

    public LongTransactionGuardPrivilegeException(string message)
        : base(message)
    {
    }

    public LongTransactionGuardPrivilegeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// An <see cref="IRetirementScan"/> that honours the D-ARC-23a contract: it first refuses while transactions older than
/// <see cref="PostgresLongTransactionGuard.Bound"/> are open (or the guard cannot see them), then delegates the row count
/// to the owning module's scan.
/// </summary>
public sealed class GuardedRetirementScan(IRetirementScan inner, PostgresLongTransactionGuard guard, KeyRingOptions options) : IRetirementScan
{
    public async ValueTask<long> CountRowsUsingAsync(LegalEntityId legalEntity, KeyPurpose purpose, int version, CancellationToken cancellationToken = default)
    {
        await guard.EnsureNoneOlderThanAsync(PostgresLongTransactionGuard.Bound(options), cancellationToken).ConfigureAwait(false);
        return await inner.CountRowsUsingAsync(legalEntity, purpose, version, cancellationToken).ConfigureAwait(false);
    }
}
