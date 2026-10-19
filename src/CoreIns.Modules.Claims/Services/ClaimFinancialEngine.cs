using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.Services;

/// <summary>A reference to authoritative evidence; callers cannot supply financial amounts.</summary>
internal sealed record ClaimFinancialEvidence(string Kind, string Reference);
internal sealed record ResolvedClaimFinancialEvidence(TransactionSetBuildRequest Request, string PaymentMethod = "SEPA_CT", PartyId? CounterpartyInsurerPartyId = null);

/// <summary>Module-owned evidence reader. Recovery/FS/payment consumers supply implementations reading their stored evidence.</summary>
internal interface IClaimFinancialEvidenceSource
{
    Task<ResolvedClaimFinancialEvidence?> ResolveAsync(ClaimId claimId, ClaimFinancialEvidence evidence, CancellationToken cancellationToken);
}

/// <summary>Until an evidence owner is registered, the engine refuses the operation.</summary>
internal sealed class UnavailableClaimFinancialEvidenceSource : IClaimFinancialEvidenceSource
{
    public Task<ResolvedClaimFinancialEvidence?> ResolveAsync(ClaimId claimId, ClaimFinancialEvidence evidence, CancellationToken cancellationToken) =>
        Task.FromResult<ResolvedClaimFinancialEvidence?>(null);
}

internal interface IClaimFinancialEngine
{
    Task<TransactionSetSubmitResponse> SubmitSystemSetAsync(ClaimId claimId, ClaimFinancialEvidence evidence, CancellationToken cancellationToken);
}

/// <summary>Internal evidence-backed path, deliberately absent from HTTP and public request DTOs.</summary>
internal sealed class ClaimFinancialEngine(
    ClaimsDbContext db, RequestContext context, ClaimProtection protection, IClaimFinancialEvidenceSource source,
    ICommandHandler<BuildTransactionSet, TransactionSetBuildResponse> build,
    ICommandHandler<SubmitTransactionSet, TransactionSetSubmitResponse> submit) : IClaimFinancialEngine
{
    public async Task<TransactionSetSubmitResponse> SubmitSystemSetAsync(ClaimId claimId, ClaimFinancialEvidence evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Kind is not ("BIL_ALLOCATION" or "FS_NOTIFICATION" or "FS_STATEMENT_LINE" or "DISBURSEMENT_OUTCOME")
            || string.IsNullOrWhiteSpace(evidence.Reference))
        {
            throw new DomainException(DomainError.Of(ModuleCode.CLM, "VALIDATION", "The system set needs a supported evidence reference."));
        }

        var entity = protection.Current(context);
        // Serialize replay checks under the same claim lock as the normal money path.
        if (await ClaimSupport.LoadAsync(db, entity, claimId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new DomainException(ClaimSupport.NotFound("claim"));
        }

        var previous = await db.TransactionSets.SingleOrDefaultAsync(s => s.LegalEntityId == entity
            && s.EvidenceKind == evidence.Kind && s.EvidenceRef == evidence.Reference, cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            if (previous.ClaimId != claimId)
            {
                throw new DomainException(FinancialSupport.Stale("The evidence belongs to another claim."));
            }

            return new TransactionSetSubmitResponse { SetId = previous.SetId.Value, Status = previous.Status };
        }

        var resolved = await source.ResolveAsync(claimId, evidence, cancellationToken).ConfigureAwait(false);
        var request = resolved?.Request;
        if (request is null || request.ClaimId != claimId || request.Transactions.Count == 0)
        {
            throw new DomainException(DomainError.Of(ModuleCode.CLM, "NOT-AVAILABLE", "Authoritative financial evidence is unavailable."));
        }

        if (resolved!.PaymentMethod is not ("SEPA_CT" or "CLEARING")
            || (resolved.PaymentMethod == "CLEARING" && (evidence.Kind != "FS_NOTIFICATION" || resolved.CounterpartyInsurerPartyId is null)))
        {
            throw new DomainException(DomainError.Of(ModuleCode.CLM, "NOT-AVAILABLE", "CLEARING needs an authoritative FS notification and insurer counterparty."));
        }

        var actor = context.Actor;
        var roles = context.Roles;
        try
        {
            context.Actor = ActorRef.Service("clm-financial-engine");
            context.Roles = [];
            var built = InProcess.Unwrap(await build.HandleAsync(new BuildTransactionSet(request, evidence, resolved.PaymentMethod, resolved.CounterpartyInsurerPartyId), cancellationToken).ConfigureAwait(false));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return InProcess.Unwrap(await submit.HandleAsync(new SubmitTransactionSet(new TransactionSetSubmitRequest { SetId = built.SetId }), cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            context.Actor = actor;
            context.Roles = roles;
        }
    }
}
