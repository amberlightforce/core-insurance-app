using CoreIns.Platform.DataProtection;
using CoreIns.Platform.DataProtection.Keys;
using Npgsql;

namespace CoreIns.IntegrationTests.GreekSearch;

/// <summary>
/// D-ARC-23a (1): key retirement is refused while a PostgreSQL transaction older than the write-transaction bound is
/// open (<c>pg_stat_activity.xact_start</c>). Runs against the same database as the Greek search tests (Testcontainers,
/// or the server named by <c>COREINS_TEST_PG</c>).
/// </summary>
public sealed class LongTransactionGuardTests(SearchDatabaseFixture database) : IClassFixture<SearchDatabaseFixture>
{
    private static readonly LegalEntityId Entity = new(Guid.Parse("0192d4a1-0000-7000-8000-00000000000a"));

    [Fact]
    public async Task Retirement_scan_refuses_while_an_old_transaction_is_open()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var guard = new PostgresLongTransactionGuard(dataSource);
        var options = new KeyRingOptions { MaxStaleForWrite = TimeSpan.FromMilliseconds(300), RetirementMargin = TimeSpan.FromMilliseconds(100) };
        PostgresLongTransactionGuard.Bound(options).ShouldBe(TimeSpan.FromMilliseconds(500));
        var scan = new GuardedRetirementScan(new ZeroRows(), guard, options);

        (await scan.CountRowsUsingAsync(Entity, KeyPurpose.FieldEncryption, 1, ct)).ShouldBe(0);

        // A writer opens a transaction and keeps it open past the bound.
        var writerConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { ApplicationName = "f1e-long-writer" }.ConnectionString;
        await using var writer = new NpgsqlConnection(writerConnection);
        await writer.OpenAsync(ct);
        await using var transaction = await writer.BeginTransactionAsync(ct);
        await using (var touch = new NpgsqlCommand("SELECT 1", writer, transaction))
        {
            await touch.ExecuteScalarAsync(ct);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(900), ct);
        (await guard.CountOlderThanAsync(PostgresLongTransactionGuard.Bound(options), ct)).ShouldBeGreaterThanOrEqualTo(1);
        var refusal = (await Should.ThrowAsync<InvalidOperationException>(async () =>
            await scan.CountRowsUsingAsync(Entity, KeyPurpose.FieldEncryption, 1, ct))).Message;
        refusal.ShouldContain("D-ARC-23a");
        refusal.ShouldContain($"pid {writer.ProcessID}");
        refusal.ShouldContain("application 'f1e-long-writer'");
        refusal.ShouldNotContain("SELECT 1"); // never the query text

        // Once it ends, retirement may proceed.
        await transaction.CommitAsync(ct);
        (await scan.CountRowsUsingAsync(Entity, KeyPurpose.FieldEncryption, 1, ct)).ShouldBe(0);
    }

    /// <summary>A role that cannot see other sessions' transactions is refused (fail closed), and accepted once granted.</summary>
    [Fact]
    public async Task Guard_refuses_a_role_without_pg_read_all_stats()
    {
        var ct = TestContext.Current.CancellationToken;
        var role = "f1e_guard_" + Guid.NewGuid().ToString("N")[..8];
        const string Password = "guard-test-only";
        await using var admin = NpgsqlDataSource.Create(database.ConnectionString);
        await using (var create = admin.CreateCommand($"CREATE ROLE {role} LOGIN PASSWORD '{Password}'"))
        {
            await create.ExecuteNonQueryAsync(ct);
        }

        try
        {
            var asRole = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = role, Password = Password, Pooling = false }.ConnectionString;
            await using (var limited = NpgsqlDataSource.Create(asRole))
            {
                var error = await Should.ThrowAsync<LongTransactionGuardPrivilegeException>(async () =>
                    await new PostgresLongTransactionGuard(limited).CountOlderThanAsync(TimeSpan.Zero, ct));
                error.Message.ShouldContain("pg_read_all_stats");
                error.Message.ShouldContain(role);
            }

            await using (var grant = admin.CreateCommand($"GRANT pg_read_all_stats TO {role}"))
            {
                await grant.ExecuteNonQueryAsync(ct);
            }

            await using (var granted = NpgsqlDataSource.Create(asRole))
            {
                (await new PostgresLongTransactionGuard(granted).CountOlderThanAsync(TimeSpan.FromHours(1), ct)).ShouldBe(0);
            }
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP ROLE IF EXISTS {role}");
            await drop.ExecuteNonQueryAsync(ct);
        }
    }

    private sealed class ZeroRows : IRetirementScan
    {
        public ValueTask<long> CountRowsUsingAsync(LegalEntityId legalEntity, KeyPurpose purpose, int version, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0L);
    }
}
