using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Azure.Core;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;

namespace CoreIns.Platform.DataProtection.Keys;

/// <summary>
/// The Key Vault operations the provider needs, behind an interface so KEK rotation is testable without a vault.
/// Key ids are always <b>versioned</b> kids (<c>https://{vault}/keys/{name}/{version}</c>).
/// </summary>
public interface IKeyVaultKeyOperations
{
    /// <summary>Versioned id of the current (latest enabled) version of key <paramref name="keyName"/>.</summary>
    Task<string> GetCurrentKeyIdAsync(string keyName, CancellationToken cancellationToken);

    /// <summary>Wraps with exactly the key version <paramref name="versionedKeyId"/>.</summary>
    Task<byte[]> WrapKeyAsync(string versionedKeyId, byte[] key, CancellationToken cancellationToken);

    /// <summary>Unwraps with exactly the key version <paramref name="versionedKeyId"/>.</summary>
    Task<byte[]> UnwrapKeyAsync(string versionedKeyId, byte[] wrappedKey, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IKeyVaultKeyOperations"/> over the Azure SDK: <see cref="KeyClient"/> resolves the current version, and a
/// <see cref="CryptographyClient"/> per <b>versioned</b> kid wraps/unwraps with RSA-OAEP-256. The identity needs only the
/// "Key Vault Crypto User" data-plane role (get, wrapKey, unwrapKey).
/// </summary>
public sealed class AzureKeyVaultKeyOperations : IKeyVaultKeyOperations
{
    private readonly KeyClient _keys;
    private readonly TokenCredential _credential;
    private readonly ConcurrentDictionary<string, CryptographyClient> _clients = new(StringComparer.Ordinal);

    public AzureKeyVaultKeyOperations(Uri vaultUri, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(vaultUri);
        ArgumentNullException.ThrowIfNull(credential);
        _keys = new KeyClient(vaultUri, credential);
        _credential = credential;
    }

    /// <summary>Wrap algorithm (RSA-OAEP with SHA-256).</summary>
    public static KeyWrapAlgorithm Algorithm => KeyWrapAlgorithm.RsaOaep256;

    public async Task<string> GetCurrentKeyIdAsync(string keyName, CancellationToken cancellationToken)
    {
        var key = await _keys.GetKeyAsync(keyName, cancellationToken: cancellationToken).ConfigureAwait(false);
        return key.Value.Id.ToString();
    }

    public async Task<byte[]> WrapKeyAsync(string versionedKeyId, byte[] key, CancellationToken cancellationToken)
    {
        var result = await Client(versionedKeyId).WrapKeyAsync(Algorithm, key, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(result.KeyId, versionedKeyId, StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException($"Key Vault wrapped with '{result.KeyId}' instead of '{versionedKeyId}'.");
        }

        return result.EncryptedKey;
    }

    public async Task<byte[]> UnwrapKeyAsync(string versionedKeyId, byte[] wrappedKey, CancellationToken cancellationToken)
    {
        var result = await Client(versionedKeyId).UnwrapKeyAsync(Algorithm, wrappedKey, cancellationToken).ConfigureAwait(false);
        return result.Key;
    }

    private CryptographyClient Client(string versionedKeyId) =>
        _clients.GetOrAdd(versionedKeyId, id => new CryptographyClient(new Uri(id), _credential));
}

/// <summary>
/// Production key-encryption keys in Azure Key Vault (contract §3.9.12, D-ARC-14, D-ARC-23): one RSA key per legal
/// entity, named by <see cref="KeyName"/> (default <c>kek-{legalEntityId:N}</c>).
/// <list type="bullet">
/// <item>Each wrap resolves the <b>current versioned kid</b> (cached for <see cref="CurrentVersionCacheDuration"/>,
/// dropped by <see cref="RefreshAsync"/>) and records that kid with the wrapped key, so a KEK rotation in Key Vault is
/// picked up by new wraps and by <see cref="KeyRing.RewrapAsync"/>, which fails loudly when the kid did not change.</item>
/// <item>RSA-OAEP has no associated data, so the <see cref="DataKeyContext"/> (legal entity, purpose, version) is wrapped
/// together with the key as a length-prefixed header and checked on unwrap: a wrapped key cannot be replayed under
/// another purpose, version or legal entity.</item>
/// </list>
/// </summary>
public sealed class AzureKeyVaultKeyProvider : IKeyProvider
{
    private readonly Uri _vaultUri;
    private readonly IKeyVaultKeyOperations _operations;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<LegalEntityId, (string KeyId, DateTimeOffset ResolvedAt)> _current = new();

    /// <summary>Provider over the Azure SDK.</summary>
    public AzureKeyVaultKeyProvider(Uri vaultUri, TokenCredential credential, Func<LegalEntityId, string>? keyName = null)
        : this(vaultUri, new AzureKeyVaultKeyOperations(vaultUri, credential), TimeProvider.System, keyName)
    {
    }

    /// <summary>Provider over any <see cref="IKeyVaultKeyOperations"/> (tests use a fake vault).</summary>
    /// <param name="vaultUri">The vault every key id must belong to (scheme https, same host and port).</param>
    /// <param name="operations">Key Vault operations.</param>
    /// <param name="time">Clock.</param>
    /// <param name="keyName">KEK name per legal entity.</param>
    /// <param name="currentVersionCacheDuration">Cache of the resolved current KEK version.</param>
    public AzureKeyVaultKeyProvider(
        Uri vaultUri, IKeyVaultKeyOperations operations, TimeProvider time, Func<LegalEntityId, string>? keyName = null,
        TimeSpan? currentVersionCacheDuration = null)
    {
        ArgumentNullException.ThrowIfNull(vaultUri);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(time);
        _vaultUri = vaultUri;
        _operations = operations;
        _time = time;
        KeyName = keyName ?? (legalEntity => $"kek-{legalEntity}");
        CurrentVersionCacheDuration = currentVersionCacheDuration ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>Key Vault key name of a legal entity's KEK.</summary>
    public Func<LegalEntityId, string> KeyName { get; }

    /// <summary>How long the resolved current KEK version is reused before Key Vault is asked again.</summary>
    public TimeSpan CurrentVersionCacheDuration { get; }

    public async ValueTask<WrapResult> WrapKeyAsync(DataKeyContext context, ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken = default)
    {
        var keyId = await CurrentKeyIdAsync(context.LegalEntity, cancellationToken).ConfigureAwait(false);
        var payload = BindPayload(context, dataKey.Span);
        try
        {
            var wrapped = await _operations.WrapKeyAsync(keyId, payload, cancellationToken).ConfigureAwait(false);
            return new WrapResult(keyId, wrapped);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public async ValueTask<byte[]> UnwrapKeyAsync(
        DataKeyContext context, string keyEncryptionKeyId, ReadOnlyMemory<byte> wrappedKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyEncryptionKeyId);
        var expectedPath = $"/keys/{KeyName(context.LegalEntity)}/";
        if (!Uri.TryCreate(keyEncryptionKeyId, UriKind.Absolute, out var kid)
            || kid.Scheme != Uri.UriSchemeHttps
            || !string.Equals(kid.Host, _vaultUri.Host, StringComparison.OrdinalIgnoreCase)
            || kid.Port != _vaultUri.Port
            || !kid.AbsolutePath.StartsWith(expectedPath, StringComparison.OrdinalIgnoreCase)
            || kid.AbsolutePath.Length <= expectedPath.Length)
        {
            throw new CryptographicException("The key-encryption key id is not a versioned key of this legal entity in the configured vault.");
        }

        var payload = await _operations.UnwrapKeyAsync(keyEncryptionKeyId, wrappedKey.ToArray(), cancellationToken).ConfigureAwait(false);
        try
        {
            return UnbindPayload(context, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public ValueTask RefreshAsync(LegalEntityId legalEntity, CancellationToken cancellationToken = default)
    {
        _current.TryRemove(legalEntity, out _);
        return ValueTask.CompletedTask;
    }

    private async ValueTask<string> CurrentKeyIdAsync(LegalEntityId legalEntity, CancellationToken cancellationToken)
    {
        if (_current.TryGetValue(legalEntity, out var cached) && _time.GetUtcNow() - cached.ResolvedAt < CurrentVersionCacheDuration)
        {
            return cached.KeyId;
        }

        var keyId = await _operations.GetCurrentKeyIdAsync(KeyName(legalEntity), cancellationToken).ConfigureAwait(false);
        _current[legalEntity] = (keyId, _time.GetUtcNow());
        return keyId;
    }

    private static byte[] BindPayload(DataKeyContext context, ReadOnlySpan<byte> dataKey)
    {
        var binding = context.ToBindingBytes();
        var payload = new byte[2 + binding.Length + dataKey.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)binding.Length);
        binding.CopyTo(payload, 2);
        dataKey.CopyTo(payload.AsSpan(2 + binding.Length));
        return payload;
    }

    private static byte[] UnbindPayload(DataKeyContext context, byte[] payload)
    {
        var binding = context.ToBindingBytes();
        if (payload.Length < 2
            || BinaryPrimitives.ReadUInt16BigEndian(payload) != binding.Length
            || payload.Length < 2 + binding.Length
            || !CryptographicOperations.FixedTimeEquals(payload.AsSpan(2, binding.Length), binding))
        {
            throw new CryptographicException("The wrapped key belongs to another legal entity, purpose or version.");
        }

        return payload.AsSpan(2 + binding.Length).ToArray();
    }
}
