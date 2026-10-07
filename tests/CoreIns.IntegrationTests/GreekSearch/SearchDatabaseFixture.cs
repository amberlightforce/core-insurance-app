using Npgsql;
using Testcontainers.PostgreSql;

namespace CoreIns.IntegrationTests.GreekSearch;

/// <summary>
/// A PostgreSQL 17 database with <c>infra/database/greek-search.sql</c> applied (twice, to prove idempotence).
/// Uses a Testcontainers PostgreSQL 17 by default; when the environment variable <c>COREINS_TEST_PG</c> holds a
/// superuser connection string, that server is used instead (for example an embedded PostgreSQL 17 where Docker is not
/// available), with a scratch database that is dropped afterwards.
/// </summary>
public sealed class SearchDatabaseFixture : IAsyncLifetime
{
    public const string ExternalServerVariable = "COREINS_TEST_PG";

    // One scratch database per fixture instance: several test classes use this fixture in parallel.
    private readonly string _scratchDatabase = "coreins_greek_search_test_" + Guid.NewGuid().ToString("N")[..12];

    private PostgreSqlContainer? _container;
    private string? _adminConnectionString;

    /// <summary>Connection string of the database with the search objects.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable(ExternalServerVariable);
        if (string.IsNullOrWhiteSpace(external))
        {
            _container = new PostgreSqlBuilder(PostgresFixture.Image).Build();
            await _container.StartAsync().ConfigureAwait(false);
            ConnectionString = _container.GetConnectionString();
        }
        else
        {
            _adminConnectionString = external;
            await ExecuteAsync(external, $"DROP DATABASE IF EXISTS {_scratchDatabase}").ConfigureAwait(false);
            await ExecuteAsync(external, $"CREATE DATABASE {_scratchDatabase} ENCODING 'UTF8' TEMPLATE template0").ConfigureAwait(false);
            ConnectionString = new NpgsqlConnectionStringBuilder(external) { Database = _scratchDatabase }.ConnectionString;
        }

        var script = await File.ReadAllTextAsync(Path.Combine(RepositoryPaths.DatabaseScripts, "greek-search.sql")).ConfigureAwait(false);
        await ExecuteAsync(ConnectionString, script).ConfigureAwait(false);
        await ExecuteAsync(ConnectionString, script).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }

        if (_adminConnectionString is not null)
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(_adminConnectionString, $"DROP DATABASE IF EXISTS {_scratchDatabase} WITH (FORCE)").ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
