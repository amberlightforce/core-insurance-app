using System.Globalization;
using Npgsql;

namespace CoreIns.Platform.DataProtection.Keys;

/// <summary>
/// Refuses key retirement while PostgreSQL transactions older than the write-transaction bound are open
/// (D-ARC-23a (1)): such a transaction may have picked a data key before its demotion and still commit rows sealed with
/// it after a retirement re-scan. Reads <c>pg_stat_activity.xact_start</c> of the current database.
/// </summary>
/// <remarks>
/// The role must see other sessions' activity (member of <c>pg_read_all_stats</c>, or the same role as the writers);
/// otherwise other sessions' <c>xact_start</c> is hidden and the guard cannot see them. Run it against the primary.
/// </remarks>
public sealed class PostgresLongTransactionGuard(NpgsqlDataSource dataSource)
{
    /// <summary>The bound of D-ARC-23a: MaxStaleForWrite + 2 × RetirementMargin.</summary>
    public static TimeSpan Bound(KeyRingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.MaxStaleForWrite + options.RetirementMargin + options.RetirementMargin;
    }

    /// <summary>Number of open transactions in the current database (other than this session) older than <paramref name="bound"/>.</summary>
    public async ValueTask<long> CountOlderThanAsync(TimeSpan bound, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*)
            FROM pg_stat_activity
            WHERE datname = current_database()
              AND pid <> pg_backend_pid()
              AND xact_start IS NOT NULL
              AND xact_start < clock_timestamp() - make_interval(secs => $1)
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = (decimal)bound.Ticks / TimeSpan.TicksPerSecond });
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> while any transaction older than <paramref name="bound"/> is open.</summary>
    public async ValueTask EnsureNoneOlderThanAsync(TimeSpan bound, CancellationToken cancellationToken = default)
    {
        var count = await CountOlderThanAsync(bound, cancellationToken).ConfigureAwait(false);
        if (count > 0)
        {
            throw new InvalidOperationException(
                $"{count} database transactions older than {bound} are open; key retirement is refused until they end (D-ARC-23a).");
        }
    }
}

/// <summary>
/// An <see cref="IRetirementScan"/> that honours the D-ARC-23a contract: it first refuses while transactions older than
/// <see cref="PostgresLongTransactionGuard.Bound"/> are open, then delegates the row count to the owning module's scan.
/// </summary>
public sealed class GuardedRetirementScan(IRetirementScan inner, PostgresLongTransactionGuard guard, KeyRingOptions options) : IRetirementScan
{
    public async ValueTask<long> CountRowsUsingAsync(LegalEntityId legalEntity, KeyPurpose purpose, int version, CancellationToken cancellationToken = default)
    {
        await guard.EnsureNoneOlderThanAsync(PostgresLongTransactionGuard.Bound(options), cancellationToken).ConfigureAwait(false);
        return await inner.CountRowsUsingAsync(legalEntity, purpose, version, cancellationToken).ConfigureAwait(false);
    }
}
