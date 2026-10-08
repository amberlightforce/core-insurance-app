using CoreIns.Platform.Context;
using CoreIns.Platform.DataProtection;
using DpLegalEntityId = CoreIns.Platform.DataProtection.LegalEntityId;
using SkLegalEntityId = CoreIns.SharedKernel.Identifiers.LegalEntityId;

namespace CoreIns.Modules.Billing.Domain;

/// <summary>
/// Display of P2 IBANs (REQ-BIL-345): only the last four characters are stored in clear and shown, masked. Validation is
/// <see cref="CoreIns.SharedKernel.Iban"/> (ISO 13616: registry country and length, mod-97); the value never goes into an
/// exception message, a log line or an event.
/// </summary>
internal static class IbanMask
{
    /// <summary>The last four characters of an electronic-format IBAN.</summary>
    public static string Last4(CoreIns.SharedKernel.Iban iban) => iban.Value[^4..];

    /// <summary>Masked display: every character but the last four hidden.</summary>
    public static string Mask(string last4) => "****" + last4;
}

/// <summary>
/// Field-level protection of BIL's P2 bank details, as PTY protects identifiers (<c>PartyProtection</c>, D-ARC-14): the IBAN
/// is an AES-256-GCM envelope under the legal entity's data key, bound to the field and to the payee account id, and it
/// gets a keyed blind index (domain <see cref="IndexName"/>) for exact lookup.
/// </summary>
internal sealed class PayeeProtection(FieldEncryptor encryptor, BlindIndexer blindIndexer, ILegalEntityDirectory legalEntities)
{
    public const string IbanField = "bil.payee_account.iban";
    public const string IndexName = "bil.iban";

    public SkLegalEntityId Current(RequestContext context) =>
        legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    public ValueTask<byte[]> EncryptAsync(SkLegalEntityId legalEntity, Guid payeeAccountId, CoreIns.SharedKernel.Iban iban, CancellationToken cancellationToken) =>
        encryptor.EncryptAsync(KeyOwner(legalEntity), IbanField, iban.Value, payeeAccountId.ToString("N"), cancellationToken);

    /// <summary>Blind index under the Active key (writes).</summary>
    public ValueTask<string> IndexAsync(SkLegalEntityId legalEntity, CoreIns.SharedKernel.Iban iban, CancellationToken cancellationToken) =>
        blindIndexer.ComputeAsync(KeyOwner(legalEntity), IndexName, iban.Value, BlindIndexNormalisers.Iban, cancellationToken);

    /// <summary>Blind-index values under every readable key version (lookups during key rotation).</summary>
    public ValueTask<IReadOnlyList<string>> IndexCandidatesAsync(SkLegalEntityId legalEntity, CoreIns.SharedKernel.Iban iban, CancellationToken cancellationToken) =>
        blindIndexer.SearchCandidatesAsync(KeyOwner(legalEntity), IndexName, iban.Value, BlindIndexNormalisers.Iban, cancellationToken);

    private static DpLegalEntityId KeyOwner(SkLegalEntityId legalEntity) => new(legalEntity.Value);
}
