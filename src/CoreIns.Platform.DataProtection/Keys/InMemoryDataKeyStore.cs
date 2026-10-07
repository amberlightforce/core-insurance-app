using System.Collections.Concurrent;

namespace CoreIns.Platform.DataProtection.Keys;

/// <summary>
/// In-memory key ring store for tests and local development. Keys are lost on restart, so data encrypted with them
/// becomes unreadable: never bind it where data outlives the process (production uses a PLT-schema store).
/// </summary>
public sealed class InMemoryDataKeyStore : IDataKeyStore
{
    private readonly ConcurrentDictionary<(LegalEntityId, KeyPurpose, int), WrappedDataKey> _keys = new();

    public ValueTask<IReadOnlyList<WrappedDataKey>> ListAsync(LegalEntityId legalEntity, KeyPurpose purpose, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<WrappedDataKey> keys = _keys.Values
            .Where(key => key.LegalEntity == legalEntity && key.Purpose == purpose)
            .OrderBy(key => key.Version)
            .ToList();
        return ValueTask.FromResult(keys);
    }

    public ValueTask AddAsync(WrappedDataKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_keys.TryAdd((key.LegalEntity, key.Purpose, key.Version), key))
        {
            throw new DataKeyConflictException($"Data key version {key.Version} already exists.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateAsync(WrappedDataKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var id = (key.LegalEntity, key.Purpose, key.Version);
        if (!_keys.ContainsKey(id))
        {
            throw new KeyNotFoundException($"Data key version {key.Version} does not exist.");
        }

        _keys[id] = key;
        return ValueTask.CompletedTask;
    }
}
