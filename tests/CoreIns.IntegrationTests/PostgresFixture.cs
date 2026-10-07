using CoreIns.Host.Database;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CoreIns.IntegrationTests;

/// <summary>
/// A real PostgreSQL 17 database initialised like the local stack and migrated as the migrate job does. Two modes:
/// <list type="bullet">
/// <item><b>Testcontainers</b> (default, CI): a <c>postgres:17</c> container initialised by <c>infra/local/pg-init</c>.</item>
/// <item><b>External server</b>: when <c>COREINS_TEST_POSTGRES</c> holds a superuser connection string (e.g. an
/// embedded PostgreSQL 17 where Docker is unavailable), each fixture creates its own database on that server with the
/// Host's bootstrap (roles <c>app</c>/<c>migrator</c>, extensions), migrates it, and drops it afterwards.</item>
/// </list>
/// The same tests run in both modes.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Same image as infra/local/compose.yaml.</summary>
    public const string Image = "postgres:17.11";

    /// <summary>Environment variable with a superuser connection string to an external PostgreSQL 17.</summary>
    public const string ExternalServerVariable = "COREINS_TEST_POSTGRES";

    public const string AppPassword = "app-integration-test";
    public const string MigratorPassword = "migrator-integration-test";

    private static readonly SemaphoreSlim BootstrapGate = new(1, 1);

    private readonly string? _external = Environment.GetEnvironmentVariable(ExternalServerVariable) is { Length: > 0 } value ? value : null;
    private PostgreSqlContainer? _container;

    /// <summary>True when running against an external server instead of a container.</summary>
    public bool IsExternal => _external is not null;

    /// <summary>The application database name.</summary>
    public string DatabaseName { get; private set; } = DatabaseBootstrapper.DefaultDatabaseName;

    /// <summary>Connection string for the application role (api / worker).</summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    /// <summary>Connection string for the migration role.</summary>
    public string MigratorConnectionString { get; private set; } = string.Empty;

    /// <summary>Superuser connection to the maintenance database (plays the Azure server administrator).</summary>
    public string AdminConnectionString { get; private set; } = string.Empty;

    /// <summary>Superuser connection to the application database (tests that must bypass privileges, e.g. tampering).</summary>
    public string SuperuserConnectionString => new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = DatabaseName }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        if (_external is not null)
        {
            DatabaseName = "coreins_t" + Guid.NewGuid().ToString("N")[..12];
            AdminConnectionString = new NpgsqlConnectionStringBuilder(_external) { Database = "postgres" }.ConnectionString;

            // Roles are cluster-wide: bootstrap one database at a time.
            await BootstrapGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await DatabaseBootstrapper.BootstrapAsync(AdminConnectionString, DatabaseName, AppPassword, MigratorPassword, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                BootstrapGate.Release();
            }
        }
        else
        {
            _container = new PostgreSqlBuilder(Image)
                .WithResourceMapping(new DirectoryInfo(RepositoryPaths.PgInit), "/docker-entrypoint-initdb.d/")
                .WithResourceMapping(new DirectoryInfo(RepositoryPaths.DatabaseScripts), "/coreins-db/")
                .WithEnvironment("APP_DB_PASSWORD", AppPassword)
                .WithEnvironment("MIGRATOR_DB_PASSWORD", MigratorPassword)
                .WithCommand("-c", "shared_preload_libraries=pg_stat_statements", "-c", "timezone=UTC", "-c", "max_connections=300")
                .Build();
            await _container.StartAsync().ConfigureAwait(false);
            AdminConnectionString = _container.GetConnectionString();
        }

        AppConnectionString = ForRole("app", AppPassword);
        MigratorConnectionString = ForRole("migrator", MigratorPassword);

        await DatabaseMigrator.MigrateAsync(MigratorConnectionString, DatabaseMigrator.DefaultAppRole, NullLogger.Instance, CancellationToken.None)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using var admin = NpgsqlDataSource.Create(AdminConnectionString);
        await using var drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {DatabaseName} WITH (FORCE)");
        await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Runs SQL as the superuser in the application database.</summary>
    public async Task ExecuteAsSuperuserAsync(string sql, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs SQL as the migrator role (DDL for test-only module schemas).</summary>
    public async Task ExecuteAsMigratorAsync(string sql, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(MigratorConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private string ForRole(string username, string password) =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Database = DatabaseName,
            Username = username,
            Password = password,
        }.ConnectionString;
}
