using System.Globalization;
using System.Security.Cryptography;
using CoreIns.Platform.DataProtection.Keys;

namespace CoreIns.Platform.DataProtection.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>A local key provider, an in-memory store and the services over them.</summary>
internal sealed class TestKeys
{
    public TestKeys(TimeProvider? time = null, IDataKeyStore? store = null, LocalKeyProvider? provider = null)
    {
        Time = time ?? TimeProvider.System;
        Store = store ?? new InMemoryDataKeyStore();
        Provider = provider ?? LocalKeyProvider.CreateEphemeral();
        Ring = new KeyRing(Provider, Store, Time);
        Encryptor = new FieldEncryptor(Ring);
        Indexer = new BlindIndexer(Ring);
    }

    public TimeProvider Time { get; }

    public LocalKeyProvider Provider { get; }

    public IDataKeyStore Store { get; }

    public KeyRing Ring { get; }

    public FieldEncryptor Encryptor { get; }

    public BlindIndexer Indexer { get; }
}

/// <summary>Drives the two-step retirement with a manual clock.</summary>
internal static class Retirement
{
    public static TimeSpan Grace(KeyRing ring) => ring.Options.MaxStaleForWrite + ring.Options.RetirementMargin + TimeSpan.FromSeconds(1);

    public static async Task RetireAsync(KeyRing ring, ManualTimeProvider time, LegalEntityId legalEntity, KeyPurpose purpose, int version, CancellationToken ct)
    {
        time.Advance(Grace(ring));
        await ring.BeginRetirementAsync(legalEntity, purpose, version, new FixedScan(0), ct);
        time.Advance(Grace(ring));
        await ring.CompleteRetirementAsync(legalEntity, purpose, version, new FixedScan(0), ct);
    }
}

/// <summary>A key store that can be switched off (key-store outage).</summary>
internal sealed class FlakyStore(IDataKeyStore inner) : IDataKeyStore
{
    public bool Down { get; set; }

    public int Reads { get; private set; }

    public ValueTask<IReadOnlyList<WrappedDataKey>> ListAsync(LegalEntityId legalEntity, KeyPurpose purpose, CancellationToken cancellationToken = default)
    {
        Reads++;
        return Down ? throw new IOException("key store unavailable") : inner.ListAsync(legalEntity, purpose, cancellationToken);
    }

    public ValueTask AddAsync(WrappedDataKey key, CancellationToken cancellationToken = default) =>
        Down ? throw new IOException("key store unavailable") : inner.AddAsync(key, cancellationToken);

    public ValueTask UpdateAsync(WrappedDataKey key, CancellationToken cancellationToken = default) =>
        Down ? throw new IOException("key store unavailable") : inner.UpdateAsync(key, cancellationToken);
}

/// <summary>Retirement scan answering a fixed row count.</summary>
internal sealed class FixedScan(long rows) : IRetirementScan
{
    public long Rows { get; set; } = rows;

    public ValueTask<long> CountRowsUsingAsync(LegalEntityId legalEntity, KeyPurpose purpose, int version, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Rows);
}

/// <summary>
/// A fake Key Vault: keys with versions, "RSA" wrap simulated with AES-GCM under a per-version secret, and a counter of
/// "get current version" calls. <see cref="Rotate"/> adds a key version as Key Vault does.
/// </summary>
internal sealed class FakeKeyVault : IKeyVaultKeyOperations
{
    private const string Vault = "https://fake-vault.vault.azure.net";

    public static Uri VaultUri { get; } = new(Vault + "/");
    private readonly Dictionary<string, int> _latest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _secrets = new(StringComparer.Ordinal);

    public int GetCurrentCalls { get; private set; }

    public List<string> WrappedWith { get; } = [];

    public void Rotate(string keyName) => _latest[keyName] = Latest(keyName) + 1;

    public Task<string> GetCurrentKeyIdAsync(string keyName, CancellationToken cancellationToken)
    {
        GetCurrentCalls++;
        return Task.FromResult(KeyId(keyName, Latest(keyName)));
    }

    public Task<byte[]> WrapKeyAsync(string versionedKeyId, byte[] key, CancellationToken cancellationToken)
    {
        WrappedWith.Add(versionedKeyId);
        var output = new byte[12 + key.Length + 16];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var aes = new AesGcm(Secret(versionedKeyId), 16);
        aes.Encrypt(output.AsSpan(0, 12), key, output.AsSpan(12, key.Length), output.AsSpan(12 + key.Length));
        return Task.FromResult(output);
    }

    public Task<byte[]> UnwrapKeyAsync(string versionedKeyId, byte[] wrappedKey, CancellationToken cancellationToken)
    {
        var plain = new byte[wrappedKey.Length - 28];
        using var aes = new AesGcm(Secret(versionedKeyId), 16);
        aes.Decrypt(wrappedKey.AsSpan(0, 12), wrappedKey.AsSpan(12, plain.Length), wrappedKey.AsSpan(12 + plain.Length), plain);
        return Task.FromResult(plain);
    }

    private int Latest(string keyName) => _latest.TryGetValue(keyName, out var version) ? version : 1;

    private static string KeyId(string keyName, int version) =>
        string.Create(CultureInfo.InvariantCulture, $"{Vault}/keys/{keyName}/v{version:D4}");

    private byte[] Secret(string keyId)
    {
        if (!_secrets.TryGetValue(keyId, out var secret))
        {
            secret = RandomNumberGenerator.GetBytes(32);
            _secrets[keyId] = secret;
        }

        return secret;
    }
}
