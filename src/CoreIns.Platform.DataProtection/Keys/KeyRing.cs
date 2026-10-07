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
    /// How long a replica trusts its cached view of the ring before re-reading the store, so a rotation done by another
    /// replica is picked up. Decryption with an unknown version always re-reads immediately.
    /// </summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Versioned data keys per legal entity and purpose (D-ARC-14; REQ-PTY-060; NFR-PTY-010 "key rotation without
/// downtime"). Data keys are generated here (256-bit, CSPRNG), stored only wrapped by the legal entity's KEK
/// (<see cref="IKeyProvider"/>), and cached unwrapped in memory.
/// <para>Rotation: <see cref="RotateAsync"/> adds version n+1 as Active and demotes n to DecryptOnly. New data uses n+1
/// at once; old data stays readable (and blind-index searches cover every readable version) while a background job
/// re-encrypts / re-indexes; <see cref="RetireAsync"/> then retires n. KEK rotation (a new Key Vault key version) is
/// <see cref="RewrapAsync"/>: data keys are re-wrapped, data is untouched.</para>
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

    /// <summary>The Active version for new data; creates version 1 on first use.</summary>
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
        if (key is null)
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

    /// <summary>All readable versions, Active first (blind-index search candidates during a rotation).</summary>
    public async ValueTask<IReadOnlyList<DataKey>> GetReadableAsync(LegalEntityId legalEntity, KeyPurpose purpose, CancellationToken cancellationToken = default)
    {
        var active = await GetActiveAsync(legalEntity, purpose, cancellationToken).ConfigureAwait(false);
        var view = await ViewAsync(legalEntity, purpose, forceReload: false, cancellationToken).ConfigureAwait(false);
        var keys = new List<DataKey> { active };
        foreach (var key in view.Keys.Where(key => key.Status != DataKeyStatus.Retired && key.Version != active.Version).OrderByDescending(key => key.Version))
        {
            keys.Add(await UnwrapAsync(key, cancellationToken).ConfigureAwait(false));
        }

        return keys;
    }

    /// <summary>Synchronous <see cref="GetActiveAsync"/> for EF Core value converters; blocks only on a cold cache.</summary>
    public DataKey GetActive(LegalEntityId legalEntity, KeyPurpose purpose)
    {
        if (_views.TryGetValue((legalEntity, purpose), out var view) && !IsStale(view)
            && ActiveOf(view) is { } active && _unwrapped.TryGetValue((legalEntity, purpose, active.Version), out var key))
        {
            return key;
        }

        return GetActiveAsync(legalEntity, purpose).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Synchronous <see cref="GetAsync"/> for EF Core value converters; blocks only on a cold cache.</summary>
    public DataKey Get(LegalEntityId legalEntity, KeyPurpose purpose, int version) =>
        _unwrapped.TryGetValue((legalEntity, purpose, version), out var key)
            ? key
            : GetAsync(legalEntity, purpose, version).AsTask().GetAwaiter().GetResult();

    /// <summary>Rotates the data key: adds version n+1 as Active and demotes the previous Active to DecryptOnly.</summary>
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
            foreach (var old in view.Keys.Where(key => key.Status == DataKeyStatus.Active))
            {
                await _store.UpdateAsync(old with { Status = DataKeyStatus.DecryptOnly }, cancellationToken).ConfigureAwait(false);
            }

            await ViewAsync(legalEntity, purpose, forceReload: true, cancellationToken).ConfigureAwait(false);
            return version;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Retires a DecryptOnly version once no data uses it (after re-encryption / re-indexing).</summary>
    public async ValueTask RetireAsync(LegalEntityId legalEntity, KeyPurpose purpose, int version, CancellationToken cancellationToken = default)
    {
        var view = await ViewAsync(legalEntity, purpose, forceReload: true, cancellationToken).ConfigureAwait(false);
        var key = view.Keys.FirstOrDefault(candidate => candidate.Version == version)
                  ?? throw new KeyNotFoundException($"Data key version {version} does not exist.");
        if (key.Status == DataKeyStatus.Active)
        {
            throw new InvalidOperationException("The Active data key cannot be retired; rotate first.");
        }

        await _store.UpdateAsync(key with { Status = DataKeyStatus.Retired }, cancellationToken).ConfigureAwait(false);
        _unwrapped.TryRemove((legalEntity, purpose, version), out _);
        await ViewAsync(legalEntity, purpose, forceReload: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// KEK rotation: re-wraps every non-retired data key of the legal entity under the provider's current KEK version.
    /// Encrypted data and blind indexes are unchanged.
    /// </summary>
    /// <returns>Number of data keys re-wrapped.</returns>
    public async ValueTask<int> RewrapAsync(LegalEntityId legalEntity, CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var purpose in Enum.GetValues<KeyPurpose>())
        {
            var view = await ViewAsync(legalEntity, purpose, forceReload: true, cancellationToken).ConfigureAwait(false);
            foreach (var key in view.Keys.Where(key => key.Status != DataKeyStatus.Retired))
            {
                var material = await _provider.UnwrapKeyAsync(legalEntity, key.KeyEncryptionKeyId, key.WrappedKey, cancellationToken).ConfigureAwait(false);
                try
                {
                    var wrapped = await _provider.WrapKeyAsync(legalEntity, material, cancellationToken).ConfigureAwait(false);
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

        return count;
    }

    private static WrappedDataKey? ActiveOf(RingView view) =>
        view.Keys.Where(key => key.Status == DataKeyStatus.Active).MaxBy(key => key.Version);

    private bool IsStale(RingView view) => _time.GetUtcNow() - view.LoadedAt > _options.RefreshInterval;

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
            var wrapped = await _provider.WrapKeyAsync(legalEntity, material, cancellationToken).ConfigureAwait(false);
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

        var material = await _provider.UnwrapKeyAsync(key.LegalEntity, key.KeyEncryptionKeyId, key.WrappedKey, cancellationToken).ConfigureAwait(false);
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
