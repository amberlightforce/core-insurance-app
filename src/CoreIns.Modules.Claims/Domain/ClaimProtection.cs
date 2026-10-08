using CoreIns.Platform.Context;
using CoreIns.Platform.DataProtection;
using DpLegalEntityId = CoreIns.Platform.DataProtection.LegalEntityId;
using SkLegalEntityId = CoreIns.SharedKernel.Identifiers.LegalEntityId;

namespace CoreIns.Modules.Claims.Domain;

/// <summary>
/// Field-level protection of CLM's free text (decision D-SL2-CLM-P2, recorded in the work package report): PRD-07 §7.1
/// classifies the description P1, but free text routinely carries names, health facts and addresses of third parties, so
/// the slice treats the description, the loss location and the whole FNOL snapshot payload as P2: AES-256-GCM envelopes
/// under the legal entity's data key, bound to the field and the row id (a ciphertext cannot move to another row or
/// column). They are never in lists, events, URLs, logs, audit diffs or the idempotency store; <c>clm.Claim.get</c> and
/// <c>clm.Fnol.get</c> (queries, never stored) show them to holders of those permissions.
/// </summary>
internal sealed class ClaimProtection(FieldEncryptor encryptor, ILegalEntityDirectory legalEntities)
{
    public const string DescriptionField = "clm.claim.description";
    public const string LossLocationField = "clm.claim.loss_location";
    public const string FnolPayloadField = "clm.fnol_snapshot.payload";

    public SkLegalEntityId Current(RequestContext context) =>
        legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    public ValueTask<byte[]> EncryptAsync(SkLegalEntityId legalEntity, string field, Guid rowId, string plaintext, CancellationToken cancellationToken) =>
        encryptor.EncryptAsync(KeyOwner(legalEntity), field, plaintext, rowId.ToString("N"), cancellationToken);

    public async ValueTask<string> DecryptAsync(SkLegalEntityId legalEntity, string field, Guid rowId, byte[] envelope, CancellationToken cancellationToken)
    {
        // A row of another legal entity is never decrypted under the current one.
        if (FieldEncryptor.ReadHeader(envelope).LegalEntity != KeyOwner(legalEntity))
        {
            throw new FieldDecryptionException("The value belongs to another legal entity than the current unit of work.");
        }

        return await encryptor.DecryptAsync(envelope, field, rowId.ToString("N"), cancellationToken).ConfigureAwait(false);
    }

    private static DpLegalEntityId KeyOwner(SkLegalEntityId legalEntity) => new(legalEntity.Value);
}
