using System.Net;
using Npgsql;

namespace CoreIns.IntegrationTests;

/// <summary>The Host boots in api mode against a real, migrated PostgreSQL 17.</summary>
public sealed class HostSmokeTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Api_mode_is_ready_when_the_database_is_reachable()
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString);
        using var client = factory.CreateClient();

        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Api_endpoints_require_authentication()
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/plt/v1/anything", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Platform_tables_are_migrated_with_least_privilege_for_the_app_role()
    {
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        var ct = TestContext.Current.CancellationToken;

        async Task<bool> Can(string table, string privilege)
        {
            await using var command = dataSource.CreateCommand($"SELECT has_table_privilege('app', 'plt.{table}', '{privilege}')");
            return (bool)(await command.ExecuteScalarAsync(ct))!;
        }

        (await Can("audit_event", "INSERT")).ShouldBeTrue();
        (await Can("audit_event", "SELECT")).ShouldBeTrue();
        (await Can("audit_event", "UPDATE")).ShouldBeFalse();
        (await Can("audit_event", "DELETE")).ShouldBeFalse();
        (await Can("audit_event", "TRUNCATE")).ShouldBeFalse();
        (await Can("outbox_message", "UPDATE")).ShouldBeTrue();
        (await Can("idempotency_record", "DELETE")).ShouldBeTrue();
        (await Can("__ef_migrations_history", "SELECT")).ShouldBeFalse();

        await using var history = dataSource.CreateCommand("SELECT count(*) FROM pg_tables WHERE schemaname = 'plt'");
        ((long)(await history.ExecuteScalarAsync(ct))!).ShouldBe(9);
    }

    [Fact]
    public async Task Database_has_the_required_extensions_and_module_schemas()
    {
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        var ct = TestContext.Current.CancellationToken;

        var extensions = new List<string>();
        await using (var command = dataSource.CreateCommand("SELECT extname FROM pg_extension"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                extensions.Add(reader.GetString(0));
            }
        }

        foreach (var required in new[] { "pg_trgm", "unaccent", "pgcrypto", "btree_gist", "pg_stat_statements" })
        {
            extensions.ShouldContain(required);
        }

        var schemas = new List<string>();
        await using (var command = dataSource.CreateCommand("SELECT nspname FROM pg_namespace"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                schemas.Add(reader.GetString(0));
            }
        }

        foreach (var required in new[] { "plt", "pty", "pfc", "rat", "uw", "pol", "bil", "clm", "ri", "fin", "doc", "cmp", "chn", "wrk", "dat", "rpt_raw", "rpt_conformed", "rpt_mart", "mig", "mkt", "hangfire" })
        {
            schemas.ShouldContain(required);
        }
    }
}
