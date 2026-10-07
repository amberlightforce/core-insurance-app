using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CoreIns.Platform.DataProtection.Keys;

/// <summary>
/// Development and test key-encryption keys: one KEK per legal entity and KEK version, derived with HKDF-SHA256 from a
/// local master secret, wrapping data keys with AES-256-GCM (authenticated data: KEK id and the
/// <see cref="DataKeyContext"/>). Not for production — the Host binds <see cref="AzureKeyVaultKeyProvider"/> there
/// (contract §3.9.12: keys in the platform key-management service) and refuses this provider outside development and CI.
/// </summary>
public sealed class LocalKeyProvider : IKeyProvider
{
    private const string IdPrefix = "local:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _masterKey;

    /// <param name="masterKey">32-byte master secret (synthetic; never a production secret).</param>
    /// <param name="currentVersion">KEK version used for new wraps; raise it to simulate KEK rotation.</param>
    public LocalKeyProvider(ReadOnlySpan<byte> masterKey, int currentVersion = 1)
    {
        if (masterKey.Length != 32)
        {
            throw new ArgumentException("The local master key must be 32 bytes.", nameof(masterKey));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(currentVersion, 1);
        _masterKey = masterKey.ToArray();
        CurrentVersion = currentVersion;
    }

    /// <summary>KEK version used for new wraps.</summary>
    public int CurrentVersion { get; set; }

    /// <summary>A provider with a random master key (tests).</summary>
    public static LocalKeyProvider CreateEphemeral() => new(RandomNumberGenerator.GetBytes(32));

    public ValueTask<WrapResult> WrapKeyAsync(DataKeyContext context, ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken = default)
    {
        var keyId = KeyId(context.LegalEntity, CurrentVersion);
        var kek = DeriveKek(context.LegalEntity, CurrentVersion);
        var output = new byte[NonceSize + dataKey.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        try
        {
            using var aes = new AesGcm(kek, TagSize);
            aes.Encrypt(nonce, dataKey.Span, output.AsSpan(NonceSize, dataKey.Length), output.AsSpan(NonceSize + dataKey.Length), AssociatedData(keyId, context));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }

        return ValueTask.FromResult(new WrapResult(keyId, output));
    }

    public ValueTask<byte[]> UnwrapKeyAsync(
        DataKeyContext context, string keyEncryptionKeyId, ReadOnlyMemory<byte> wrappedKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyEncryptionKeyId);
        var version = ParseVersion(context.LegalEntity, keyEncryptionKeyId);
        if (wrappedKey.Length <= NonceSize + TagSize)
        {
            throw new CryptographicException("Wrapped key is too short.");
        }

        var kek = DeriveKek(context.LegalEntity, version);
        var input = wrappedKey.Span;
        var plain = new byte[input.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(kek, TagSize);
            aes.Decrypt(
                input[..NonceSize], input.Slice(NonceSize, plain.Length), input[(NonceSize + plain.Length)..], plain,
                AssociatedData(keyEncryptionKeyId, context));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }

        return ValueTask.FromResult(plain);
    }

    private static byte[] AssociatedData(string keyId, DataKeyContext context)
    {
        var id = Encoding.UTF8.GetBytes(keyId);
        var binding = context.ToBindingBytes();
        var data = new byte[id.Length + 1 + binding.Length];
        id.CopyTo(data, 0);
        data[id.Length] = 0x1F;
        binding.CopyTo(data, id.Length + 1);
        return data;
    }

    private static string KeyId(LegalEntityId legalEntity, int version) =>
        string.Create(CultureInfo.InvariantCulture, $"{IdPrefix}{legalEntity}:v{version}");

    private static int ParseVersion(LegalEntityId legalEntity, string keyId)
    {
        var expectedPrefix = string.Create(CultureInfo.InvariantCulture, $"{IdPrefix}{legalEntity}:v");
        if (!keyId.StartsWith(expectedPrefix, StringComparison.Ordinal)
            || !int.TryParse(keyId.AsSpan(expectedPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            throw new CryptographicException("The key-encryption key id does not belong to this legal entity.");
        }

        return version;
    }

    private byte[] DeriveKek(LegalEntityId legalEntity, int version) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            _masterKey,
            32,
            salt: [],
            info: Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"coreins/kek/{legalEntity}/v{version}")));
}
