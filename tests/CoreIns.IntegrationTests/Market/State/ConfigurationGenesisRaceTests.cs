using CoreIns.Modules.Market.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CoreIns.IntegrationTests.Market.State;

/// <summary>
/// D-SL5-06: api and worker start together and race to write the genesis state. A database of its own (the genesis has not been written yet),
/// so the race is real: all starters must end with one genesis, one registration of each version and one activation per legal entity.
/// </summary>
public sealed class ConfigurationGenesisRaceTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<long> CountAsync(string sql)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task D_SL5_06_many_processes_starting_at_once_write_one_genesis_and_register_each_version_once()
    {
        (await CountAsync("SELECT count(*) FROM mkt.config_state")).ShouldBe(0, "the database starts without a state");

        // Eight independent processes (each with its own cache), half of them starting through the hosted service and half through the first read.
        var processes = Enumerable.Range(0, 8).Select(_ => StateHarness.States(database.AppConnectionString)).ToList();
        var results = await Task.WhenAll(processes.Select((states, i) => Task.Run(async () =>
        {
            if (i % 2 == 0)
            {
                await new ConfigurationStatesStartup(states, Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigurationStatesStartup>.Instance).StartAsync(Ct);
                return (await states.CurrentAsync(Ct)).Hash;
            }

            return (await states.CurrentAsync(Ct)).Hash;
        }, Ct)));

        results.Distinct().Count().ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM mkt.config_state")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM mkt.pack_version")).ShouldBe(3);
        (await CountAsync("SELECT count(*) FROM mkt.pack_activation")).ShouldBe(await CountAsync("SELECT count(*) FROM mkt.legal_entity WHERE pack_id = 'gr'"));
    }
}

/// <summary>Two real api hosts booting together on an empty database (see <see cref="ConfigurationGenesisRaceTests"/>).</summary>
public sealed class ConfigurationGenesisHostsTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<long> CountAsync(string sql)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task D_SL5_06_two_hosts_booting_together_end_with_one_state_and_serve_the_same_hash()
    {
        await using var api = new ApiHostFactory(database.AppConnectionString);
        await using var second = new ApiHostFactory(database.AppConnectionString);

        var hashes = await Task.WhenAll(new[] { api, second }.Select(host => Task.Run(async () =>
        {
            using var client = host.CreateClient();
            using var scope = host.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<CoreIns.Modules.Market.Contracts.IMarketConfigurationService>().CurrentHashAsync(Ct)).Hash;
        }, Ct)));

        hashes[0].ShouldBe(hashes[1]);
        (await CountAsync("SELECT count(*) FROM mkt.config_state WHERE cause = 'GENESIS'")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM mkt.pack_version")).ShouldBe(3);
    }
}
