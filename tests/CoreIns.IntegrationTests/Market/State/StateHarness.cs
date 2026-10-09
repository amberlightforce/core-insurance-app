using System.Text.Json.Nodes;
using CoreIns.CountryPacks.CY;
using CoreIns.CountryPacks.GR.Configuration;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Market.Domain;
using CoreIns.Modules.Market.Persistence;
using CoreIns.Modules.Market.Services;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CoreIns.IntegrationTests.Market.State;

/// <summary>
/// Test-only helpers for configuration states (never an API): build the persisted states over a database, and append a state that switches a
/// pack to another shipped version, the way SL5-MKT-ROLLBACK will (one transaction, state lock, on top of the newest state).
/// </summary>
internal static class StateHarness
{
    public static readonly IPackConfigurationSource[] Gr = [new GrPackConfiguration()];

    public static readonly IPackConfigurationSource[] Cy = [new CyTreatmentRules()];

    public static PersistedConfigurationStates States(string connectionString, IClock? clock = null, IEnumerable<IPackConfigurationSource>? sources = null) =>
        new(NpgsqlDataSource.Create(connectionString), sources ?? Gr, clock ?? SystemClock.Instance, NullLogger<PersistedConfigurationStates>.Instance);

    /// <summary>Appends a state that puts <paramref name="packId"/> at <paramref name="version"/> on top of the newest state; returns its hash.</summary>
    public static async Task<ConfigurationHash> SwitchAsync(
        string connectionString, PersistedConfigurationStates states, string packId, string version, string cause = StateCauses.PackRollback, IClock? clock = null)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await ConfigStateWriter.LockAsync(connection, transaction, TestContext.Current.CancellationToken);

        string manifestText;
        Instant newestAt;
        await using (var select = new NpgsqlCommand("SELECT manifest::text, activated_at FROM mkt.config_state ORDER BY seq DESC LIMIT 1", connection, transaction))
        await using (var reader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
            manifestText = reader.GetString(0);
            newestAt = Instant.FromUtcDateTime(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc));
        }

        var newest = ConfigStateManifest.FromJson(JsonNode.Parse(manifestText));
        var target = states.Shipped.Single(c => c.PackId == packId && c.Version == version);
        var packs = newest.Packs.Where(p => p.PackId != packId).Append(new ManifestPack(packId, version, target.Digest, target.Country!)).ToList();
        var manifest = new ConfigStateManifest(packs, newest.CoreDigest, newest.Hash, cause);
        var hash = await ConfigStateWriter.AppendAsync(
            connection, transaction, manifest, Instant.Max((clock ?? SystemClock.Instance).Now, newestAt), causeRef: Guid.CreateVersion7(), TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return hash;
    }

    /// <summary>Waits until a process whose current-state cache holds the old state must have refreshed it (the 1 s bound plus a margin).</summary>
    public static Task CacheExpiryAsync() => Task.Delay(PersistedConfigurationStates.CacheLifetime + TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
}
