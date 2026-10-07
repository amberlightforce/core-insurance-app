using CoreIns.Host.Database;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CoreIns.IntegrationTests;

/// <summary>
/// A PostgreSQL 17 container initialised exactly like the local stack: <c>infra/local/pg-init</c> creates the
/// database and runs the shared <c>infra/database/bootstrap.sql</c> (roles <c>app</c> and <c>migrator</c>, privileges,
/// the five extensions); then the schema is migrated as the migrate job does.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Same image as infra/local/compose.yaml.</summary>
    public const string Image = "postgres:17.11";

    public const string AppPassword = "app-integration-test";
    public const string MigratorPassword = "migrator-integration-test";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .WithResourceMapping(new DirectoryInfo(RepositoryPaths.PgInit), "/docker-entrypoint-initdb.d/")
        .WithResourceMapping(new DirectoryInfo(RepositoryPaths.DatabaseScripts), "/coreins-db/")
        .WithEnvironment("APP_DB_PASSWORD", AppPassword)
        .WithEnvironment("MIGRATOR_DB_PASSWORD", MigratorPassword)
        .WithCommand("-c", "shared_preload_libraries=pg_stat_statements", "-c", "timezone=UTC")
        .Build();

    /// <summary>Connection string for the application role (api / worker).</summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    /// <summary>Connection string for the migration role.</summary>
    public string MigratorConnectionString { get; private set; } = string.Empty;

    /// <summary>Superuser connection to the maintenance database (plays the Azure server administrator).</summary>
    public string AdminConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);

        AppConnectionString = ForRole("app", AppPassword);
        MigratorConnectionString = ForRole("migrator", MigratorPassword);

        await DatabaseMigrator.MigrateAsync(MigratorConnectionString, DatabaseMigrator.DefaultAppRole, NullLogger.Instance, CancellationToken.None)
            .ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    private string ForRole(string username, string password) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = DatabaseBootstrapper.DefaultDatabaseName,
            Username = username,
            Password = password,
        }.ConnectionString;
}
