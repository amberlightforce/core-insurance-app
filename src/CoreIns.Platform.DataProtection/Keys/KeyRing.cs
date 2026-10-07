using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace CoreIns.Platform.DataProtection.Keys;

/// <summary>An unwrapped data key version (held in memory only).</summary>
public sealed class DataKey
{
    internal DataKey(LegalEntityId legalEntity, KeyPurpose purpose, int version, byte[] material)
    {
        LegalEntity = legalEntity;
        Purpose = purpose;
        Version = version;
        Material = material;
    }

    public LegalEntityId LegalEntity { get; }

    public KeyPurpose Purpose { get; }

    public int Version { get; }

    internal byte[] Material { get; }
}

/// <summary>Key ring options (D-ARC-23).</summary>
public sealed class KeyRingOptions
{
    /// <summary>
    /// Hard bound on the age of the key-ring view used to pick the key for a <b>write</b> (encryption, blind-index
    /// value). A replica never seals or indexes with a view older than this: it reloads (asynchronously, or synchronously
    /// within <see cref="SyncReloadTimeout"/>) or fails closed with <see cref="KeyRingStaleException"/>. Retirement waits
    /// at least this long (plus <see cref="RetirementMargin"/>) after each state change, so no replica can still write
    /// with a version being retired.
    /// </summary>
    public TimeSpan MaxStaleForWrite { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Extra margin added to every retirement wait (clock skew between replicas, in-flight writes).</summary>
    public TimeSpan RetirementMargin { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a synchronous write (EF Core converter) may block reloading a too-old view before failing closed.</summary>
    public TimeSpan SyncReloadTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a set of blind-index search candidates (readable versions) is reused (review N5). A version created by
    /// another replica becomes searchable on this replica at most this long after its rotation.
    /// </summary>
    public TimeSpan SearchCandidateCacheDuration { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Versioned data keys per legal entity and purpose (D-ARC-14, D-ARC-23; REQ-PTY-060; NFR-PTY-010 "key rotation
/// without downtime"). Data keys are generated here (256-bit, CSPRNG), stored only wrapped by the legal entity's KEK
/// (<see cref="IKeyProvider"/>), and cached unwrapped in memory.
/// <para><b>Writes</b> (async and synchronous) always use the highest Active version of a view at most
/// <see cref="KeyRingOptions.MaxStaleForWrite"/> old. The synchronous path (EF Core converters, synchronous blind index)
/// refreshes ahead in the background once the view is half that age, so normally it does no I/O; if the view reaches the
/// bound (idle replica, key-store outage) it reloads synchronously within <see cref="KeyRingOptions.SyncReloadTimeout"/>
/// or throws <see cref="KeyRingStaleException"/>. A replica therefore writes with a demoted version for at most
/// MaxStaleForWrite after the demotion.</para>
/// <para><b>Reads</b> (decryption) use any cached readable key; an unknown version re-reads the store. Search
/// candidates come from a view at most <see cref="KeyRingOptions.SearchCandidateCacheDuration"/> old and cover every
/// readable version (Active, DecryptOnly, Retiring), including versions newer than this replica's write key.</para>
/// <para><b>Rotation and retirement</b>: <see cref="RotateAsync"/> adds version n+1 as Active and demotes n to
/// DecryptOnly with <c>DemotedAt</c>. Retirement has two steps, each after the write bound + margin:
/// <see cref="BeginRetirementAsync"/> (DemotedAt + bound passed, first re-scan clean → Retiring, <c>RetiringAt</c>) and
/// <see cref="CompleteRetirementAsync"/> (RetiringAt + bound passed, second re-scan clean → Retired). A write racing the
/// first scan is caught by the second. <see cref="UnretireAsync"/> (Retired → Retiring) is the recovery step when rows
/// sealed with a retired version are found later.</para>
/// <para><b>Operating constraints (D-ARC-23a)</b> — the guarantees above hold only when:
/// <list type="number">
/// <item>every write transaction touching encrypted or blind-indexed columns finishes within
/// MaxStaleForWrite + 2 × RetirementMargin (enforce with PostgreSQL <c>idle_in_transaction_session_timeout</c> and
/// statement timeouts; the retirement scan refuses while older transactions are open — <see cref="GuardedRetirementScan"/>
/// over <see cref="PostgresLongTransactionGuard"/>, <c>pg_stat_activity.xact_start</c>);</item>
/// <item>hosts are NTP-synchronised (skew well under RetirementMargin) and both retirement steps are run by one operator
/// job, so DemotedAt, RetiringAt and the checks use one clock;</item>
/// <item>the key store (<see cref="IDataKeyStore"/>) is always read from the PostgreSQL primary, never a lagging read
/// replica, so a "fresh" view really is fresh.</item>
/// </list></para>
/// <para><b>KEK rotation</b> (new Key Vault key version) is <see cref="RewrapAsync"/>: data keys are re-wrapped under the
/// new KEK version, data is untouched, and an unchanged KEK id fails loudly.</para>
/// </summary>
public sealed class KeyRing
{
    private const int KeySize = 32;

    private readonly IKeyProvider _provider;
    private readonly IDataKeyStore _store;
    private readonly TimeProvider _time;
    private readonly KeyRingOptions _options;
    private readonly ConcurrentDictionary<(LegalEntityId, KeyPurpose), RingView> _views = new();
    private readonly ConcurrentDictionary<(LegalEntityId, KeyPurpose, int), DataKey> _unwrapped = new();
    private readonly ConcurrentDictionary<(LegalEntityId, KeyPurpose), SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<(LegalEntityId, KeyPurpose), byte> _refreshing = new();

    public KeyRing(IKeyProvider provider, IDataKeyStore store, TimeProvider time, KeyRingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        _provider = provider;
        _store = store;
        _time = time;
        _options = options ?? new KeyRingOptions();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.MaxStaleForWrite, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.RetirementMargin, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.SyncReloadTimeout, TimeSpan.Zero, nameof(options));
    }

    public KeyRingOptions Options => _options;

    /// <summary>
    /// The key for a write: the highest Active version of a view at most MaxStaleForWrite old (reloaded when older);
    /// creates version 1 on first use.
    /// </summary>
    /// <exception cref="KeyRingStaleException">The key store could not be read to refresh a too-old view.</exception>
    public async ValueTask<DataKey> GetActiveAsync(LegalEntityId legalEntity, KeyPurpose purpose, CancellationToken cancellationToken = default)
    {
        var view = await WriteViewAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
        var active = ActiveOf(view);
        if (active is null)
        {
            await CreateFirstVersionAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
            view = await ReloadForWriteAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
            active = ActiveOf(view) ?? throw new CryptographicException("No active data key after creation.");
        }

        return await UnwrapAsync(active, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous <see cref="GetActiveAsync"/> for EF Core value converters and the synchronous blind index. Serves the
    /// cached write key while the view is within MaxStaleForWrite (refreshing ahead in the background past half of it);
    /// beyond the bound it reloads synchronously within SyncReloadTimeout or throws <see cref="KeyRingStaleException"/>.
    /// </summary>
    public DataKey GetActive(LegalEntityId legalEntity, KeyPurpose purpose)
    {
        if (_views.TryGetValue((legalEntity, purpose), out var view)
            && Age(view) <= _options.MaxStaleForWrite
            && ActiveOf(view) is { } active
            && _unwrapped.TryGetValue((legalEntity, purpose, active.Version), out var key))
        {
            if (Age(view) > TimeSpan.FromTicks(_options.MaxStaleForWrite.Ticks / 2))
            {
                RefreshInBackground(legalEntity, purpose);
            }

            return key;
        }

        try
        {
            return Task.Run(() => GetActiveAsync(legalEntity, purpose).AsTask())
                .WaitAsync(_options.SyncReloadTimeout)
                .GetAwaiter().GetResult();
        }
        catch (TimeoutException exception)
        {
            throw new KeyRingStaleException(
                $"The key ring could not be refreshed within {_options.SyncReloadTimeout}; the write is refused.", exception);
        }
    }

    /// <summary>A specific readable version (Active, DecryptOnly or Retiring); re-reads the store when the version is unknown.</summary>
    public async ValueTask<DataKey> GetAsync(LegalEntityId legalEntity, KeyPurpose purpose, int version, CancellationToken cancellationToken = default)
    {
        if (_unwrapped.TryGetValue((legalEntity, purpose, version), out var cached))
        {
            return cached;
        }

        var view = _views.TryGetValue((legalEntity, purpose), out var known) ? known : null;
        var key = view?.Keys.FirstOrDefault(candidate => candidate.Version == version);
        if (key is null || !IsReadable(key))
        {
            view = await ReloadAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
            key = view.Keys.FirstOrDefault(candidate => candidate.Version == version);
        }

        if (key is null || !IsReadable(key))
        {
            throw new CryptographicException($"Data key version {version} is not readable.");
        }

        return await UnwrapAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous <see cref="GetAsync"/> for EF Core value converters. A version not yet loaded is fetched with the wait
    /// bounded by <see cref="KeyRingOptions.SyncReloadTimeout"/>; on timeout <see cref="KeyRingStaleException"/> is thrown
    /// instead of blocking a request thread indefinitely (review R4).
    /// </summary>
    public DataKey Get(LegalEntityId legalEntity, KeyPurpose purpose, int version)
    {
        if (_unwrapped.TryGetValue((legalEntity, purpose, version), out var key))
        {
            return key;
        }

        try
        {
            return Task.Run(() => GetAsync(legalEntity, purpose, version).AsTask())
                .WaitAsync(_options.SyncReloadTimeout)
                .GetAwaiter().GetResult();
        }
        catch (TimeoutException exception)
        {
            throw new KeyRingStaleException(
                $"Data key version {version} could not be loaded within {_options.SyncReloadTimeout}.", exception);
        }
    }

    /// <summary>
    /// All readable versions (Active first, then the others newest first) from a view at most
    /// SearchCandidateCacheDuration old: the blind-index search candidate set.
    /// </summary>
    public async ValueTask<IReadOnlyList<DataKey>> GetReadableAsync(LegalEntityId legalEntity, KeyPurpose purpose, CancellationToken cancellationToken = default)
    {
        if (!_views.TryGetValue((legalEntity, purpose), out var view) || Age(view) > _options.SearchCandidateCacheDuration)
        {
            view = await ReloadAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
        }

        if (ActiveOf(view) is null)
        {
            await GetActiveAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
            view = await ReloadAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
        }

        var ordered = view.Keys
            .Where(IsReadable)
            .OrderByDescending(key => key.Status == DataKeyStatus.Active)
            .ThenByDescending(key => key.Version);
        var keys = new List<DataKey>();
        foreach (var key in ordered)
        {
            keys.Add(await UnwrapAsync(key, cancellationToken).ConfigureAwait(false));
        }

        return keys;
    }

    /// <summary>Loads and unwraps every readable key of the legal entity (start-up, so request threads do not block).</summary>
    public async ValueTask WarmUpAsync(LegalEntityId legalEntity, CancellationToken cancellationToken = default)
    {
        foreach (var purpose in Enum.GetValues<KeyPurpose>())
        {
            await GetActiveAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
            await GetReadableAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Rotates the data key: adds version n+1 as Active and demotes the previous Active (persisting DemotedAt).</summary>
    /// <returns>The new version.</returns>
    public async ValueTask<int> RotateAsync(LegalEntityId legalEntity, KeyPurpose purpose, CancellationToken cancellationToken = default)
    {
        var gate = Gate(legalEntity, purpose);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var view = await ReloadAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
            var version = view.Keys.Count == 0 ? 1 : view.Keys.Max(key => key.Version) + 1;
            await AddNewVersionAsync(legalEntity, purpose, version, cancellationToken).ConfigureAwait(false);

            // The new version is Active before the old one is demoted: writers pick the highest Active version.
            var now = _time.GetUtcNow();
            foreach (var old in view.Keys.Where(key => key.Status == DataKeyStatus.Active))
            {
                await _store.UpdateAsync(old with { Status = DataKeyStatus.DecryptOnly, DemotedAt = now }, cancellationToken).ConfigureAwait(false);
            }

            await ReloadAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
            return version;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Retirement step 1: DecryptOnly → Retiring. Refused (InvalidOperationException) unless the version is DecryptOnly,
    /// DemotedAt + MaxStaleForWrite + RetirementMargin has passed (no replica can still pick it for a write), and the
    /// first re-scan finds no row using it.
    /// </summary>
    public async ValueTask BeginRetirementAsync(
        LegalEntityId legalEntity, KeyPurpose purpose, int version, IRetirementScan scan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var key = await FindAsync(legalEntity, purpose, version, cancellationToken).ConfigureAwait(false);
        if (key.Status != DataKeyStatus.DecryptOnly)
        {
            throw new InvalidOperationException(key.Status == DataKeyStatus.Active
                ? "The Active data key cannot be retired; rotate first."
                : $"Data key version {version} is {key.Status}, not DecryptOnly.");
        }

        EnsureWaited(key.DemotedAt ?? key.CreatedAt, version, "demoted");
        await EnsureNoRowsAsync(scan, legalEntity, purpose, version, cancellationToken).ConfigureAwait(false);

        await _store.UpdateAsync(key with { Status = DataKeyStatus.Retiring, RetiringAt = _time.GetUtcNow() }, cancellationToken).ConfigureAwait(false);
        await ReloadAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Retirement step 2: Retiring → Retired. Refused unless RetiringAt + MaxStaleForWrite + RetirementMargin has passed
    /// and the second re-scan finds no row using the version (a write that raced the first scan keeps it Retiring).
    /// </summary>
    public async ValueTask CompleteRetirementAsync(
        LegalEntityId legalEntity, KeyPurpose purpose, int version, IRetirementScan scan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var key = await FindAsync(legalEntity, purpose, version, cancellationToken).ConfigureAwait(false);
        if (key.Status != DataKeyStatus.Retiring)
        {
            throw new InvalidOperationException($"Data key version {version} is {key.Status}, not Retiring; begin the retirement first.");
        }

        EnsureWaited(key.RetiringAt ?? _time.GetUtcNow(), version, "marked retiring");
        await EnsureNoRowsAsync(scan, legalEntity, purpose, version, cancellationToken).ConfigureAwait(false);

        await _store.UpdateAsync(key with { Status = DataKeyStatus.Retired }, cancellationToken).ConfigureAwait(false);
        _unwrapped.TryRemove((legalEntity, purpose, version), out _);
        await ReloadAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recovery (D-ARC-23a (4)): Retired → Retiring, when rows sealed or indexed with a retired version are found after
    /// all. The version becomes readable and searchable again (it is never written); <c>RetiringAt</c> is reset to now, so
    /// a later <see cref="CompleteRetirementAsync"/> waits the full bound and needs a clean scan again. The wrapped key is
    /// never deleted on retirement, so this is always possible.
    /// </summary>
    public async ValueTask UnretireAsync(LegalEntityId legalEntity, KeyPurpose purpose, int version, CancellationToken cancellationToken = default)
    {
        var key = await FindAsync(legalEntity, purpose, version, cancellationToken).ConfigureAwait(false);
        if (key.Status != DataKeyStatus.Retired)
        {
            throw new InvalidOperationException($"Data key version {version} is {key.Status}, not Retired.");
        }

        await _store.UpdateAsync(key with { Status = DataKeyStatus.Retiring, RetiringAt = _time.GetUtcNow() }, cancellationToken).ConfigureAwait(false);
        await ReloadAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// KEK rotation: refreshes the provider's current KEK version and re-wraps every non-retired data key of the legal
    /// entity under it. Encrypted data and blind indexes are unchanged. Keys already wrapped by the current KEK version
    /// are left alone; if no key changed KEK version, the KEK was not rotated and the call fails.
    /// </summary>
    /// <returns>Number of data keys re-wrapped.</returns>
    public async ValueTask<int> RewrapAsync(LegalEntityId legalEntity, CancellationToken cancellationToken = default)
    {
        await _provider.RefreshAsync(legalEntity, cancellationToken).ConfigureAwait(false);

        var total = 0;
        var count = 0;
        foreach (var purpose in Enum.GetValues<KeyPurpose>())
        {
            var view = await ReloadAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
            foreach (var key in view.Keys.Where(key => key.Status != DataKeyStatus.Retired))
            {
                total++;
                var context = new DataKeyContext(key.LegalEntity, key.Purpose, key.Version);
                var material = await _provider.UnwrapKeyAsync(context, key.KeyEncryptionKeyId, key.WrappedKey, cancellationToken).ConfigureAwait(false);
                try
                {
                    var wrapped = await _provider.WrapKeyAsync(context, material, cancellationToken).ConfigureAwait(false);
                    if (string.Equals(wrapped.KeyEncryptionKeyId, key.KeyEncryptionKeyId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    await _store.UpdateAsync(
                        key with { KeyEncryptionKeyId = wrapped.KeyEncryptionKeyId, WrappedKey = wrapped.WrappedKey },
                        cancellationToken).ConfigureAwait(false);
                    count++;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(material);
                }
            }

            await ReloadAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
        }

        if (total > 0 && count == 0)
        {
            throw new InvalidOperationException(
                "Re-wrap changed no data key: the key-encryption key version is unchanged (rotate the KEK in the key-management service first).");
        }

        return count;
    }

    private static bool IsReadable(WrappedDataKey key) => key.Status != DataKeyStatus.Retired;

    private static WrappedDataKey? ActiveOf(RingView view) =>
        view.Keys.Where(key => key.Status == DataKeyStatus.Active).MaxBy(key => key.Version);

    private TimeSpan Age(RingView view) => _time.GetUtcNow() - view.LoadedAt;

    private void EnsureWaited(DateTimeOffset since, int version, string what)
    {
        var earliest = since + _options.MaxStaleForWrite + _options.RetirementMargin;
        if (_time.GetUtcNow() < earliest)
        {
            throw new InvalidOperationException(
                $"Data key version {version} was {what} too recently; replicas may still write with it. Retry after {earliest:O}.");
        }
    }

    private static async ValueTask EnsureNoRowsAsync(
        IRetirementScan scan, LegalEntityId legalEntity, KeyPurpose purpose, int version, CancellationToken cancellationToken)
    {
        var remaining = await scan.CountRowsUsingAsync(legalEntity, purpose, version, cancellationToken).ConfigureAwait(false);
        if (remaining > 0)
        {
            throw new InvalidOperationException($"{remaining} rows still use data key version {version}; re-encrypt / re-index them first.");
        }
    }

    private async ValueTask<WrappedDataKey> FindAsync(LegalEntityId legalEntity, KeyPurpose purpose, int version, CancellationToken cancellationToken)
    {
        var view = await ReloadAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
        return view.Keys.FirstOrDefault(candidate => candidate.Version == version)
               ?? throw new KeyNotFoundException($"Data key version {version} does not exist.");
    }

    private void RefreshInBackground(LegalEntityId legalEntity, KeyPurpose purpose)
    {
        if (!_refreshing.TryAdd((legalEntity, purpose), 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ReloadAsync(legalEntity, purpose, CancellationToken.None).ConfigureAwait(false);
                if (_views.TryGetValue((legalEntity, purpose), out var view) && ActiveOf(view) is { } active)
                {
                    await UnwrapAsync(active, CancellationToken.None).ConfigureAwait(false);
                }
            }
#pragma warning disable CA1031 // Background refresh: any failure (store outage) leaves the view to age; writes then fail closed.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
            finally
            {
                _refreshing.TryRemove((legalEntity, purpose), out _);
            }
        });
    }

    /// <summary>The cached view when at most MaxStaleForWrite old, otherwise a reload; reload failures fail closed.</summary>
    private async ValueTask<RingView> WriteViewAsync(LegalEntityId legalEntity, KeyPurpose purpose, CancellationToken cancellationToken)
    {
        if (_views.TryGetValue((legalEntity, purpose), out var cached) && Age(cached) <= _options.MaxStaleForWrite)
        {
            return cached;
        }

        return await ReloadForWriteAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RingView> ReloadForWriteAsync(LegalEntityId legalEntity, KeyPurpose purpose, CancellationToken cancellationToken)
    {
        try
        {
            return await ReloadAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new KeyRingStaleException("The key store could not be read; writes are refused until the key ring is fresh.", exception);
        }
    }

    private async ValueTask<RingView> ReloadAsync(LegalEntityId legalEntity, KeyPurpose purpose, CancellationToken cancellationToken)
    {
        var loadedAt = _time.GetUtcNow();
        var keys = await _store.ListAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
        var view = new RingView(loadedAt, keys);

        // Never replace a view with one loaded earlier (a slow background refresh racing a write reload).
        _views.AddOrUpdate((legalEntity, purpose), view, (_, existing) => existing.LoadedAt > view.LoadedAt ? existing : view);
        return view;
    }

    private async ValueTask CreateFirstVersionAsync(LegalEntityId legalEntity, KeyPurpose purpose, CancellationToken cancellationToken)
    {
        var gate = Gate(legalEntity, purpose);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var view = await ReloadForWriteAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
            if (ActiveOf(view) is not null)
            {
                return;
            }

            var version = view.Keys.Count == 0 ? 1 : view.Keys.Max(key => key.Version) + 1;
            try
            {
                await AddNewVersionAsync(legalEntity, purpose, version, cancellationToken).ConfigureAwait(false);
            }
            catch (DataKeyConflictException)
            {
                // Another replica created it first; the caller re-reads the ring.
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask AddNewVersionAsync(LegalEntityId legalEntity, KeyPurpose purpose, int version, CancellationToken cancellationToken)
    {
        var material = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            var wrapped = await _provider.WrapKeyAsync(new DataKeyContext(legalEntity, purpose, version), material, cancellationToken).ConfigureAwait(false);
            await _store.AddAsync(
                new WrappedDataKey(legalEntity, purpose, version, wrapped.KeyEncryptionKeyId, wrapped.WrappedKey, DataKeyStatus.Active, _time.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private async ValueTask<DataKey> UnwrapAsync(WrappedDataKey key, CancellationToken cancellationToken)
    {
        var id = (key.LegalEntity, key.Purpose, key.Version);
        if (_unwrapped.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var material = await _provider.UnwrapKeyAsync(
            new DataKeyContext(key.LegalEntity, key.Purpose, key.Version), key.KeyEncryptionKeyId, key.WrappedKey, cancellationToken).ConfigureAwait(false);
        if (material.Length != KeySize)
        {
            throw new CryptographicException("Unwrapped data key has the wrong size.");
        }

        return _unwrapped.GetOrAdd(id, new DataKey(key.LegalEntity, key.Purpose, key.Version, material));
    }

    private SemaphoreSlim Gate(LegalEntityId legalEntity, KeyPurpose purpose) =>
        _locks.GetOrAdd((legalEntity, purpose), _ => new SemaphoreSlim(1, 1));

    private sealed record RingView(DateTimeOffset LoadedAt, IReadOnlyList<WrappedDataKey> Keys);
}
