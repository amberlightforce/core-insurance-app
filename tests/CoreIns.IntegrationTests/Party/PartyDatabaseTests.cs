using CoreIns.Host.Database;
using CoreIns.Host.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CoreIns.IntegrationTests.Party;

/// <summary>The migrate job applies every module's EF Core migrations idempotently and grants least privilege.</summary>
public sealed class PartyDatabaseTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task The_migrate_job_creates_the_pty_tables_idempotently_with_no_delete_for_the_app_role()
    {
        ModuleCatalog.Databases.Select(d => d.Schema).ShouldBe(["plt", "pty"]);

        // A second run (every deployment runs the migrate job) changes nothing and does not fail.
        await DatabaseMigrator.MigrateAsync(database.MigratorConnectionString, DatabaseMigrator.DefaultAppRole, NullLogger.Instance, TestContext.Current.CancellationToken);

        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        var ct = TestContext.Current.CancellationToken;

        async Task<bool> Can(string table, string privilege)
        {
            await using var command = dataSource.CreateCommand($"SELECT has_table_privilege('app', '{table}', '{privilege}')");
            return (bool)(await command.ExecuteScalarAsync(ct))!;
        }

        foreach (var table in new[] { "party", "party_name", "party_search_key", "party_identifier", "party_address", "party_contact_point", "intermediary", "producer_code" })
        {
            (await Can($"pty.{table}", "INSERT")).ShouldBeTrue(table);
            (await Can($"pty.{table}", "SELECT")).ShouldBeTrue(table);
            (await Can($"pty.{table}", "DELETE")).ShouldBeFalse(table);
            (await Can($"pty.{table}", "TRUNCATE")).ShouldBeFalse(table);
        }

        (await Can("pty.__ef_migrations_history", "SELECT")).ShouldBeFalse();
        (await Can("plt.number_series", "UPDATE")).ShouldBeTrue();
        (await Can("plt.data_key", "DELETE")).ShouldBeFalse();

        await using var migrations = dataSource.CreateCommand("SELECT to_regprocedure('public.coreins_search_key(text)') IS NOT NULL");
        ((bool)(await migrations.ExecuteScalarAsync(ct))!).ShouldBeTrue();
    }
}
