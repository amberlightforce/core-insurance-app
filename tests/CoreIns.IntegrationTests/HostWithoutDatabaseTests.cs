using System.Net;

namespace CoreIns.IntegrationTests;

/// <summary>Probe semantics when PostgreSQL is unreachable: alive, but not ready. Needs no Docker.</summary>
public sealed class HostWithoutDatabaseTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=coreins;Username=app;Password=none;Timeout=2";

    [Fact]
    public async Task Api_mode_is_alive_but_not_ready_without_a_database()
    {
        await using var factory = new ApiHostFactory(UnreachableDatabase);
        using var client = factory.CreateClient();

        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }
}
