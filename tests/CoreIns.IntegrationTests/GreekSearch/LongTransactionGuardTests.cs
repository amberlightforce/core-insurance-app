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
        await using var writer = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await writer.BeginTransactionAsync(ct);
        await using (var touch = new NpgsqlCommand("SELECT 1", writer, transaction))
        {
            await touch.ExecuteScalarAsync(ct);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(900), ct);
        (await guard.CountOlderThanAsync(PostgresLongTransactionGuard.Bound(options), ct)).ShouldBeGreaterThanOrEqualTo(1);
        (await Should.ThrowAsync<InvalidOperationException>(async () =>
            await scan.CountRowsUsingAsync(Entity, KeyPurpose.FieldEncryption, 1, ct))).Message.ShouldContain("D-ARC-23a");

        // Once it ends, retirement may proceed.
        await transaction.CommitAsync(ct);
        (await scan.CountRowsUsingAsync(Entity, KeyPurpose.FieldEncryption, 1, ct)).ShouldBe(0);
    }

    private sealed class ZeroRows : IRetirementScan
    {
        public ValueTask<long> CountRowsUsingAsync(LegalEntityId legalEntity, KeyPurpose purpose, int version, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0L);
    }
}
