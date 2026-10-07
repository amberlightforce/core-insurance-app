using System.Globalization;
using System.Text;

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

/// <summary>Life-cycle state of a data-key version (rotation without downtime, NFR-PTY-010, D-ARC-23).</summary>
public enum DataKeyStatus
{
    /// <summary>Used for new encryptions / new index values. The highest Active version wins.</summary>
    Active = 1,

    /// <summary>Superseded: still decrypts and still answers searches while data is re-encrypted / re-indexed.</summary>
    DecryptOnly = 2,

    /// <summary>No data uses it any more; it is kept only for audit and is never unwrapped.</summary>
    Retired = 3,

    /// <summary>
    /// Retirement in progress: the first re-scan found no rows; still decrypts and still answers searches, never
    /// written; becomes Retired only after a second re-scan at least the write-staleness bound later (D-ARC-23).
    /// </summary>
    Retiring = 4,
}

/// <summary>
/// A data-encryption key version as persisted: only its wrapped form (encrypted under the legal entity's
/// key-encryption key) is ever stored.
/// </summary>
/// <param name="LegalEntity">Owning legal entity.</param>
/// <param name="Purpose">Key purpose.</param>
/// <param name="Version">Version, 1, 2, … per legal entity and purpose.</param>
/// <param name="KeyEncryptionKeyId">Versioned id of the key-encryption key that wrapped it (Key Vault kid).</param>
/// <param name="WrappedKey">The wrapped key bytes.</param>
/// <param name="Status">Life-cycle state.</param>
/// <param name="CreatedAt">When the version was created.</param>
/// <param name="DemotedAt">
/// When the version stopped being Active (persisted so every replica can tell when its write view, at most
/// <see cref="KeyRingOptions.MaxStaleForWrite"/> old, can no longer pick it for new data; D-ARC-23).
/// </param>
/// <param name="RetiringAt">When the version entered <see cref="DataKeyStatus.Retiring"/> (first clean re-scan).</param>
public sealed record WrappedDataKey(
    LegalEntityId LegalEntity,
    KeyPurpose Purpose,
    int Version,
    string KeyEncryptionKeyId,
    ReadOnlyMemory<byte> WrappedKey,
    DataKeyStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DemotedAt = null,
    DateTimeOffset? RetiringAt = null);

/// <summary>
/// A write (encryption or blind-index computation) was refused because this replica could not obtain a view of the key
/// ring that is at most <see cref="KeyRingOptions.MaxStaleForWrite"/> old (for example during a key-store outage).
/// Fail closed: writing with a possibly demoted or retiring version could make the data unreadable (D-ARC-23).
/// </summary>
public sealed class KeyRingStaleException : Exception
{
    public KeyRingStaleException()
    {
    }

    public KeyRingStaleException(string message)
        : base(message)
    {
    }

    public KeyRingStaleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Identity of a data key, bound into its wrapping (authenticated data in the local provider, an authenticated prefix of
/// the wrapped payload in Key Vault), so a wrapped key cannot be replayed as another legal entity's, purpose's or
/// version's key.
/// </summary>
public readonly record struct DataKeyContext(LegalEntityId LegalEntity, KeyPurpose Purpose, int Version)
{
    /// <summary>Canonical binding bytes.</summary>
    public byte[] ToBindingBytes() =>
        Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"coreins-dek|{LegalEntity}|{(int)Purpose}|v{Version}"));
}

/// <summary>Result of wrapping a data key under a key-encryption key.</summary>
/// <param name="KeyEncryptionKeyId">Versioned id of the key-encryption key used (needed to unwrap).</param>
/// <param name="WrappedKey">The wrapped key.</param>
public sealed record WrapResult(string KeyEncryptionKeyId, ReadOnlyMemory<byte> WrappedKey);

/// <summary>
/// Key-encryption keys (KEK), one per legal entity, held in the platform key-management service (contract §3.9.12):
/// Azure Key Vault in production (<see cref="AzureKeyVaultKeyProvider"/>), <see cref="LocalKeyProvider"/> for development
/// and tests. The KEK never leaves the provider; only data keys are wrapped and unwrapped.
/// </summary>
public interface IKeyProvider
{
    /// <summary>Wraps <paramref name="dataKey"/> with the current KEK version of the context's legal entity.</summary>
    ValueTask<WrapResult> WrapKeyAsync(DataKeyContext context, ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken = default);

    /// <summary>Unwraps a data key wrapped by KEK version <paramref name="keyEncryptionKeyId"/>; the context must match.</summary>
    ValueTask<byte[]> UnwrapKeyAsync(
        DataKeyContext context, string keyEncryptionKeyId, ReadOnlyMemory<byte> wrappedKey, CancellationToken cancellationToken = default);

    /// <summary>Forgets any cached "current KEK version" of the legal entity (called before a re-wrap after KEK rotation).</summary>
    ValueTask RefreshAsync(LegalEntityId legalEntity, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
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

/// <summary>
/// Counts the rows that still use a data-key version (the owning modules' re-encryption / re-index scan). Retirement is
/// refused while any row remains (D-ARC-23).
/// <para><b>Contract (D-ARC-23a):</b> an implementation must also <b>refuse</b> (throw) while database transactions
/// older than MaxStaleForWrite + 2 × RetirementMargin are open, because such a transaction may still commit rows sealed
/// with the version being retired after the count. Wrap the module scan in <see cref="GuardedRetirementScan"/> (which uses
/// <see cref="PostgresLongTransactionGuard"/>), and read from the primary, never a lagging replica.</para>
/// </summary>
public interface IRetirementScan
{
    ValueTask<long> CountRowsUsingAsync(LegalEntityId legalEntity, KeyPurpose purpose, int version, CancellationToken cancellationToken = default);
}

/// <summary>Legal entities whose keys are pre-loaded at start-up (supplied by the Host from the MKT legal-entity registry).</summary>
public interface ILegalEntityCatalogue
{
    ValueTask<IReadOnlyList<LegalEntityId>> ListAsync(CancellationToken cancellationToken = default);
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
