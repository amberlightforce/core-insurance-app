namespace CoreIns.Platform.DataProtection.Keys;

/// <summary>
/// What a data key is used for. Separate keys per purpose: a key is never used for two algorithms (AES-GCM encryption
/// and HMAC blind indexes).
/// </summary>
public enum KeyPurpose
{
    FieldEncryption = 1,
    BlindIndex = 2,
}

/// <summary>Life-cycle state of a data-key version (rotation without downtime, NFR-PTY-010).</summary>
public enum DataKeyStatus
{
    /// <summary>Used for new encryptions / new index values. Exactly one per legal entity and purpose.</summary>
    Active = 1,

    /// <summary>Superseded: still decrypts and still answers searches while data is re-encrypted / re-indexed.</summary>
    DecryptOnly = 2,

    /// <summary>No data uses it any more; it is kept only for audit and is never unwrapped.</summary>
    Retired = 3,
}

/// <summary>
/// A data-encryption key version as persisted: only its wrapped form (encrypted under the legal entity's
/// key-encryption key) is ever stored.
/// </summary>
/// <param name="LegalEntity">Owning legal entity.</param>
/// <param name="Purpose">Key purpose.</param>
/// <param name="Version">Version, 1, 2, … per legal entity and purpose.</param>
/// <param name="KeyEncryptionKeyId">Id of the key-encryption key version that wrapped it (Key Vault key id).</param>
/// <param name="WrappedKey">The wrapped key bytes.</param>
/// <param name="Status">Life-cycle state.</param>
/// <param name="CreatedAt">When the version was created.</param>
public sealed record WrappedDataKey(
    LegalEntityId LegalEntity,
    KeyPurpose Purpose,
    int Version,
    string KeyEncryptionKeyId,
    ReadOnlyMemory<byte> WrappedKey,
    DataKeyStatus Status,
    DateTimeOffset CreatedAt);

/// <summary>Result of wrapping a data key under a key-encryption key.</summary>
/// <param name="KeyEncryptionKeyId">Id of the key-encryption key version used (needed to unwrap).</param>
/// <param name="WrappedKey">The wrapped key.</param>
public sealed record WrapResult(string KeyEncryptionKeyId, ReadOnlyMemory<byte> WrappedKey);

/// <summary>
/// Key-encryption keys (KEK), one per legal entity, held in the platform key-management service (contract §3.9.12):
/// Azure Key Vault in production (<see cref="AzureKeyVaultKeyProvider"/>), <see cref="LocalKeyProvider"/> for development
/// and tests. The KEK never leaves the provider; only data keys are wrapped and unwrapped.
/// </summary>
public interface IKeyProvider
{
    /// <summary>Wraps <paramref name="dataKey"/> with the current KEK version of <paramref name="legalEntity"/>.</summary>
    ValueTask<WrapResult> WrapKeyAsync(LegalEntityId legalEntity, ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken = default);

    /// <summary>Unwraps a data key wrapped by KEK version <paramref name="keyEncryptionKeyId"/>.</summary>
    ValueTask<byte[]> UnwrapKeyAsync(
        LegalEntityId legalEntity, string keyEncryptionKeyId, ReadOnlyMemory<byte> wrappedKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Persistence of wrapped data keys (the key ring). Production stores it in the PLT schema; versions are unique per
/// (legal entity, purpose, version) so concurrent creators on several replicas cannot both win.
/// </summary>
public interface IDataKeyStore
{
    /// <summary>All versions of the key ring of (<paramref name="legalEntity"/>, <paramref name="purpose"/>), any order.</summary>
    ValueTask<IReadOnlyList<WrappedDataKey>> ListAsync(LegalEntityId legalEntity, KeyPurpose purpose, CancellationToken cancellationToken = default);

    /// <summary>Adds a new version; throws <see cref="DataKeyConflictException"/> when the version already exists.</summary>
    ValueTask AddAsync(WrappedDataKey key, CancellationToken cancellationToken = default);

    /// <summary>Replaces an existing version (status change, or re-wrap under a new KEK version).</summary>
    ValueTask UpdateAsync(WrappedDataKey key, CancellationToken cancellationToken = default);
}

/// <summary>A data-key version already exists (another replica created it first).</summary>
public sealed class DataKeyConflictException : Exception
{
    public DataKeyConflictException()
    {
    }

    public DataKeyConflictException(string message)
        : base(message)
    {
    }

    public DataKeyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
