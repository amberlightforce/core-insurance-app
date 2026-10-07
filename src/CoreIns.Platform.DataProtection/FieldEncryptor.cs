using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CoreIns.Platform.DataProtection.Keys;

namespace CoreIns.Platform.DataProtection;

/// <summary>
/// Field-level envelope encryption of P2/P3 identifiers and IBANs (contract §3.9.12, D-ARC-14, REQ-PTY-060,
/// NFR-PTY-010) with AES-256-GCM (System.Security.Cryptography) under the legal entity's Active data key.
/// </summary>
/// <remarks>
/// <para>Envelope layout (version 1), stored as <c>bytea</c>:</para>
/// <code>
/// [0]       format version (0x01)
/// [1..16]   legal entity id (UUID, big-endian)
/// [17..20]  data-key version (int32, big-endian)
/// [21..32]  nonce (96 bits, CSPRNG)
/// [33..n-16] ciphertext
/// [n-16..n] GCM tag (128 bits)
/// </code>
/// <para>The associated data is bytes [0..20] plus the UTF-8 field context (for example
/// <c>pty.party_identifier.value</c>), so a ciphertext cannot be moved to another legal entity, key version or column
/// without failing authentication. Decryption needs no context besides the field: the envelope names its key.
/// Random nonces keep a key well below the 2^32-message bound when keys rotate (NFR-PTY-010).</para>
/// </remarks>
public sealed class FieldEncryptor(KeyRing keyRing)
{
    /// <summary>Envelope format version written by this class.</summary>
    public const byte FormatVersion = 1;

    private const int HeaderSize = 1 + 16 + 4;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>Bytes added to the plaintext by the envelope.</summary>
    public const int Overhead = HeaderSize + NonceSize + TagSize;

    /// <summary>Encrypts a UTF-8 string.</summary>
    public async ValueTask<byte[]> EncryptAsync(LegalEntityId legalEntity, string fieldContext, string plaintext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var key = await keyRing.GetActiveAsync(legalEntity, KeyPurpose.FieldEncryption, cancellationToken).ConfigureAwait(false);
        return Seal(key, fieldContext, Encoding.UTF8.GetBytes(plaintext));
    }

    /// <summary>Decrypts an envelope produced by <see cref="EncryptAsync"/>.</summary>
    /// <exception cref="FieldDecryptionException">Malformed or tampered envelope, or wrong field context.</exception>
    public async ValueTask<string> DecryptAsync(ReadOnlyMemory<byte> envelope, string fieldContext, CancellationToken cancellationToken = default)
    {
        var header = ReadHeader(envelope.Span);
        var key = await keyRing.GetAsync(header.LegalEntity, KeyPurpose.FieldEncryption, header.KeyVersion, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(Open(key, fieldContext, envelope.Span));
    }

    /// <summary>Synchronous <see cref="EncryptAsync"/> (EF Core value converters).</summary>
    public byte[] Encrypt(LegalEntityId legalEntity, string fieldContext, string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Seal(keyRing.GetActive(legalEntity, KeyPurpose.FieldEncryption), fieldContext, Encoding.UTF8.GetBytes(plaintext));
    }

    /// <summary>Synchronous <see cref="DecryptAsync"/> (EF Core value converters).</summary>
    public string Decrypt(byte[] envelope, string fieldContext)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var header = ReadHeader(envelope);
        var key = keyRing.Get(header.LegalEntity, KeyPurpose.FieldEncryption, header.KeyVersion);
        return Encoding.UTF8.GetString(Open(key, fieldContext, envelope));
    }

    /// <summary>True when the envelope was sealed with a version older than the Active one (re-encryption job).</summary>
    public async ValueTask<bool> NeedsReEncryptionAsync(ReadOnlyMemory<byte> envelope, CancellationToken cancellationToken = default)
    {
        var header = ReadHeader(envelope.Span);
        var active = await keyRing.GetActiveAsync(header.LegalEntity, KeyPurpose.FieldEncryption, cancellationToken).ConfigureAwait(false);
        return header.KeyVersion != active.Version;
    }

    /// <summary>Decrypts and re-encrypts under the Active version (rotation without downtime, NFR-PTY-010).</summary>
    public async ValueTask<byte[]> ReEncryptAsync(ReadOnlyMemory<byte> envelope, string fieldContext, CancellationToken cancellationToken = default)
    {
        var header = ReadHeader(envelope.Span);
        var plaintext = await DecryptAsync(envelope, fieldContext, cancellationToken).ConfigureAwait(false);
        return await EncryptAsync(header.LegalEntity, fieldContext, plaintext, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the clear header of an envelope.</summary>
    public static EnvelopeHeader ReadHeader(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length < Overhead || envelope[0] != FormatVersion)
        {
            throw new FieldDecryptionException("Not a field-encryption envelope of a supported format.");
        }

        var legalEntity = new LegalEntityId(new Guid(envelope.Slice(1, 16), bigEndian: true));
        var version = BinaryPrimitives.ReadInt32BigEndian(envelope.Slice(17, 4));
        return new EnvelopeHeader(legalEntity, version);
    }

    private static byte[] Seal(DataKey key, string fieldContext, byte[] plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldContext);
        var envelope = new byte[Overhead + plaintext.Length];
        var span = envelope.AsSpan();
        span[0] = FormatVersion;
        key.LegalEntity.Value.TryWriteBytes(span.Slice(1, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(17, 4), key.Version);
        var nonce = span.Slice(HeaderSize, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key.Material, TagSize);
        aes.Encrypt(
            nonce,
            plaintext,
            span.Slice(HeaderSize + NonceSize, plaintext.Length),
            span[(HeaderSize + NonceSize + plaintext.Length)..],
            AssociatedData(span[..HeaderSize], fieldContext));
        CryptographicOperations.ZeroMemory(plaintext);
        return envelope;
    }

    private static byte[] Open(DataKey key, string fieldContext, ReadOnlySpan<byte> envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldContext);
        var cipherLength = envelope.Length - Overhead;
        var plaintext = new byte[cipherLength];
        try
        {
            using var aes = new AesGcm(key.Material, TagSize);
            aes.Decrypt(
                envelope.Slice(HeaderSize, NonceSize),
                envelope.Slice(HeaderSize + NonceSize, cipherLength),
                envelope[(HeaderSize + NonceSize + cipherLength)..],
                plaintext,
                AssociatedData(envelope[..HeaderSize], fieldContext));
        }
        catch (AuthenticationTagMismatchException exception)
        {
            throw new FieldDecryptionException("The envelope failed authentication (tampered, or wrong field context).", exception);
        }

        return plaintext;
    }

    private static byte[] AssociatedData(ReadOnlySpan<byte> header, string fieldContext)
    {
        var context = Encoding.UTF8.GetBytes(fieldContext);
        var data = new byte[header.Length + context.Length];
        header.CopyTo(data);
        context.CopyTo(data.AsSpan(header.Length));
        return data;
    }
}

/// <summary>Clear header of an envelope.</summary>
/// <param name="LegalEntity">Legal entity whose key sealed it.</param>
/// <param name="KeyVersion">Data-key version.</param>
public readonly record struct EnvelopeHeader(LegalEntityId LegalEntity, int KeyVersion);

/// <summary>An envelope could not be decrypted: malformed, tampered, or opened with the wrong field context.</summary>
public sealed class FieldDecryptionException : CryptographicException
{
    public FieldDecryptionException()
    {
    }

    public FieldDecryptionException(string message)
        : base(message)
    {
    }

    public FieldDecryptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
