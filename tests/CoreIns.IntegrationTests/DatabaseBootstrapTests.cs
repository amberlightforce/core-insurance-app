using System.Net;
using CoreIns.Host.Database;
using Npgsql;

namespace CoreIns.IntegrationTests;

/// <summary>
/// The shared bootstrap (infra/database/bootstrap.sql), run as on Azure by the Host's bootstrap phase.
/// pg-init has already applied it once when the container started; running it twice more must change nothing.
/// </summary>
public sealed class DatabaseBootstrapTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Bootstrap_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;

        for (var run = 0; run < 2; run++)
        {
            await DatabaseBootstrapper.BootstrapAsync(
                database.AdminConnectionString, DatabaseBootstrapper.DefaultDatabaseName,
                PostgresFixture.AppPassword, PostgresFixture.MigratorPassword, ct);
        }

        // Roles still log in with their passwords, extensions are present once, and the migration still succeeds.
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        await using (var command = app.CreateCommand("SELECT count(*) FROM pg_extension WHERE extname = ANY($1)"))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = DatabaseMigrator.RequiredExtensions.ToArray() });
            (await command.ExecuteScalarAsync(ct)).ShouldBe(5L);
        }

        await DatabaseMigrator.MigrateAsync(
            database.MigratorConnectionString, DatabaseMigrator.DefaultAppRole,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, ct);
    }

    [Fact]
    public async Task Bootstrap_refuses_to_run_without_role_passwords()
    {
        var error = await Should.ThrowAsync<PostgresException>(() => DatabaseBootstrapper.BootstrapAsync(
            database.AdminConnectionString, DatabaseBootstrapper.DefaultDatabaseName, string.Empty, string.Empty,
            TestContext.Current.CancellationToken));

        error.MessageText.ShouldContain("coreins.app_password");
    }

    [Fact]
    public async Task Hangfire_dashboard_opens_for_admins()
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/hangfire", UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, "Platform.Admin");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
