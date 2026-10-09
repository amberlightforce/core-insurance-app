using CoreIns.Host.Database;
using CoreIns.Host.Hosting;
using CoreIns.Modules.Policy.Queries;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CoreIns.IntegrationTests.Policy.Temporal;

/// <summary>
/// D-SL3-03 (a) on data that already exists: the slice-3 migration applied to a database holding a policy written before it.
/// A snapshot reference issued before the migration carries knownAt = the clock at issue, which is later than the policy's last
/// record; the backfilled watermark must not sit below it, or every stored reference would be refused as forged after deploy.
/// </summary>
public sealed class WatermarkBackfillTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    /// <summary>The last Policy migration before the slice-3 schema.</summary>
    private const string PreviousMigration = "20261007193657_ReviewFixes";

    [Fact]
    public async Task REQ_POL_007_D2_the_backfilled_watermark_is_not_below_the_migration_time_so_references_issued_before_it_stay_readable()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = "coreins_m" + Guid.NewGuid().ToString("N")[..12];
        await DatabaseBootstrapper.BootstrapAsync(database.AdminConnectionString, name, PostgresFixture.AppPassword, PostgresFixture.MigratorPassword, ct);
        try
        {
            string Connection(string user, string password) =>
                new NpgsqlConnectionStringBuilder(database.AdminConnectionString) { Database = name, Username = user, Password = password }.ConnectionString;
            var migrator = Connection("migrator", PostgresFixture.MigratorPassword);
            var superuser = new NpgsqlConnectionStringBuilder(database.AdminConnectionString) { Database = name }.ConnectionString;

            // Everything migrated, then Policy rolled back to the schema before slice 3: the world as it is at deploy time.
            await DatabaseMigrator.MigrateAsync(migrator, DatabaseMigrator.DefaultAppRole, NullLogger.Instance, ct);
            await using var context = ModuleCatalog.Databases.Single(d => d.Module == ModuleCode.POL).CreateForMigration(migrator);
            await context.GetService<IMigrator>().MigrateAsync(PreviousMigration, ct);

            // A policy last recorded long ago (no watermark column yet), and the database clock as the migration will see it.
            var recordedAt = DateTime.SpecifyKind(new DateTime(2026, 1, 15, 10, 0, 0), DateTimeKind.Utc);
            var policy = Guid.CreateVersion7();
            await using var admin = NpgsqlDataSource.Create(superuser);
            await using (var insert = admin.CreateCommand($"""
                INSERT INTO pol.policy (policy_id, legal_entity_id, jurisdiction, policy_number, product_code, policyholder_party_id, recorded_at, created_by, record_version)
                VALUES ('{policy}', '{ApiHostFactory.LegalEntityId}', 'GR', 'POLTEST-{policy:N}', 'P', '{Guid.CreateVersion7()}', '2026-01-15T10:00:00Z', 'test', 1)
                """))
            {
                await insert.ExecuteNonQueryAsync(ct);
            }

            await using var clockCommand = admin.CreateCommand("SELECT clock_timestamp()");
            var beforeMigration = (DateTime)(await clockCommand.ExecuteScalarAsync(ct))!;

            await context.Database.MigrateAsync(ct);

            await using var read = admin.CreateCommand($"SELECT last_recorded_at FROM pol.policy WHERE policy_id = '{policy}'");
            var watermark = DateTime.SpecifyKind((DateTime)(await read.ExecuteScalarAsync(ct))!, DateTimeKind.Utc);

            watermark.ShouldBeGreaterThanOrEqualTo(DateTime.SpecifyKind(beforeMigration, DateTimeKind.Utc));
            watermark.ShouldBeGreaterThan(recordedAt);

            // A reference issued before the migration, at a time after the last record: still read at exactly that time, not clamped
            // and not refused as lying beyond the watermark.
            var issuedAt = Instant.FromUtcDateTime(DateTime.SpecifyKind(beforeMigration, DateTimeKind.Utc));
            PolicyReader.EffectiveKnownAt(issuedAt, Instant.FromUtcDateTime(watermark)).ShouldBe(issuedAt);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var maintenance = NpgsqlDataSource.Create(database.AdminConnectionString);
            await using var drop = maintenance.CreateCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
