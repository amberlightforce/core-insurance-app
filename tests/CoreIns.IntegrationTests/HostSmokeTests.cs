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
