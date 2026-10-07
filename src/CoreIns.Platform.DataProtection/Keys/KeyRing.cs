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

/// <summary>Key ring options.</summary>
public sealed class KeyRingOptions
{
    /// <summary>
    /// How long a replica trusts its cached view of the ring for <b>writes</b> before re-reading the store. A replica
    /// may therefore keep sealing with a demoted version for up to this long after a rotation elsewhere; retirement waits
    /// for it (D-ARC-23). Reads of an unknown version and search candidate sets always re-read the store.
    /// </summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Extra margin on top of <see cref="RefreshInterval"/> before a demoted version may be retired (clock skew, in-flight writes).</summary>
    public TimeSpan RetirementMargin { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Versioned data keys per legal entity and purpose (D-ARC-14, D-ARC-23; REQ-PTY-060; NFR-PTY-010 "key rotation
/// without downtime"). Data keys are generated here (256-bit, CSPRNG), stored only wrapped by the legal entity's KEK
/// (<see cref="IKeyProvider"/>), and cached unwrapped in memory.
/// <para><b>Rotation across replicas.</b> <see cref="RotateAsync"/> adds version n+1 as Active and demotes n to
/// DecryptOnly, persisting <c>DemotedAt</c>. Replicas pick the highest Active version; a replica whose cached view is
/// older may still write with n for up to <see cref="KeyRingOptions.RefreshInterval"/>, which is harmless because n stays
/// readable. Search candidates (<see cref="GetReadableAsync"/>) are always read from the store, so they include versions
/// newer than a replica's cached Active. <see cref="RetireAsync"/> refuses until <c>DemotedAt</c> + refresh interval +
/// margin has passed <b>and</b> the owners' re-scan (<see cref="IRetirementScan"/>) finds no row still using n.</para>
/// <para><b>KEK rotation</b> (new Key Vault key version) is <see cref="RewrapAsync"/>: data keys are re-wrapped under the
/// new KEK version, data is untouched, and an unchanged KEK id fails loudly.</para>
/// <para><b>Request threads.</b> The synchronous accessors used by EF Core converters never do I/O once the ring is warm
/// (<see cref="WarmUpAsync"/>, run at start-up by <see cref="KeyRingWarmUpService"/>): a stale view is refreshed in the
/// background. Only a cold ring or an unknown version blocks.</para>
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
    }

    public KeyRingOptions Options => _options;

    /// <summary>The Active version for new data (highest Active in this replica's view); creates version 1 on first use.</summary>
    public async ValueTask<DataKey> GetActiveAsync(LegalEntityId legalEntity, KeyPurpose purpose, CancellationToken cancellationToken = default)
    {
        var view = await ViewAsync(legalEntity, purpose, forceReload: false, cancellationToken).ConfigureAwait(false);
        var active = ActiveOf(view);
        if (active is null)
        {
            await CreateFirstVersionAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
            view = await ViewAsync(legalEntity, purpose, forceReload: true, cancellationToken).ConfigureAwait(false);
            active = ActiveOf(view) ?? throw new CryptographicException("No active data key after creation.");
        }

        return await UnwrapAsync(active, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A specific readable version (Active or DecryptOnly); re-reads the store when the version is unknown.</summary>
    public async ValueTask<DataKey> GetAsync(LegalEntityId legalEntity, KeyPurpose purpose, int version, CancellationToken cancellationToken = default)
    {
        if (_unwrapped.TryGetValue((legalEntity, purpose, version), out var cached))
        {
            return cached;
        }

        var view = await ViewAsync(legalEntity, purpose, forceReload: false, cancellationToken).ConfigureAwait(false);
        var key = view.Keys.FirstOrDefault(candidate => candidate.Version == version);
        if (key is null || key.Status == DataKeyStatus.Retired)
        {
            view = await ViewAsync(legalEntity, purpose, forceReload: true, cancellationToken).ConfigureAwait(false);
            key = view.Keys.FirstOrDefault(candidate => candidate.Version == version);
        }

        if (key is null || key.Status == DataKeyStatus.Retired)
        {
            throw new CryptographicException($"Data key version {version} is not readable.");
        }

        return await UnwrapAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// All readable versions, read from the store (never from a stale view), highest Active first then the others newest
    /// first: the blind-index search candidate set during a rotation.
    /// </summary>
    public async ValueTask<IReadOnlyList<DataKey>> GetReadableAsync(LegalEntityId legalEntity, KeyPurpose purpose, CancellationToken cancellationToken = default)
    {
        var view = await ViewAsync(legalEntity, purpose, forceReload: true, cancellationToken).ConfigureAwait(false);
        if (ActiveOf(view) is null)
        {
            await GetActiveAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
            view = await ViewAsync(legalEntity, purpose, forceReload: true, cancellationToken).ConfigureAwait(false);
        }

        var ordered = view.Keys
            .Where(key => key.Status != DataKeyStatus.Retired)
            .OrderByDescending(key => key.Status == DataKeyStatus.Active)
            .ThenByDescending(key => key.Version);
        var keys = new List<DataKey>();
        foreach (var key in ordered)
        {
            keys.Add(await UnwrapAsync(key, cancellationToken).ConfigureAwait(false));
        }

        return keys;
    }

    /// <summary>Loads and unwraps every readable key of the legal entity (start-up, so request threads never block).</summary>
    public async ValueTask WarmUpAsync(LegalEntityId legalEntity, CancellationToken cancellationToken = default)
    {
        foreach (var purpose in Enum.GetValues<KeyPurpose>())
        {
            await GetActiveAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
            await GetReadableAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Synchronous <see cref="GetActiveAsync"/> for EF Core value converters. With a cached Active key it never does I/O
    /// (a stale view is refreshed in the background); it blocks only on a cold ring.
    /// </summary>
    public DataKey GetActive(LegalEntityId legalEntity, KeyPurpose purpose)
    {
        if (_views.TryGetValue((legalEntity, purpose), out var view)
            && ActiveOf(view) is { } active && _unwrapped.TryGetValue((legalEntity, purpose, active.Version), out var key))
        {
            if (IsStale(view))
            {
                RefreshInBackground(legalEntity, purpose);
            }

            return key;
        }

        return GetActiveAsync(legalEntity, purpose).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Synchronous <see cref="GetAsync"/> for EF Core value converters; blocks only for a version not yet loaded.</summary>
    public DataKey Get(LegalEntityId legalEntity, KeyPurpose purpose, int version) =>
        _unwrapped.TryGetValue((legalEntity, purpose, version), out var key)
            ? key
            : GetAsync(legalEntity, purpose, version).AsTask().GetAwaiter().GetResult();

    /// <summary>Rotates the data key: adds version n+1 as Active and demotes the previous Active (persisting DemotedAt).</summary>
    /// <returns>The new version.</returns>
    public async ValueTask<int> RotateAsync(LegalEntityId legalEntity, KeyPurpose purpose, CancellationToken cancellationToken = default)
    {
        var gate = Gate(legalEntity, purpose);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var view = await ViewAsync(legalEntity, purpose, forceReload: true, cancellationToken).ConfigureAwait(false);
            var version = view.Keys.Count == 0 ? 1 : view.Keys.Max(key => key.Version) + 1;
            await AddNewVersionAsync(legalEntity, purpose, version, cancellationToken).ConfigureAwait(false);

            // The new version is Active before the old one is demoted: readers pick the highest Active version.
            var now = _time.GetUtcNow();
            foreach (var old in view.Keys.Where(key => key.Status == DataKeyStatus.Active))
            {
                await _store.UpdateAsync(old with { Status = DataKeyStatus.DecryptOnly, DemotedAt = now }, cancellationToken).ConfigureAwait(false);
            }

            await ViewAsync(legalEntity, purpose, forceReload: true, cancellationToken).ConfigureAwait(false);
            return version;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Retires a DecryptOnly version. Refused (InvalidOperationException) while the version is Active, before
    /// DemotedAt + <see cref="KeyRingOptions.RefreshInterval"/> + <see cref="KeyRingOptions.RetirementMargin"/> (stale
    /// replicas may still be writing with it), or while <paramref name="scan"/> still finds rows using it.
    /// </summary>
    public async ValueTask RetireAsync(
        LegalEntityId legalEntity, KeyPurpose purpose, int version, IRetirementScan scan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var view = await ViewAsync(legalEntity, purpose, forceReload: true, cancellationToken).ConfigureAwait(false);
        var key = view.Keys.FirstOrDefault(candidate => candidate.Version == version)
                  ?? throw new KeyNotFoundException($"Data key version {version} does not exist.");
        if (key.Status == DataKeyStatus.Active)
        {
            throw new InvalidOperationException("The Active data key cannot be retired; rotate first.");
        }

        if (key.Status == DataKeyStatus.Retired)
        {
            return;
        }

        var earliest = (key.DemotedAt ?? key.CreatedAt) + _options.RefreshInterval + _options.RetirementMargin;
        if (_time.GetUtcNow() < earliest)
        {
            throw new InvalidOperationException(
                $"Data key version {version} was demoted too recently; replicas may still write with it. Retire after {earliest:O}.");
        }

        var remaining = await scan.CountRowsUsingAsync(legalEntity, purpose, version, cancellationToken).ConfigureAwait(false);
        if (remaining > 0)
        {
            throw new InvalidOperationException($"{remaining} rows still use data key version {version}; re-encrypt / re-index them first.");
        }

        await _store.UpdateAsync(key with { Status = DataKeyStatus.Retired }, cancellationToken).ConfigureAwait(false);
        _unwrapped.TryRemove((legalEntity, purpose, version), out _);
        await ViewAsync(legalEntity, purpose, forceReload: true, cancellationToken).ConfigureAwait(false);
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
            var view = await ViewAsync(legalEntity, purpose, forceReload: true, cancellationToken).ConfigureAwait(false);
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

            await ViewAsync(legalEntity, purpose, forceReload: true, cancellationToken).ConfigureAwait(false);
        }

        if (total > 0 && count == 0)
        {
            throw new InvalidOperationException(
                "Re-wrap changed no data key: the key-encryption key version is unchanged (rotate the KEK in the key-management service first).");
        }

        return count;
    }

    private static WrappedDataKey? ActiveOf(RingView view) =>
        view.Keys.Where(key => key.Status == DataKeyStatus.Active).MaxBy(key => key.Version);

    private bool IsStale(RingView view) => _time.GetUtcNow() - view.LoadedAt > _options.RefreshInterval;

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
                await GetActiveAsync(legalEntity, purpose).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is CryptographicException or InvalidOperationException or IOException)
            {
                // The cached key stays in use; the next stale access tries again.
            }
            finally
            {
                _refreshing.TryRemove((legalEntity, purpose), out _);
            }
        });
    }

    private async ValueTask<RingView> ViewAsync(LegalEntityId legalEntity, KeyPurpose purpose, bool forceReload, CancellationToken cancellationToken)
    {
        if (!forceReload && _views.TryGetValue((legalEntity, purpose), out var cached) && !IsStale(cached))
        {
            return cached;
        }

        var keys = await _store.ListAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
        var view = new RingView(_time.GetUtcNow(), keys);
        _views[(legalEntity, purpose)] = view;
        return view;
    }

    private async ValueTask CreateFirstVersionAsync(LegalEntityId legalEntity, KeyPurpose purpose, CancellationToken cancellationToken)
    {
        var gate = Gate(legalEntity, purpose);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var view = await ViewAsync(legalEntity, purpose, forceReload: true, cancellationToken).ConfigureAwait(false);
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
