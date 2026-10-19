using CoreIns.Modules.Claims.Commands.FriendlySettlement;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Item = CoreIns.Modules.Claims.Contracts.Api.TransactionSetBuildRequest.TransactionItem;

namespace CoreIns.Modules.Claims.Services;

/// <summary>
/// FS statement evidence (REQ-CLM-160, D-SL4-12): a MATCHED own-insurer statement line (kind <c>FS_STATEMENT_LINE</c>, reference = the line
/// id) resolves to one RECOVERY transaction on the case's recovery; the amount is the stored line's, never a client's (PITFALLS 7).
/// The CLEARING payable has no set: its payment is issued directly when its line matches.
/// </summary>
internal sealed class FsStatementEvidenceProvider(ClaimsDbContext db, RequestContext context, ClaimProtection protection) : IClaimFinancialEvidenceProvider
{
    public async Task<ResolvedClaimFinancialEvidence?> ResolveAsync(ClaimId claimId, ClaimFinancialEvidence evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Kind != FsStatements.EvidenceKind || !Guid.TryParseExact(evidence.Reference, "D", out var lineId))
        {
            return null;
        }

        var entity = protection.Current(context);
        var line = await db.FsStatementLines.AsNoTracking().SingleOrDefaultAsync(
            l => l.FsStatementLineId == lineId && l.LegalEntityId == entity && l.ClaimId == claimId && l.MatchStatus == "MATCHED" && l.Direction == FsStatements.Receivable, cancellationToken)
            .ConfigureAwait(false);
        if (line is not { RecoveryId: { } recoveryId, FsCaseId: { } caseId } || line.Amount <= 0m)
        {
            return null;
        }

        var exposureId = await db.FsCases.AsNoTracking().Where(c => c.FsCaseId == caseId && c.RecoveryId == recoveryId)
            .Select(c => c.ExposureId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (exposureId is null)
        {
            return null;
        }

        return new ResolvedClaimFinancialEvidence(new TransactionSetBuildRequest
        {
            ClaimId = claimId,
            Transactions =
            [
                new Item
                {
                    Kind = Item.KindValue.Recovery, ExposureId = exposureId.Value, CostType = "INDEMNITY", CostCategory = "VEHICLE_REPAIR",
                    Amount = new Money(line.Amount, Currency.EUR), RecoveryId = recoveryId,
                },
            ],
        });
    }
}
