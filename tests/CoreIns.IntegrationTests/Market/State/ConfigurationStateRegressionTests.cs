using CoreIns.Modules.Market.Domain;
using CoreIns.CountryPacks.GR.Configuration;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Market.Persistence;
using CoreIns.Modules.Market.Queries;
using CoreIns.Modules.Market.Services;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Http;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CoreIns.IntegrationTests.Market.State;

public sealed class ConfigurationStateRegressionTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static ConfigurationEngine Engine(IConfigurationStates states) => new(states, new LegalEntityRegistry([]), new Env(), SystemClock.Instance);

    [Fact]
    public async Task Unknown_implicit_pin_must_fail_closed()
    {
        using var states = StateHarness.States(database.AppConnectionString);
        await states.EnsureGenesisAsync(Ct);
        var unknown = new ConfigurationHash(Sha256Hash.ComputeUtf8("old unknown worker event pin"));
        var ex = await Should.ThrowAsync<DomainException>(async () => await Engine(states).StateAsync(null, null, unknown, Ct));
        ex.Error.Code.ToString().ShouldBe("MKT-ERR-CFG-HASH-UNKNOWN");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Future_knownAt_must_be_refused_even_with_explicit_hash(bool explicitHash)
    {
        using var states = StateHarness.States(database.AppConnectionString);
        await states.EnsureGenesisAsync(Ct);
        var hash = explicitHash ? (await states.CurrentAsync(Ct)).Hash : (ConfigurationHash?)null;
        var ex = await Should.ThrowAsync<DomainException>(async () => await Engine(states).StateAsync(hash, SystemClock.Instance.Now.Plus(TimeSpan.FromDays(1)), null, Ct));
        ex.Error.Code.ToString().ShouldBe("MKT-ERR-CFG-VALIDATION");
    }

    [Fact]
    public async Task First_read_after_database_unavailable_at_startup_must_recheck_shipped_versions()
    {
        using var initial = StateHarness.States(database.AppConnectionString);
        await initial.EnsureGenesisAsync(Ct);
        using var changed = StateHarness.States(database.AppConnectionString, sources: [new ChangedGr()]);
        await database.ExecuteAsSuperuserAsync("REVOKE SELECT ON mkt.pack_version FROM app", Ct);
        try
        {
            await new ConfigurationStatesStartup(changed, NullLogger<ConfigurationStatesStartup>.Instance).StartAsync(Ct);
        }
        finally
        {
            await database.ExecuteAsSuperuserAsync("GRANT SELECT ON mkt.pack_version TO app", Ct);
        }
        await Should.ThrowAsync<PackVersionChangedException>(async () => await changed.CurrentAsync(Ct));
    }

    [Fact]
    public async Task Database_failure_must_not_replace_current_pin_with_genesis()
    {
        var catalogue = new ConfigurationCatalogue(StateHarness.Gr, SystemClock.Instance.Now);
        var resolver = new MarketConfigurationResolver(Engine(new UnreadableStates(catalogue.Hash)));
        await Should.ThrowAsync<NpgsqlException>(async () => await resolver.CurrentHashAsync(null, Ct));
    }

    [Theory]
    [InlineData("/health/live", true)]
    [InlineData("/health/ready", true)]
    [InlineData("/api/pol/v1/jobs", false)]
    [InlineData("/health/live/business", false)]
    public async Task Only_health_endpoints_bypass_unavailable_configuration(string path, bool probe)
    {
        var catalogue = new ConfigurationCatalogue(StateHarness.Gr, SystemClock.Instance.Now);
        var resolver = new MarketConfigurationResolver(Engine(new UnreadableStates(catalogue.Hash)));
        var reachedEndpoint = false;
        var middleware = new RequestContextMiddleware(_ => { reachedEndpoint = true; return Task.CompletedTask; });
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        var context = new RequestContext();
        var options = Options.Create(new StampOptions { LegalEntity = "GR-TEST", Country = "GR" });
        if (probe)
        {
            await middleware.InvokeAsync(http, context, options, resolver);
            reachedEndpoint.ShouldBeTrue();
            context.ConfigurationHash.ShouldBeNull();
        }
        else
        {
            await Should.ThrowAsync<NpgsqlException>(() => middleware.InvokeAsync(http, context, options, resolver));
            reachedEndpoint.ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData("pg_advisory_lock")]
    [InlineData("pg_advisory_xact_lock_shared")]
    public async Task State_writer_must_require_exclusive_transaction_lock(string lockFunction)
    {
        using var states = StateHarness.States(database.AppConnectionString);
        await states.EnsureGenesisAsync(Ct);
        var current = await states.CurrentAsync(Ct);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var connection = await source.OpenConnectionAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var take = new NpgsqlCommand($"SELECT {lockFunction}({MarketStateSql.LockClass}, {MarketStateSql.LockObject})", connection, transaction);
        await take.ExecuteNonQueryAsync(Ct);
        var next = new ConfigStateManifest(current.Manifest.Packs, current.Manifest.CoreDigest, current.Hash, StateCauses.PackActivation);
        try
        {
            var ex = await Should.ThrowAsync<PostgresException>(async () => await ConfigStateWriter.AppendAsync(connection, transaction, next, SystemClock.Instance.Now, null, Ct));
            ex.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
        }
        finally
        {
            await transaction.RollbackAsync(Ct);
            if (lockFunction == "pg_advisory_lock")
            {
                await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({MarketStateSql.LockClass}, {MarketStateSql.LockObject})", connection);
                await release.ExecuteNonQueryAsync(Ct);
            }
        }
    }

    private sealed class UnreadableStates(ConfigurationHash genesis) : IConfigurationStates
    {
        public ConfigurationHash ExpectedGenesisHash => genesis;
        public Task<ConfigurationCatalogue> CurrentAsync(CancellationToken ct) => throw new NpgsqlException("database failed after activation");
        public Task<ConfigurationCatalogue?> ByHashAsync(ConfigurationHash hash, CancellationToken ct) => throw new NpgsqlException("unreadable");
        public Task<ConfigurationCatalogue?> AtAsync(Instant instant, CancellationToken ct) => throw new NpgsqlException("unreadable");
    }
    private sealed class ChangedGr : IVersionedPackConfigurationSource
    {
        private readonly GrPackConfiguration original = new();
        public string PackId => original.PackId;
        public string PackVersion => original.PackVersion;
        public string Country => original.Country;
        public IReadOnlyList<PackConfigValue> Values => original.Values.Select((v, i) => i == 0 ? v with { Note = "altered after release" } : v).ToList();
        public IReadOnlyList<PackVersionData> Versions => [original.Versions[0], new(PackVersion, Values)];
    }
    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "review";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

