using System.Collections.Concurrent;
using Azure.Core;
using Azure.Security.KeyVault.Keys.Cryptography;

namespace CoreIns.Platform.DataProtection.Keys;

/// <summary>
/// Production key-encryption keys in Azure Key Vault (contract §3.9.12, D-ARC-14): one RSA key per legal entity, named
/// by <see cref="KeyName"/> (default <c>kek-{legalEntityId:N}</c>). Data keys are wrapped with RSA-OAEP-256 by the
/// latest key version; the versioned key id returned by Key Vault is stored with the wrapped key, so rotating the KEK
/// in Key Vault (new version) never breaks unwrapping of older data keys, and <see cref="KeyRing.RewrapAsync"/>
/// re-wraps them under the new version without touching the encrypted data.
/// The Host supplies the <see cref="TokenCredential"/> (managed identity); the identity needs only the
/// "Key Vault Crypto User" data-plane role (wrapKey / unwrapKey), never key management.
/// </summary>
public sealed class AzureKeyVaultKeyProvider : IKeyProvider
{
    private readonly Uri _vaultUri;
    private readonly TokenCredential _credential;
    private readonly ConcurrentDictionary<string, CryptographyClient> _clients = new(StringComparer.Ordinal);

    public AzureKeyVaultKeyProvider(Uri vaultUri, TokenCredential credential, Func<LegalEntityId, string>? keyName = null)
    {
        ArgumentNullException.ThrowIfNull(vaultUri);
        ArgumentNullException.ThrowIfNull(credential);
        _vaultUri = vaultUri;
        _credential = credential;
        KeyName = keyName ?? (legalEntity => $"kek-{legalEntity}");
    }

    /// <summary>Key Vault key name of a legal entity's KEK.</summary>
    public Func<LegalEntityId, string> KeyName { get; }

    /// <summary>Wrap algorithm (RSA-OAEP with SHA-256).</summary>
    public static KeyWrapAlgorithm Algorithm => KeyWrapAlgorithm.RsaOaep256;

    public async ValueTask<WrapResult> WrapKeyAsync(LegalEntityId legalEntity, ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken = default)
    {
        var versionlessId = new Uri(_vaultUri, $"keys/{KeyName(legalEntity)}").ToString();
        var result = await Client(versionlessId).WrapKeyAsync(Algorithm, dataKey.ToArray(), cancellationToken).ConfigureAwait(false);
        return new WrapResult(result.KeyId, result.EncryptedKey);
    }

    public async ValueTask<byte[]> UnwrapKeyAsync(
        LegalEntityId legalEntity, string keyEncryptionKeyId, ReadOnlyMemory<byte> wrappedKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyEncryptionKeyId);
        var expected = new Uri(_vaultUri, $"keys/{KeyName(legalEntity)}/").ToString();
        if (!keyEncryptionKeyId.StartsWith(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new System.Security.Cryptography.CryptographicException("The key-encryption key id does not belong to this legal entity.");
        }

        var result = await Client(keyEncryptionKeyId).UnwrapKeyAsync(Algorithm, wrappedKey.ToArray(), cancellationToken).ConfigureAwait(false);
        return result.Key;
    }

    private CryptographyClient Client(string keyId) =>
        _clients.GetOrAdd(keyId, id => new CryptographyClient(new Uri(id), _credential));
}
