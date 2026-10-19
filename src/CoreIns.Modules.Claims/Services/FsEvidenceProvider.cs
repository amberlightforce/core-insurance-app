using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.Services;

/// <summary>
/// FS evidence (D-SL4-12): an at-fault FS notification (kind <c>FS_NOTIFICATION</c>, reference = clearing reference) resolves to the
/// CLEARING payment of the stored case; amounts come from the stored notification, never from a client.
/// TODO(SL4-CLM-FS-STATEMENT): resolve <c>FS_STATEMENT_LINE</c> here (statement line → recovery / settlement of the payable).
/// </summary>
internal sealed class FsEvidenceProvider(ClaimsDbContext db, RequestContext context, ClaimProtection protection) : IClaimFinancialEvidenceProvider
{
    public async Task<ResolvedClaimFinancialEvidence?> ResolveAsync(ClaimId claimId, ClaimFinancialEvidence evidence, CancellationToken cancellationToken)
    {
        if (evidence.Kind != "FS_NOTIFICATION")
        {
            return null;
        }

        var entity = protection.Current(context);
        var fsCase = await db.FsCases.AsNoTracking().SingleOrDefaultAsync(c => c.LegalEntityId == entity && c.ClaimId == claimId
            && c.NotificationReference == evidence.Reference && c.Role == "AT_FAULT_INSURER", cancellationToken).ConfigureAwait(false);
        if (fsCase is not { ExposureId: { } exposureId, ClearingValue: { } value } || value <= 0m)
        {
            return null;
        }

        var request = new TransactionSetBuildRequest
        {
            ClaimId = claimId,
            Transactions =
            [
                new TransactionSetBuildRequest.TransactionItem
                {
                    Kind = TransactionSetBuildRequest.TransactionItem.KindValue.Payment,
                    ExposureId = exposureId, CostType = "INDEMNITY", CostCategory = "VEHICLE_REPAIR",
                    Amount = new Money(value, Currency.EUR),
                    PayeePartyId = fsCase.CounterpartyInsurerPartyId, PayeeAccountId = fsCase.FsCaseId,
                    PaymentType = TransactionSetBuildRequest.TransactionItem.PaymentTypeValue.Final,
                },
            ],
        };
        return new ResolvedClaimFinancialEvidence(request, "CLEARING", fsCase.CounterpartyInsurerPartyId);
    }
}
