using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Market.Domain;
using CoreIns.Modules.Market.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace CoreIns.Modules.Market.Services;

/// <summary>
/// Where configuration states come from (D-SL5-06). A state is immutable once recorded, so a state found by hash never changes (P-07);
/// only "which state is current" moves, and it is read at most 1 s stale (REQ-MKT-050).
/// </summary>
internal interface IConfigurationStates
{
    /// <summary>The current state: the newest recorded one, read from the database at most 1 s ago.</summary>
    Task<ConfigurationCatalogue> CurrentAsync(CancellationToken cancellationToken);

    /// <summary>The state recorded under <paramref name="hash"/>, or null when this stamp never recorded it.</summary>
    Task<ConfigurationCatalogue?> ByHashAsync(ConfigurationHash hash, CancellationToken cancellationToken);

    /// <summary>The state that was current at <paramref name="knownAt"/> (the newest with <c>activated_at</c> at or before it), or null before the first state.</summary>
    Task<ConfigurationCatalogue?> AtAsync(Instant knownAt, CancellationToken cancellationToken);

    /// <summary>The expected genesis hash of this release (diagnostics and genesis validation).</summary>
    ConfigurationHash ExpectedGenesisHash { get; }
}

/// <summary>One fixed state: tests, and tools that build a catalogue by hand. Its hash is the only recorded one.</summary>
internal sealed class StaticConfigurationStates(ConfigurationCatalogue catalogue) : IConfigurationStates
{
    public ConfigurationCatalogue Catalogue => catalogue;

    public ConfigurationHash ExpectedGenesisHash => catalogue.Hash;

    public Task<ConfigurationCatalogue> CurrentAsync(CancellationToken cancellationToken) => Task.FromResult(catalogue);

    public Task<ConfigurationCatalogue?> ByHashAsync(ConfigurationHash hash, CancellationToken cancellationToken) =>
        Task.FromResult<ConfigurationCatalogue?>(hash == catalogue.Hash ? catalogue : null);

    public Task<ConfigurationCatalogue?> AtAsync(Instant knownAt, CancellationToken cancellationToken) =>
        Task.FromResult<ConfigurationCatalogue?>(knownAt >= catalogue.ActivatedAt ? catalogue : null);
}

/// <summary>A shipped pack version differs from the one recorded under the same id and version: a version never changes (D-SL5-07).</summary>
internal sealed class PackVersionChangedException(string message) : InvalidOperationException(message);

/// <summary>
/// The configuration states of <c>mkt.config_state</c>, built from <c>mkt.pack_version</c> (D-SL5-06, REQ-MKT-048/050). States are cached by
/// hash for ever (immutable); the current state is re-read when the last read is older than <see cref="CacheLifetime"/>, so nothing starting
/// a second after an activation sees the old hash. A database that cannot be read when the cache has expired is an error, never a stale answer.
/// </summary>
internal sealed partial class PersistedConfigurationStates : IConfigurationStates, IDisposable
{
    /// <summary>The longest the current state is served from memory (REQ-MKT-050: nothing starting 1 s after an activation sees the old hash).</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(1);

    private readonly NpgsqlDataSource _dataSource;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<IPackConfigurationSource> _sources;
    private readonly IReadOnlyList<PackVersionContent> _shipped;
    private readonly ConcurrentDictionary<string, ConfigurationCatalogue> _byHash = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PackVersionContent> _packs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private Snapshot? _snapshot;
    private volatile bool _registered;

    public PersistedConfigurationStates(
        NpgsqlDataSource dataSource, IEnumerable<IPackConfigurationSource> sources, IClock clock, ILogger<PersistedConfigurationStates> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _dataSource = dataSource;
        _clock = clock;
        _logger = logger;
        _sources = [.. sources];
        _shipped = ShippedVersions(_sources);
        ExpectedGenesisHash = CatalogueState.Genesis(_sources).Manifest.Hash;
    }

    public ConfigurationHash ExpectedGenesisHash { get; }

    /// <summary>Every version this release ships, the core defaults included, each validated as a loadable state before it can be recorded.</summary>
    internal IReadOnlyList<PackVersionContent> Shipped => _shipped;

    public async Task<ConfigurationCatalogue> CurrentAsync(CancellationToken cancellationToken)
    {
        await EnsureRegisteredAsync(cancellationToken).ConfigureAwait(false);
        if (Fresh(_snapshot, _clock.Now) is { } hit)
        {
            return hit;
        }

        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Fresh(_snapshot, _clock.Now) is { } again)
            {
                return again;
            }

            // The read starts now: whatever it returns was current no later than this instant, so the 1 s bound counts from here.
            var started = _clock.Now;
            var newest = await NewestHashAsync(cancellationToken).ConfigureAwait(false);
            if (newest is null)
            {
                await EnsureGenesisAsync(cancellationToken).ConfigureAwait(false);
                newest = await NewestHashAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("No configuration state exists after genesis.");
            }

            var catalogue = await ByHashAsync(newest.Value, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"The newest configuration state {newest} cannot be read.");
            _snapshot = new Snapshot(catalogue, started);
            return catalogue;
        }
        finally
        {
            _refresh.Release();
        }
    }

    public async Task<ConfigurationCatalogue?> ByHashAsync(ConfigurationHash hash, CancellationToken cancellationToken)
    {
        await EnsureRegisteredAsync(cancellationToken).ConfigureAwait(false);
        var key = hash.ToString();
        if (_byHash.TryGetValue(key, out var cached))
        {
            return cached;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        string manifestText;
        Instant activatedAt;
        await using (var command = new NpgsqlCommand("SELECT manifest::text, activated_at FROM mkt.config_state WHERE hash = @hash", connection))
        {
            command.Parameters.AddWithValue("hash", NpgsqlDbType.Char, key);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            manifestText = reader.GetString(0);
            activatedAt = Instant.FromUtcDateTime(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc));
        }

        var manifest = ConfigStateManifest.FromJson(JsonNode.Parse(manifestText));
        if (manifest.Hash != hash)
        {
            throw new InvalidOperationException($"Configuration state {hash} does not match the hash of its stored manifest; the state table was altered.");
        }

        var core = await LoadByDigestAsync(connection, CoreDefaults.PackId, manifest.CoreDigest, cancellationToken).ConfigureAwait(false);
        var packs = new List<PackVersionContent>();
        foreach (var named in manifest.Packs)
        {
            packs.Add(await LoadAsync(connection, named.PackId, named.Version, cancellationToken).ConfigureAwait(false));
        }

        var catalogue = new ConfigurationCatalogue(new CatalogueState(manifest, core, packs), activatedAt);
        return _byHash.GetOrAdd(key, catalogue);
    }

    public async Task<ConfigurationCatalogue?> AtAsync(Instant knownAt, CancellationToken cancellationToken)
    {
        await EnsureRegisteredAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT hash FROM mkt.config_state WHERE activated_at <= @at ORDER BY seq DESC LIMIT 1", connection);
        command.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz, knownAt.ToUtcDateTime());
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string hash
            ? await ByHashAsync(ConfigurationHash.Parse(hash.Trim()), cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    /// Registers every shipped version (idempotent by digest) and, when no state exists, writes the genesis state over the newest version of each
    /// pack with an ACTIVATE activation per legal entity of those packs (D-SL5-06). All of it runs in one transaction under the state advisory lock,
    /// so api and worker starting together produce one genesis. A recorded version whose shipped digest differs throws <see cref="PackVersionChangedException"/>.
    /// </summary>
    public async Task EnsureGenesisAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigStateWriter.LockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var now = _clock.Now;
        foreach (var content in _shipped)
        {
            await RegisterAsync(connection, transaction, content, now, cancellationToken).ConfigureAwait(false);
        }

        if (await ConfigStateWriter.NewestAsync(connection, transaction, cancellationToken).ConfigureAwait(false) is null)
        {
            var genesis = CatalogueState.Genesis(_sources);
            var hash = await ConfigStateWriter.AppendAsync(connection, transaction, genesis.Manifest, now, causeRef: null, cancellationToken).ConfigureAwait(false);
            await WriteGenesisActivationsAsync(connection, transaction, genesis, hash, now, cancellationToken).ConfigureAwait(false);
            var packs = string.Join(", ", genesis.Manifest.Packs.Select(p => $"{p.PackId}@{p.Version}"));
            var written = hash.ToString();
            GenesisWritten(_logger, written, packs);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _registered = true;
    }

    private Task EnsureRegisteredAsync(CancellationToken cancellationToken) =>
        _registered ? Task.CompletedTask : EnsureGenesisAsync(cancellationToken);

    private static async Task WriteGenesisActivationsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CatalogueState genesis, ConfigurationHash hash, Instant now, CancellationToken cancellationToken)
    {
        foreach (var pack in genesis.Manifest.Packs)
        {
            var entities = new List<Guid>();
            await using (var select = new NpgsqlCommand("SELECT legal_entity_id FROM mkt.legal_entity WHERE pack_id = @pack ORDER BY code", connection, transaction))
            {
                select.Parameters.AddWithValue("pack", NpgsqlDbType.Varchar, pack.PackId);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    entities.Add(reader.GetGuid(0));
                }
            }

            foreach (var entity in entities)
            {
                await using var insert = new NpgsqlCommand(
                    """
                    INSERT INTO mkt.pack_activation
                        (id, legal_entity_id, pack_id, version, kind, status, requested_by, decided_by, approval_request_id, activated_at, resulting_hash, supersedes_id, created_at)
                    VALUES (@id, @entity, @pack, @version, 'ACTIVATE', 'ACTIVE', 'system:genesis', 'system:genesis', NULL, @at, @hash, NULL, @at)
                    """, connection, transaction);
                insert.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, Guid.CreateVersion7());
                insert.Parameters.AddWithValue("entity", NpgsqlDbType.Uuid, entity);
                insert.Parameters.AddWithValue("pack", NpgsqlDbType.Varchar, pack.PackId);
                insert.Parameters.AddWithValue("version", NpgsqlDbType.Varchar, pack.Version);
                insert.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz, now.ToUtcDateTime());
                insert.Parameters.AddWithValue("hash", NpgsqlDbType.Char, hash.ToString());
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task RegisterAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, PackVersionContent content, Instant now, CancellationToken cancellationToken)
    {
        await using (var select = new NpgsqlCommand(
            "SELECT content_digest, country FROM mkt.pack_version WHERE pack_id = @pack AND version = @version", connection, transaction))
        {
            select.Parameters.AddWithValue("pack", NpgsqlDbType.Varchar, content.PackId);
            select.Parameters.AddWithValue("version", NpgsqlDbType.Varchar, content.Version);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var recorded = reader.GetString(0).Trim();
                var country = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
                if (recorded != content.Digest.ToString() || country != content.Country)
                {
                    throw new PackVersionChangedException(
                        $"Pack version {content.PackId}@{content.Version} is recorded with content digest {recorded}, but this release ships digest {content.Digest}. "
                        + "A shipped version never changes: ship the new content as a new version (REQ-MKT-129, D-SL5-07).");
                }

                return;
            }
        }

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO mkt.pack_version (pack_id, version, country, content_digest, "values", status, registered_at)
            VALUES (@pack, @version, @country, @digest, @values, 'Published', @at)
            """, connection, transaction);
        insert.Parameters.AddWithValue("pack", NpgsqlDbType.Varchar, content.PackId);
        insert.Parameters.AddWithValue("version", NpgsqlDbType.Varchar, content.Version);
        insert.Parameters.AddWithValue("country", NpgsqlDbType.Char, (object?)content.Country ?? DBNull.Value);
        insert.Parameters.AddWithValue("digest", NpgsqlDbType.Char, content.Digest.ToString());
        insert.Parameters.AddWithValue("values", NpgsqlDbType.Jsonb, PackVersionContent.ToJson(content.Values).ToJsonString());
        insert.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz, now.ToUtcDateTime());
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConfigurationHash?> NewestHashAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ConfigStateWriter.NewestAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PackVersionContent> LoadAsync(NpgsqlConnection connection, string packId, string version, CancellationToken cancellationToken)
    {
        var key = packId + "@" + version;
        if (_packs.TryGetValue(key, out var cached))
        {
            return cached;
        }

        await using var command = new NpgsqlCommand(
            "SELECT country, content_digest, \"values\"::text FROM mkt.pack_version WHERE pack_id = @pack AND version = @version", connection);
        command.Parameters.AddWithValue("pack", NpgsqlDbType.Varchar, packId);
        command.Parameters.AddWithValue("version", NpgsqlDbType.Varchar, version);
        return await ReadPackAsync(command, packId, version, key, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PackVersionContent> LoadByDigestAsync(NpgsqlConnection connection, string packId, Sha256Hash digest, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT country, content_digest, \"values\"::text, version FROM mkt.pack_version WHERE pack_id = @pack AND content_digest = @digest", connection);
        command.Parameters.AddWithValue("pack", NpgsqlDbType.Varchar, packId);
        command.Parameters.AddWithValue("digest", NpgsqlDbType.Char, digest.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"No registered version of '{packId}' has content digest {digest}; a state names core defaults that were never recorded.");
        }

        return Verified(reader, packId, reader.GetString(3), $"{packId}@{reader.GetString(3)}");
    }

    private async Task<PackVersionContent> ReadPackAsync(NpgsqlCommand command, string packId, string version, string key, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Pack version {key} is named by a configuration state but is not registered.");
        }

        return Verified(reader, packId, version, key);
    }

    private PackVersionContent Verified(NpgsqlDataReader reader, string packId, string version, string key)
    {
        var country = reader.IsDBNull(0) ? null : reader.GetString(0).Trim();
        var stored = Sha256Hash.Parse(reader.GetString(1).Trim());
        var values = PackVersionContent.FromJson(JsonNode.Parse(reader.GetString(2)));
        if (PackVersionContent.DigestOf(values) != stored)
        {
            throw new InvalidOperationException($"Pack version {key} does not match its recorded content digest {stored}; the registry was altered.");
        }

        return _packs.GetOrAdd(key, new PackVersionContent(packId, version, country, stored, values));
    }

    private static ConfigurationCatalogue? Fresh(Snapshot? snapshot, Instant now) =>
        snapshot is not null && now >= snapshot.CheckedAt && now - snapshot.CheckedAt < CacheLifetime ? snapshot.Catalogue : null;

    private static List<PackVersionContent> ShippedVersions(IReadOnlyList<IPackConfigurationSource> sources)
    {
        var all = new List<PackVersionContent> { CoreDefaults.Content };
        foreach (var source in sources)
        {
            var versions = source is IVersionedPackConfigurationSource versioned
                ? versioned.Versions
                : [new PackVersionData(source.PackVersion, source.Values)];
            if (versions.Count == 0 || versions[^1].Version != source.PackVersion)
            {
                throw new InvalidOperationException($"Pack '{source.PackId}': the newest shipped version must be {source.PackVersion}.");
            }

            foreach (var version in versions)
            {
                if (!Semver().IsMatch(version.Version))
                {
                    throw new InvalidOperationException($"Pack '{source.PackId}': '{version.Version}' is not a semantic version (REQ-MKT-129).");
                }

                if (all.Any(c => c.PackId == source.PackId && c.Version == version.Version))
                {
                    throw new InvalidOperationException($"Pack '{source.PackId}' ships version {version.Version} twice.");
                }

                var content = PackVersionContent.Of(source.PackId, version.Version, source.Country, version.Values);

                // A version is recorded for ever: it must load before it is registered.
                _ = new ConfigurationCatalogue(
                    new CatalogueState(
                        ConfigStateManifest.Genesis([content], CoreDefaults.Content.Digest), CoreDefaults.Content, [content]),
                    Instant.MinValue);
                all.Add(content);
            }
        }

        return all;
    }

    /// <summary>Releases the refresh gate.</summary>
    public void Dispose() => _refresh.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "Configuration genesis state {Hash} written ({Packs}).")]
    private static partial void GenesisWritten(ILogger logger, string hash, string packs);

    [GeneratedRegex("^[0-9]+[.][0-9]+[.][0-9]+$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Semver();

    private sealed record Snapshot(ConfigurationCatalogue Catalogue, Instant CheckedAt);
}

/// <summary>
/// Writes the genesis state when the host starts (D-SL5-06). A pack version whose shipped content differs from its recorded digest stops
/// the host. A database that cannot be reached yet (or is not migrated yet) only logs: the first read retries, and the readiness probe
/// reports the database.
/// </summary>
internal sealed partial class ConfigurationStatesStartup(PersistedConfigurationStates states, ILogger<ConfigurationStatesStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await states.EnsureGenesisAsync(limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException or IOException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            GenesisNotChecked(logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Configuration genesis was not checked at startup (database not ready); the first read retries it.")]
    private static partial void GenesisNotChecked(ILogger logger, Exception exception);
}
