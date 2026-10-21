using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CoreIns.Platform.DataProtection;
using CoreIns.Platform.DataProtection.Keys;

namespace CoreIns.Modules.Data.Domain;

/// <summary>
/// Wraps per-subject keys (crypto-shred keys) under the platform key-encryption key (<see cref="IKeyProvider"/>: Key Vault in
/// Azure, local HKDF provider in development), so the database never holds a usable subject key (NX-HD-13).
/// </summary>
/// <remarks>
/// Stored format of <c>dat.subject_key.key_material</c>:
/// <list type="bullet">
/// <item><b>Legacy</b>: the raw 32-byte key (length exactly 32). Still readable; <see cref="IsLegacy"/> lets writers re-wrap it.</item>
/// <item><b>Wrapped v1</b>: <c>0x53 0x01 | u16 kekIdLength | kekId (UTF-8) | wrappedKey</c>, always longer than 32 bytes.
/// The wrapped payload is <c>u16 tokenLength | subjectToken | key</c>, so a wrapped key cannot be replayed for another subject.</item>
/// </list>
/// Destroying the row's material (crypto-shred) is unchanged: the column is set to NULL. Key material and plaintext are never logged.
/// </remarks>
internal sealed class SubjectKeyProtector(IKeyProvider keyProvider)
{
    private const byte Magic = 0x53;
    private const byte Version = 1;
    private const int KeyLength = 32;

    // Pseudo legal entity under which platform-level (not entity-scoped) subject keys are wrapped.
    private static readonly DataKeyContext Context = new(new LegalEntityId(new Guid("00000000-0000-0000-0000-00000000da7a")), KeyPurpose.FieldEncryption, 1);

    /// <summary>True when <paramref name="stored"/> is a legacy raw key (not wrapped).</summary>
    public static bool IsLegacy(byte[] stored) => stored.Length == KeyLength;

    /// <summary>Wraps a new or legacy raw key for storage.</summary>
    public async ValueTask<byte[]> WrapAsync(string subjectToken, byte[] key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectToken);
        if (key.Length != KeyLength)
        {
            throw new ArgumentException("A subject key is 32 bytes.", nameof(key));
        }

        var tokenBytes = Encoding.UTF8.GetBytes(subjectToken);
        var payload = new byte[2 + tokenBytes.Length + KeyLength];
        BinaryPrimitives.WriteUInt16BigEndian(payload, checked((ushort)tokenBytes.Length));
        tokenBytes.CopyTo(payload, 2);
        key.CopyTo(payload, 2 + tokenBytes.Length);
        try
        {
            var result = await keyProvider.WrapKeyAsync(Context, payload, cancellationToken).ConfigureAwait(false);
            var kekId = Encoding.UTF8.GetBytes(result.KeyEncryptionKeyId);
            var output = new byte[4 + kekId.Length + result.WrappedKey.Length];
            output[0] = Magic;
            output[1] = Version;
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(2), checked((ushort)kekId.Length));
            kekId.CopyTo(output, 4);
            result.WrappedKey.Span.CopyTo(output.AsSpan(4 + kekId.Length));
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>Returns the raw key for either stored format; fails closed on anything malformed or bound to another subject.</summary>
    public async ValueTask<byte[]> UnwrapAsync(string subjectToken, byte[] stored, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectToken);
        if (IsLegacy(stored))
        {
            return stored;
        }

        if (stored.Length < 5 || stored[0] != Magic || stored[1] != Version)
        {
            throw new CryptographicException("The stored subject key has an unknown format.");
        }

        var kekLength = BinaryPrimitives.ReadUInt16BigEndian(stored.AsSpan(2));
        if (stored.Length <= 4 + kekLength)
        {
            throw new CryptographicException("The stored subject key is truncated.");
        }

        var kekId = Encoding.UTF8.GetString(stored, 4, kekLength);
        var payload = await keyProvider.UnwrapKeyAsync(Context, kekId, stored.AsMemory(4 + kekLength), cancellationToken).ConfigureAwait(false);
        try
        {
            var tokenBytes = Encoding.UTF8.GetBytes(subjectToken);
            if (payload.Length != 2 + tokenBytes.Length + KeyLength
                || BinaryPrimitives.ReadUInt16BigEndian(payload) != tokenBytes.Length
                || !CryptographicOperations.FixedTimeEquals(payload.AsSpan(2, tokenBytes.Length), tokenBytes))
            {
                throw new CryptographicException("The wrapped subject key belongs to another subject.");
            }

            return payload.AsSpan(2 + tokenBytes.Length).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }
}
