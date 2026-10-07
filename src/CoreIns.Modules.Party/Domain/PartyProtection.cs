using System.Globalization;
using CoreIns.Platform.Context;
using CoreIns.Platform.DataProtection;
using CoreIns.SharedKernel;
using DpLegalEntityId = CoreIns.Platform.DataProtection.LegalEntityId;
using SkLegalEntityId = CoreIns.SharedKernel.Identifiers.LegalEntityId;

namespace CoreIns.Modules.Party.Domain;

/// <summary>
/// Field-level protection of PTY's P2 data (REQ-PTY-060, D-ARC-14, F-1e DataProtection): identifier values and the
/// birth date are AES-256-GCM envelopes under the legal entity's data key, bound to the field and to the row id (a
/// ciphertext cannot be moved to another row or column); identifiers also get a keyed blind index for exact search. No
/// plain value reaches an index, a log or an event.
/// </summary>
internal sealed class PartyProtection(FieldEncryptor encryptor, BlindIndexer blindIndexer, ILegalEntityDirectory legalEntities)
{
    public const string BirthDateField = "pty.party.birth_date";
    public const string IdentifierField = "pty.party_identifier.value";

    /// <summary>Blind-index domain of one scheme (equal values of different schemes never correlate).</summary>
    public static string IndexName(string scheme) => "pty.identifier." + scheme;

    public static DpLegalEntityId KeyOwner(SkLegalEntityId legalEntity) => new(legalEntity.Value);

    public SkLegalEntityId Current(RequestContext context) =>
        legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    public ValueTask<byte[]> EncryptBirthDateAsync(SkLegalEntityId legalEntity, Guid partyId, BusinessDate birthDate, CancellationToken cancellationToken) =>
        encryptor.EncryptAsync(KeyOwner(legalEntity), BirthDateField, birthDate.ToString(), partyId.ToString("N"), cancellationToken);

    public async ValueTask<BusinessDate> DecryptBirthDateAsync(SkLegalEntityId legalEntity, Guid partyId, byte[] envelope, CancellationToken cancellationToken)
    {
        EnsureOwner(legalEntity, envelope);
        var text = await encryptor.DecryptAsync(envelope, BirthDateField, partyId.ToString("N"), cancellationToken).ConfigureAwait(false);
        return new BusinessDate(DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    public ValueTask<byte[]> EncryptIdentifierAsync(SkLegalEntityId legalEntity, Guid identifierId, string value, CancellationToken cancellationToken) =>
        encryptor.EncryptAsync(KeyOwner(legalEntity), IdentifierField, value, identifierId.ToString("N"), cancellationToken);

    public async ValueTask<string> DecryptIdentifierAsync(SkLegalEntityId legalEntity, Guid identifierId, byte[] envelope, CancellationToken cancellationToken)
    {
        EnsureOwner(legalEntity, envelope);
        return await encryptor.DecryptAsync(envelope, IdentifierField, identifierId.ToString("N"), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Blind index of a normalised identifier under the Active key (writes).</summary>
    public ValueTask<string> IndexAsync(SkLegalEntityId legalEntity, string scheme, string normalised, CancellationToken cancellationToken) =>
        blindIndexer.ComputeAsync(KeyOwner(legalEntity), IndexName(scheme), normalised, null, cancellationToken);

    /// <summary>Blind-index values under every readable key version (searches and duplicate checks during rotation).</summary>
    public ValueTask<IReadOnlyList<string>> IndexCandidatesAsync(SkLegalEntityId legalEntity, string scheme, string normalised, CancellationToken cancellationToken) =>
        blindIndexer.SearchCandidatesAsync(KeyOwner(legalEntity), IndexName(scheme), normalised, null, cancellationToken);

    /// <summary>A row of another legal entity is never decrypted under the current one (F-1e review m6).</summary>
    private static void EnsureOwner(SkLegalEntityId legalEntity, byte[] envelope)
    {
        if (FieldEncryptor.ReadHeader(envelope).LegalEntity != KeyOwner(legalEntity))
        {
            throw new FieldDecryptionException("The value belongs to another legal entity than the current unit of work.");
        }
    }
}
