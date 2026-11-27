using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreIns.Modules.Reinsurance.Recovery;

/// <summary>RI-ERR errors of the recovery operations (BO-RI-08).</summary>
internal static class RecoveryOpsErrors
{
    public static CoreIns.SharedKernel.Results.DomainError Occurrence(string detail) => CoreIns.SharedKernel.Results.DomainError.Of(ModuleCode.RI, "OCCURRENCE", detail);

    public static CoreIns.SharedKernel.Results.DomainError Authority(string detail) => CoreIns.SharedKernel.Results.DomainError.Of(ModuleCode.RI, "AUTHORITY", detail);

    public static CoreIns.SharedKernel.Results.DomainError Refused(string detail) => CoreIns.SharedKernel.Results.DomainError.Of(ModuleCode.RI, "RECALCULATION-REFUSED", detail);
}

/// <summary><c>ri.Recovery.recalculate</c> as a command (REQ-RI-003, -128).</summary>
internal sealed record RecalculateRecoveries(RecoveryRecalculateRequest Request) : ICommand<RecoveryRecalculateResponse>;

/// <summary>Shape rules.</summary>
internal sealed class RecalculateRecoveriesValidator : AbstractValidator<RecalculateRecoveries>
{
    public RecalculateRecoveriesValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.CatEventCode).MaximumLength(64).When(c => c.Request is not null);
    }
}

/// <summary>
/// The full restatement of one contract year (REQ-RI-003; D-SL4-19): one advisory lock per contract year (taken inside
/// <see cref="RecoveryBooker"/>), the whole year is recomputed from the claim view and only the difference to what is booked
/// is written as sealed delta rows, in a batch that is always written so the caller has a batch id. The result equals what the
/// incremental, claim-by-claim intake leaves behind. A cat event code is BO-RI-09 and is refused here.
/// </summary>
internal sealed class RecalculateRecoveriesHandler(
    ReinsuranceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, RecoveryBooker booker) : ICommandHandler<RecalculateRecoveries, RecoveryRecalculateResponse>
{
    public async Task<Result<RecoveryRecalculateResponse>> HandleAsync(RecalculateRecoveries command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (request.ContractYear is null == string.IsNullOrWhiteSpace(request.CatEventCode))
        {
            return RiErrors.Validation("contractYear", "YEAR_OR_EVENT", "Give either the contract year or a cat event code.");
        }

        if (!string.IsNullOrWhiteSpace(request.CatEventCode))
        {
            // TODO(hardening): BO-RI-09 (catastrophe occurrences) restates by event.
            return RiErrors.Validation("catEventCode", "NOT_AVAILABLE", "Recalculation by cat event is not available yet; give the contract year.");
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var contract = await db.Contracts.AsNoTracking().SingleOrDefaultAsync(c => c.ContractId == request.ContractId && c.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (contract is null)
        {
            return RiErrors.NotFound();
        }

        if (contract.ContractYear != request.ContractYear)
        {
            return RiErrors.Validation("contractYear", "YEAR_MISMATCH", $"The contract is for {contract.ContractYear}, not {request.ContractYear}.");
        }

        var result = await booker.RecalculateBatchAsync(contract.ContractId.Value, Guid.Empty, Guid.CreateVersion7(), forceBatch: true, cancellationToken).ConfigureAwait(false);
        if (result.Refusal is not null || result.BatchId is not { } batchId)
        {
            return RecoveryOpsErrors.Refused(result.Refusal ?? "The recalculation produced no batch.");
        }

        return new RecoveryRecalculateResponse { BatchId = batchId, ContractId = contract.ContractId, ContractYear = contract.ContractYear, RowsBooked = result.Rows };
    }
}

/// <summary>Audit facts of <c>ri.Recovery.recalculate</c>.</summary>
internal sealed class RecalculateRecoveriesAuditor : ICommandAuditor<RecalculateRecoveries, RecoveryRecalculateResponse>
{
    public CommandAuditFacts Describe(RecalculateRecoveries command, Result<RecoveryRecalculateResponse>? result) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.RI, "Contract", command.Request.ContractId),
        BusinessKeys = BusinessKeys.Empty.With("riContractId", command.Request.ContractId.Value.ToString("D")),
        Changes = result is { IsSuccess: true } ok ? AuditDiff.Compute(null, new { batchId = ok.Value.BatchId, rowsBooked = ok.Value.RowsBooked }) : [],
    };
}

/// <summary><c>ri.Occurrence.link</c> as a command (REQ-RI-115).</summary>
internal sealed record LinkOccurrence(OccurrenceLinkRequest Request) : ICommand<OccurrenceLinkResponse>;

/// <summary><c>ri.Occurrence.unlink</c> as a command.</summary>
internal sealed record UnlinkOccurrence(OccurrenceUnlinkRequest Request) : ICommand<OccurrenceUnlinkResponse>;

/// <summary>Shape rules of link.</summary>
internal sealed class LinkOccurrenceValidator : AbstractValidator<LinkOccurrence>
{
    public LinkOccurrenceValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.ClaimIds).NotNull().Must(ids => ids.Count is >= 2 and <= 100).WithMessage("Link two to 100 claims.").When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(1024).When(c => c.Request is not null);
    }
}

/// <summary>Shape rules of unlink.</summary>
internal sealed class UnlinkOccurrenceValidator : AbstractValidator<UnlinkOccurrence>
{
    public UnlinkOccurrenceValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.ClaimIds).NotNull().Must(ids => ids.Count is >= 1 and <= 100).WithMessage("Unlink one to 100 claims.").When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(1024).When(c => c.Request is not null);
    }
}

/// <summary>
/// Groups claims into one occurrence (REQ-RI-115) so a per-occurrence layer sees them as one loss. All claims must belong to the
/// caller's legal entity and fall under the same contract, and none may be in another active occurrence. The contract year is
/// restated at once (same advisory lock as the booker), so the recoveries follow the grouping in the same transaction.
/// </summary>
internal sealed class LinkOccurrenceHandler(
    ReinsuranceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, RecoveryBooker booker) : ICommandHandler<LinkOccurrence, OccurrenceLinkResponse>
{
    public async Task<Result<OccurrenceLinkResponse>> HandleAsync(LinkOccurrence command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
        var claimIds = request.ClaimIds.Select(c => c.Value).Distinct().Order().ToArray();
        if (claimIds.Length < 2)
        {
            return RiErrors.Validation("claimIds", "TWO_CLAIMS", "An occurrence groups at least two different claims.");
        }

        var known = await db.ClaimViews.AsNoTracking().Where(v => claimIds.Contains(v.ClaimId) && v.LegalEntityId == legalEntity).Select(v => v.ClaimId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (known.Count != claimIds.Length)
        {
            return RecoveryOpsErrors.Occurrence("A claim is unknown to reinsurance in your legal entity.");
        }

        var contracts = await db.ClaimExposures.AsNoTracking().Where(e => claimIds.Contains(e.ClaimId) && e.ContractId != null)
            .Select(e => new { e.ClaimId, e.ContractId }).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var contractIds = contracts.Select(c => c.ContractId!.Value).Distinct().ToList();
        if (contractIds.Count != 1 || contracts.Select(c => c.ClaimId).Distinct().Count() != claimIds.Length)
        {
            return RecoveryOpsErrors.Occurrence("The claims must all fall under one and the same reinsurance contract.");
        }

        var contractId = contractIds[0];
        await LockAsync(db, contractId, cancellationToken).ConfigureAwait(false);
        var contract = await db.Contracts.AsNoTracking().SingleOrDefaultAsync(c => c.ContractId == new RiContractId(contractId) && c.LegalEntityId == new LegalEntityId(legalEntity), cancellationToken).ConfigureAwait(false);
        if (contract is null)
        {
            return RiErrors.NotFound();
        }

        if (await db.OccurrenceClaims.AnyAsync(l => claimIds.Contains(l.ClaimId) && l.UnlinkedAt == null, cancellationToken).ConfigureAwait(false))
        {
            return RecoveryOpsErrors.Occurrence("A claim is already linked to an occurrence; unlink it first.");
        }

        var now = clock.Now;
        var occurrenceId = Guid.CreateVersion7();
        db.Occurrences.Add(new OccurrenceRow
        {
            OccurrenceId = occurrenceId, LegalEntityId = legalEntity, ContractId = contractId, ContractYear = contract.ContractYear,
            Reason = request.Reason.Trim(), CreatedBy = context.Actor.ToString(), CreatedAt = now,
        });
        db.OccurrenceClaims.AddRange(claimIds.Select(claim => new OccurrenceClaimRow { OccurrenceId = occurrenceId, ClaimId = claim, LegalEntityId = legalEntity, LinkedAt = now }));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return RiErrors.Stale("A claim was linked to another occurrence meanwhile.");
        }

        await booker.RecalculateBatchAsync(contractId, claimIds[0], Guid.CreateVersion7(), forceBatch: false, cancellationToken).ConfigureAwait(false);
        return new OccurrenceLinkResponse
        {
            Occurrence = new OccurrenceView
            {
                OccurrenceId = occurrenceId, ContractId = contract.ContractId, ContractYear = contract.ContractYear, Reason = request.Reason.Trim(),
                ClaimIds = [.. claimIds.Select(c => new ClaimId(c))], Status = OccurrenceView.StatusValue.Active,
            },
        };
    }

    /// <summary>The same advisory lock the booker takes per contract year, so a link and a claim movement never interleave.</summary>
    internal static Task LockAsync(ReinsuranceDbContext db, Guid contractId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", [new NpgsqlParameter("k", "ri:recovery:" + contractId.ToString("D"))], cancellationToken);
}

/// <summary>
/// Takes claims out of their occurrence (REQ-RI-115). Claims must be in one active occurrence; when fewer than two remain
/// linked the occurrence is dissolved. The contract year is restated, so the claims are measured on their own again and the
/// recoveries return to what they were before the link (the difference is booked as deltas, never as updates).
/// </summary>
internal sealed class UnlinkOccurrenceHandler(
    ReinsuranceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, RecoveryBooker booker) : ICommandHandler<UnlinkOccurrence, OccurrenceUnlinkResponse>
{
    public async Task<Result<OccurrenceUnlinkResponse>> HandleAsync(UnlinkOccurrence command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
        var claimIds = request.ClaimIds.Select(c => c.Value).Distinct().ToArray();
        var memberships = await db.OccurrenceClaims.Where(l => claimIds.Contains(l.ClaimId) && l.UnlinkedAt == null && l.LegalEntityId == legalEntity)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (memberships.Count != claimIds.Length)
        {
            return RecoveryOpsErrors.Occurrence("A claim is not linked to an occurrence.");
        }

        var occurrenceIds = memberships.Select(m => m.OccurrenceId).Distinct().ToList();
        if (occurrenceIds.Count != 1)
        {
            return RecoveryOpsErrors.Occurrence("The claims belong to different occurrences; unlink them one occurrence at a time.");
        }

        var occurrence = await db.Occurrences.SingleAsync(o => o.OccurrenceId == occurrenceIds[0], cancellationToken).ConfigureAwait(false);
        await LinkOccurrenceHandler.LockAsync(db, occurrence.ContractId, cancellationToken).ConfigureAwait(false);

        // Re-read under the lock: a racing unlink may have taken the claims out already.
        var active = await db.OccurrenceClaims.Where(l => l.OccurrenceId == occurrence.OccurrenceId && l.UnlinkedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (claimIds.Any(c => active.All(a => a.ClaimId != c)))
        {
            return RiErrors.Stale("The occurrence changed meanwhile.");
        }

        var now = clock.Now;
        var reason = request.Reason.Trim();
        var leaving = active.Where(a => claimIds.Contains(a.ClaimId)).ToList();
        var remaining = active.Except(leaving).ToList();
        if (remaining.Count < 2)
        {
            leaving.AddRange(remaining);
            remaining = [];
            occurrence.DissolvedAt = now;
        }

        foreach (var membership in leaving)
        {
            membership.UnlinkedAt = now;
            membership.UnlinkReason = reason;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await booker.RecalculateBatchAsync(occurrence.ContractId, claimIds[0], Guid.CreateVersion7(), forceBatch: false, cancellationToken).ConfigureAwait(false);
        return new OccurrenceUnlinkResponse
        {
            Occurrence = new OccurrenceView
            {
                OccurrenceId = occurrence.OccurrenceId, ContractId = new RiContractId(occurrence.ContractId), ContractYear = occurrence.ContractYear, Reason = occurrence.Reason,
                ClaimIds = [.. remaining.Select(m => new ClaimId(m.ClaimId)).OrderBy(c => c.Value)],
                Status = occurrence.DissolvedAt is null ? OccurrenceView.StatusValue.Active : OccurrenceView.StatusValue.Dissolved,
            },
        };
    }
}

/// <summary>Audit facts of <c>ri.Occurrence.link</c>.</summary>
internal sealed class LinkOccurrenceAuditor : ICommandAuditor<LinkOccurrence, OccurrenceLinkResponse>
{
    public CommandAuditFacts Describe(LinkOccurrence command, Result<OccurrenceLinkResponse>? result) => result is { IsSuccess: true } ok
        ? new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.RI, "Occurrence", ok.Value.Occurrence.OccurrenceId.ToString("D")),
            BusinessKeys = BusinessKeys.Empty.With("riContractId", ok.Value.Occurrence.ContractId.Value.ToString("D")),
            Changes = AuditDiff.Compute(null, new { claims = ok.Value.Occurrence.ClaimIds.Count, reason = command.Request.Reason }),
        }
        : new CommandAuditFacts();
}

/// <summary>Audit facts of <c>ri.Occurrence.unlink</c>.</summary>
internal sealed class UnlinkOccurrenceAuditor : ICommandAuditor<UnlinkOccurrence, OccurrenceUnlinkResponse>
{
    public CommandAuditFacts Describe(UnlinkOccurrence command, Result<OccurrenceUnlinkResponse>? result) => result is { IsSuccess: true } ok
        ? new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.RI, "Occurrence", ok.Value.Occurrence.OccurrenceId.ToString("D")),
            BusinessKeys = BusinessKeys.Empty.With("riContractId", ok.Value.Occurrence.ContractId.Value.ToString("D")),
            Changes = AuditDiff.Compute(null, new { unlinked = command.Request.ClaimIds.Count, status = ok.Value.Occurrence.Status.ToString(), reason = command.Request.Reason }),
        }
        : new CommandAuditFacts();
}
