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
/// <para>Associated data = bytes [0..20] ‖ len32 ‖ UTF-8 field context (for example <c>pty.party_identifier.value</c>;
/// control characters rejected) ‖ len32 ‖ UTF-8 row key (empty when none), lengths big-endian. A ciphertext therefore cannot be moved to another legal entity, key version or
/// column. Moving it to another <b>row of the same column</b> is detected only when the caller supplies a row key (for
/// example the row's UUID) on both encrypt and decrypt; the EF Core converters cannot see the row and pass none, so for
/// them row binding is not provided.</para>
/// <para>Random nonces keep a key well below the 2^32-message bound because keys rotate (NFR-PTY-010).</para>
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

    /// <summary>Encrypts a UTF-8 string, optionally bound to a row key.</summary>
    public async ValueTask<byte[]> EncryptAsync(
        LegalEntityId legalEntity, string fieldContext, string plaintext, string? rowKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var key = await keyRing.GetActiveAsync(legalEntity, KeyPurpose.FieldEncryption, cancellationToken).ConfigureAwait(false);
        return Seal(key, fieldContext, rowKey, Encoding.UTF8.GetBytes(plaintext));
    }

    /// <summary>Decrypts an envelope produced by <see cref="EncryptAsync"/> (same field context and row key).</summary>
    /// <exception cref="FieldDecryptionException">Malformed or tampered envelope, or wrong field context / row key.</exception>
    public async ValueTask<string> DecryptAsync(
        ReadOnlyMemory<byte> envelope, string fieldContext, string? rowKey = null, CancellationToken cancellationToken = default)
    {
        var header = ReadHeader(envelope.Span);
        var key = await keyRing.GetAsync(header.LegalEntity, KeyPurpose.FieldEncryption, header.KeyVersion, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(Open(key, fieldContext, rowKey, envelope.Span));
    }

    /// <summary>Synchronous <see cref="EncryptAsync"/> (EF Core value converters).</summary>
    public byte[] Encrypt(LegalEntityId legalEntity, string fieldContext, string plaintext, string? rowKey)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Seal(keyRing.GetActive(legalEntity, KeyPurpose.FieldEncryption), fieldContext, rowKey, Encoding.UTF8.GetBytes(plaintext));
    }

    /// <summary>Synchronous <see cref="DecryptAsync"/>.</summary>
    public string Decrypt(byte[] envelope, string fieldContext, string? rowKey)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var header = ReadHeader(envelope);
        var key = keyRing.Get(header.LegalEntity, KeyPurpose.FieldEncryption, header.KeyVersion);
        return Encoding.UTF8.GetString(Open(key, fieldContext, rowKey, envelope));
    }

    /// <summary>
    /// Synchronous decrypt that also requires the envelope to belong to <paramref name="expected"/> (the unit of work's
    /// legal entity): a row of another legal entity is never decrypted under the current one (review F-1e m6).
    /// </summary>
    /// <exception cref="FieldDecryptionException">The envelope belongs to another legal entity, or fails authentication.</exception>
    public string DecryptForLegalEntity(byte[] envelope, string fieldContext, LegalEntityId expected, string? rowKey)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var header = ReadHeader(envelope);
        if (header.LegalEntity != expected)
        {
            throw new FieldDecryptionException("The value belongs to another legal entity than the current unit of work.");
        }

        return Decrypt(envelope, fieldContext, rowKey);
    }

    /// <summary>True when the envelope was sealed with a version other than this replica's Active one (re-encryption job).</summary>
    public async ValueTask<bool> NeedsReEncryptionAsync(ReadOnlyMemory<byte> envelope, CancellationToken cancellationToken = default)
    {
        var header = ReadHeader(envelope.Span);
        var active = await keyRing.GetActiveAsync(header.LegalEntity, KeyPurpose.FieldEncryption, cancellationToken).ConfigureAwait(false);
        return header.KeyVersion != active.Version;
    }

    /// <summary>Decrypts and re-encrypts under the Active version (rotation without downtime, NFR-PTY-010).</summary>
    public async ValueTask<byte[]> ReEncryptAsync(
        ReadOnlyMemory<byte> envelope, string fieldContext, string? rowKey = null, CancellationToken cancellationToken = default)
    {
        var header = ReadHeader(envelope.Span);
        var plaintext = await DecryptAsync(envelope, fieldContext, rowKey, cancellationToken).ConfigureAwait(false);
        return await EncryptAsync(header.LegalEntity, fieldContext, plaintext, rowKey, cancellationToken).ConfigureAwait(false);
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

    private static byte[] Seal(DataKey key, string fieldContext, string? rowKey, byte[] plaintext)
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
            AssociatedData(span[..HeaderSize], fieldContext, rowKey));
        CryptographicOperations.ZeroMemory(plaintext);
        return envelope;
    }

    private static byte[] Open(DataKey key, string fieldContext, string? rowKey, ReadOnlySpan<byte> envelope)
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
                AssociatedData(envelope[..HeaderSize], fieldContext, rowKey));
        }
        catch (AuthenticationTagMismatchException exception)
        {
            throw new FieldDecryptionException("The envelope failed authentication (tampered, or wrong field context or row key).", exception);
        }

        return plaintext;
    }

    /// <summary>
    /// header ‖ len32(fieldContext) ‖ fieldContext ‖ len32(rowKey) ‖ rowKey (UTF-8, big-endian lengths): the encoding is
    /// injective, so no (field context, row key) pair can be re-split into another (review N1).
    /// </summary>
    private static byte[] AssociatedData(ReadOnlySpan<byte> header, string fieldContext, string? rowKey)
    {
        if (fieldContext.Any(char.IsControl))
        {
            throw new ArgumentException("The field context must not contain control characters.", nameof(fieldContext));
        }

        var context = Encoding.UTF8.GetBytes(fieldContext);
        var row = Encoding.UTF8.GetBytes(rowKey ?? string.Empty);
        var data = new byte[header.Length + 4 + context.Length + 4 + row.Length];
        var span = data.AsSpan();
        header.CopyTo(span);
        span = span[header.Length..];
        BinaryPrimitives.WriteInt32BigEndian(span, context.Length);
        context.CopyTo(span[4..]);
        span = span[(4 + context.Length)..];
        BinaryPrimitives.WriteInt32BigEndian(span, row.Length);
        row.CopyTo(span[4..]);
        return data;
    }
}

/// <summary>Clear header of an envelope.</summary>
/// <param name="LegalEntity">Legal entity whose key sealed it.</param>
/// <param name="KeyVersion">Data-key version.</param>
public readonly record struct EnvelopeHeader(LegalEntityId LegalEntity, int KeyVersion);

/// <summary>An envelope could not be decrypted: malformed, tampered, opened with the wrong field context or row key, or of another legal entity.</summary>
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
