using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CoreIns.Platform.DataProtection.Keys;

namespace CoreIns.Platform.DataProtection;

/// <summary>
/// Keyed blind index for exact-match search on encrypted identifiers (AFM, IBAN, ID numbers): HMAC-SHA256 under the
/// legal entity's BlindIndex data key (REQ-PTY-060, D-ARC-14). Values are normalised before hashing so display
/// variants ("123 456 789", "gr16 0110 …") index identically; no plain value reaches an index or a log.
/// </summary>
/// <remarks>
/// <para>Index value: <c>v{version}:{base64url(HMAC-SHA256(key_v, len32(indexName) ‖ indexName ‖ len32(normalised) ‖ normalised))}</c>. The index
/// name (for example <c>pty.identifier.AFM</c>) separates domains, so equal values in different columns do not
/// correlate; per-legal-entity keys mean indexes never correlate across entities.</para>
/// <para>Rotation without downtime (D-ARC-23): writes use the replica's Active version; searches use
/// <see cref="SearchCandidatesAsync"/>: one value per readable version (Active, DecryptOnly, Retiring) from a key-ring
/// view at most <see cref="KeyRingOptions.SearchCandidateCacheDuration"/> old, so it includes versions newer than this
/// replica's write key and rows written with a demoted version (<c>WHERE idx = ANY(@candidates)</c>), until the
/// re-index job has moved every row to the Active version and the old version is retired. Writes follow the key-ring
/// write-staleness bound (<see cref="KeyRingOptions.MaxStaleForWrite"/>). Determinism holds per key version.</para>
/// </remarks>
public sealed class BlindIndexer(KeyRing keyRing)
{
    /// <summary>Index value under the Active version, for writes.</summary>
    public async ValueTask<string> ComputeAsync(
        LegalEntityId legalEntity, string indexName, string value, Func<string, string>? normaliser = null, CancellationToken cancellationToken = default)
    {
        var key = await keyRing.GetActiveAsync(legalEntity, KeyPurpose.BlindIndex, cancellationToken).ConfigureAwait(false);
        return Compute(key, indexName, (normaliser ?? BlindIndexNormalisers.Identifier)(value));
    }

    /// <summary>Synchronous <see cref="ComputeAsync"/>.</summary>
    public string Compute(LegalEntityId legalEntity, string indexName, string value, Func<string, string>? normaliser = null) =>
        Compute(keyRing.GetActive(legalEntity, KeyPurpose.BlindIndex), indexName, (normaliser ?? BlindIndexNormalisers.Identifier)(value));

    /// <summary>Index values of <paramref name="value"/> under every readable version (Active first), for searches.</summary>
    public async ValueTask<IReadOnlyList<string>> SearchCandidatesAsync(
        LegalEntityId legalEntity, string indexName, string value, Func<string, string>? normaliser = null, CancellationToken cancellationToken = default)
    {
        var normalised = (normaliser ?? BlindIndexNormalisers.Identifier)(value);
        var keys = await keyRing.GetReadableAsync(legalEntity, KeyPurpose.BlindIndex, cancellationToken).ConfigureAwait(false);
        return keys.Select(key => Compute(key, indexName, normalised)).ToList();
    }

    /// <summary>Key version of an index value (re-index job selects rows below the Active version).</summary>
    public static int VersionOf(string indexValue)
    {
        ArgumentNullException.ThrowIfNull(indexValue);
        var colon = indexValue.IndexOf(':', StringComparison.Ordinal);
        if (indexValue.Length < 3 || indexValue[0] != 'v' || colon < 2
            || !int.TryParse(indexValue.AsSpan(1, colon - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            throw new FormatException("Not a blind-index value.");
        }

        return version;
    }

    private static string Compute(DataKey key, string indexName, string normalised)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        var name = Encoding.UTF8.GetBytes(indexName);
        var value = Encoding.UTF8.GetBytes(normalised);
        // len32(indexName) ‖ indexName ‖ len32(value) ‖ value: injective, so (name, value) pairs cannot be re-split (review N2).
        var message = new byte[4 + name.Length + 4 + value.Length];
        BinaryPrimitives.WriteInt32BigEndian(message, name.Length);
        name.CopyTo(message, 4);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(4 + name.Length), value.Length);
        value.CopyTo(message, 8 + name.Length);

        var mac = HMACSHA256.HashData(key.Material, message);
        CryptographicOperations.ZeroMemory(value);
        CryptographicOperations.ZeroMemory(message);
        return string.Create(CultureInfo.InvariantCulture, $"v{key.Version}:{Base64Url(mac)}");
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Normalisers applied before blind-index hashing (locale-independent).</summary>
public static class BlindIndexNormalisers
{
    /// <summary>
    /// Identifiers (AFM, ID numbers, IBAN): NFKC, white space and the separators "-", ".", "/" removed, upper-cased
    /// (invariant). Scheme-specific normalisation (for example the AFM via <c>IdValidator</c>) runs first in the caller.
    /// </summary>
    public static string Identifier(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
        {
            if (!char.IsWhiteSpace(character) && character is not ('-' or '.' or '/'))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }

    /// <summary>IBAN: same as <see cref="Identifier"/> (spaces removed, upper case).</summary>
    public static string Iban(string value) => Identifier(value);
}
