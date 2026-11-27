using CoreIns.Modules.Reinsurance.Authority;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.Rules;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Reinsurance.Recovery;

/// <summary><c>ri.Recovery.adjust</c> as a command (REQ-RI-003, -135).</summary>
internal sealed record AdjustRecovery(RecoveryAdjustRequest Request) : ICommand<RecoveryAdjustResponse>;

/// <summary>Shape rules.</summary>
internal sealed class AdjustRecoveryValidator : AbstractValidator<AdjustRecovery>
{
    public AdjustRecoveryValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(1024).When(c => c.Request is not null);
    }
}

/// <summary>
/// A manual adjustment of the incurred recoverable of one layer on one occurrence (REQ-RI-135): a reason is mandatory, EUR cents
/// only, never zero. The amount is split over the panel by largest remainder (<see cref="CentAllocation"/>), one sealed delta row
/// per participant in <c>ri.recovery_adjustment</c>; nothing is updated and the engine's own batches are not touched, so a later
/// recalculation does not undo it. The actor needs <c>RI.RECOVERY_ADJUSTMENT</c> authority over the amount (PITFALLS 4: the
/// dimensions are computed here). A referral is refused: the amount is above the actor's own limit.
/// TODO(hardening): turn REFER into a PLT approval request (a checker approves, then the rows are written) instead of refusing.
/// </summary>
internal sealed class AdjustRecoveryHandler(
    ReinsuranceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IAuthorityService authority) : ICommandHandler<AdjustRecovery, RecoveryAdjustResponse>
{
    public async Task<Result<RecoveryAdjustResponse>> HandleAsync(AdjustRecovery command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var currency = Currency.FromCode("EUR");
        if (request.Amount.Currency != currency || !request.Amount.IsRoundedToMinorUnits || request.Amount.IsZero)
        {
            return RiErrors.Validation("amount", "AMOUNT", "The adjustment must be a non-zero EUR amount in whole cents.");
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var contract = await db.Contracts.AsNoTracking().SingleOrDefaultAsync(c => c.ContractId == request.ContractId && c.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (contract is null)
        {
            return RiErrors.NotFound();
        }

        await LinkOccurrenceHandler.LockAsync(db, contract.ContractId.Value, cancellationToken).ConfigureAwait(false);
        var version = await db.Versions.AsNoTracking().SingleAsync(v => v.ContractId == contract.ContractId && v.KnownTo == null, cancellationToken).ConfigureAwait(false);
        var layer = await db.Layers.AsNoTracking().SingleOrDefaultAsync(l => l.LayerId == request.LayerId.Value && l.VersionId == version.VersionId && l.Rev == version.ContentRev, cancellationToken).ConfigureAwait(false);
        if (layer is null)
        {
            return RiErrors.Validation("layerId", "LAYER", "The layer does not belong to the contract.");
        }

        // The claim must be one the contract covers; a linked claim is booked on the occurrence's representative claim.
        var claim = request.ClaimId.Value;
        if (!await db.ClaimExposures.AnyAsync(e => e.ClaimId == claim && e.ContractId == contract.ContractId.Value, cancellationToken).ConfigureAwait(false))
        {
            return RecoveryOpsErrors.Occurrence("The claim does not fall under this contract.");
        }

        var membership = await db.OccurrenceClaims.AsNoTracking().SingleOrDefaultAsync(l => l.ClaimId == claim && l.UnlinkedAt == null, cancellationToken).ConfigureAwait(false);
        if (membership is not null)
        {
            claim = await db.OccurrenceClaims.AsNoTracking().Where(l => l.OccurrenceId == membership.OccurrenceId && l.UnlinkedAt == null).MinAsync(l => l.ClaimId, cancellationToken).ConfigureAwait(false);
        }

        var now = clock.Now;
        var check = await authority.CheckAsync(
            new AuthorityCheckRequest(
                context.Actor, context.Roles, ReinsuranceAuthorityTypes.RecoveryAdjustment,
                new Dictionary<string, DimensionValue>(StringComparer.Ordinal) { [ReinsuranceAuthorityTypes.AmountDimension] = DimensionValue.Of(new Money(Math.Abs(request.Amount.Amount), currency)) },
                ObjectRef.For(ModuleCode.RI, "Contract", contract.ContractId), now),
            cancellationToken).ConfigureAwait(false);
        context.AuthorityChecks.Add(check);
        if (check.Decision != AuthorityDecision.Allow)
        {
            return RecoveryOpsErrors.Authority($"No authority for a recovery adjustment of {request.Amount} (RI.RECOVERY_ADJUSTMENT: {check.ReasonCode}).");
        }

        var content = await ContractSupport.ReadContentAsync(db, contract, version, cancellationToken).ConfigureAwait(false);
        var panel = RecoveryBooker.Participations(content).OrderByDescending(p => p.IsLead).ThenBy(p => p.ParticipantId, StringComparer.Ordinal).ToList();
        var split = CentAllocation.Allocate(request.Amount.Amount, [.. panel.Select(p => p.SignedLine)]);

        var adjustmentId = Guid.CreateVersion7();
        var rows = new List<RecoveryAdjustmentRow>();
        for (var i = 0; i < panel.Count; i++)
        {
            rows.Add(new RecoveryAdjustmentRow
            {
                AdjustmentRowId = Guid.CreateVersion7(), AdjustmentId = adjustmentId, LegalEntityId = legalEntity.Value, ContractId = contract.ContractId.Value,
                ContractYear = contract.ContractYear, ClaimId = claim, LayerId = layer.LayerId, LayerNo = layer.LayerNo, ParticipantPartyId = Guid.Parse(panel[i].ParticipantId),
                IncurredDelta = split[i], Reason = request.Reason.Trim(), RecordedBy = context.Actor.ToString(), AuthorityCheckId = check.CheckId.Value, RecordedAt = now,
            });
        }

        db.RecoveryAdjustments.AddRange(rows);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new RecoveryAdjustResponse
        {
            Item = new RecoveryAdjustmentItem
            {
                AdjustmentId = adjustmentId, ContractId = contract.ContractId, ClaimId = new ClaimId(claim), LayerId = new RiLayerId(layer.LayerId),
                Amount = request.Amount, Reason = request.Reason.Trim(), RecordedAt = now,
                Parts = [.. rows.Select(r => new RecoveryAdjustmentItem.PartItem { ParticipantPartyId = new PartyId(r.ParticipantPartyId), Amount = new Money(r.IncurredDelta, currency) })],
            },
        };
    }
}

/// <summary>Audit facts of <c>ri.Recovery.adjust</c>.</summary>
internal sealed class AdjustRecoveryAuditor : ICommandAuditor<AdjustRecovery, RecoveryAdjustResponse>
{
    public CommandAuditFacts Describe(AdjustRecovery command, Result<RecoveryAdjustResponse>? result) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.RI, "Contract", command.Request.ContractId),
        BusinessKeys = BusinessKeys.Empty.With("riContractId", command.Request.ContractId.Value.ToString("D")),
        Changes = result is { IsSuccess: true } ok
            ? AuditDiff.Compute(null, new { adjustmentId = ok.Value.Item.AdjustmentId, amount = ok.Value.Item.Amount.ToString(), reason = ok.Value.Item.Reason })
            : [],
    };
}

/// <summary>
/// <c>ri.AggregateTracker.get</c> (REQ-RI-152): the aggregate position per layer of a contract year, from the booked recovery
/// deltas plus the manual adjustments, after the placed percentage. Scoped to the caller's legal entity.
/// </summary>
internal sealed class AggregateTrackerReader(ReinsuranceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
{
    public async Task<AggregateTrackerGetResponse> GetAsync(int year, string? layer, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var eur = Currency.FromCode("EUR");
        var contracts = await db.Contracts.AsNoTracking().Where(c => c.LegalEntityId == legalEntity && c.ContractYear == year).ToListAsync(cancellationToken).ConfigureAwait(false);
        var contractIds = contracts.Select(c => c.ContractId).ToList();
        var layers = await (from l in db.Layers.AsNoTracking()
                            join v in db.Versions.AsNoTracking() on l.VersionId equals v.VersionId
                            where contractIds.Contains(v.ContractId) && v.KnownTo == null && l.Rev == v.ContentRev
                            select new { v.ContractId, l.LayerId, l.LayerNo, l.Aal }).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(layer))
        {
            layers = Guid.TryParse(layer, out var layerId)
                ? [.. layers.Where(l => l.LayerId == layerId)]
                : int.TryParse(layer, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var layerNo) ? [.. layers.Where(l => l.LayerNo == layerNo)] : [];
        }

        var rawContractIds = contractIds.Select(c => c.Value).ToList();
        var booked = await db.Recoveries.AsNoTracking().Where(r => rawContractIds.Contains(r.ContractId))
            .GroupBy(r => r.LayerId).Select(g => new { LayerId = g.Key, Incurred = g.Sum(r => r.IncurredDelta), Paid = g.Sum(r => r.PaidDelta) })
            .ToDictionaryAsync(g => g.LayerId, cancellationToken).ConfigureAwait(false);
        var adjusted = await db.RecoveryAdjustments.AsNoTracking().Where(r => rawContractIds.Contains(r.ContractId))
            .GroupBy(r => r.LayerId).Select(g => new { LayerId = g.Key, Incurred = g.Sum(r => r.IncurredDelta) })
            .ToDictionaryAsync(g => g.LayerId, g => g.Incurred, cancellationToken).ConfigureAwait(false);
        var yearOf = contracts.ToDictionary(c => c.ContractId, c => c.ContractYear);

        return new AggregateTrackerGetResponse
        {
            Schedule =
            [
                .. layers.OrderBy(l => l.ContractId.Value).ThenBy(l => l.LayerNo).Select(l =>
                {
                    var b = booked.GetValueOrDefault(l.LayerId);
                    var adjustments = adjusted.GetValueOrDefault(l.LayerId);
                    var incurred = (b?.Incurred ?? 0m) + adjustments;
                    var paid = b?.Paid ?? 0m;
                    return new AggregateTrackerRow
                    {
                        ContractId = l.ContractId, ContractYear = yearOf[l.ContractId], LayerId = new RiLayerId(l.LayerId), LayerNo = l.LayerNo,
                        AggregateLimit = l.Aal is { } aal ? new Money(aal, eur) : null,
                        RecoverableIncurred = new Money(incurred, eur), RecoverablePaid = new Money(paid, eur),
                        RecoverableOutstanding = new Money(incurred - paid, eur), Adjustments = new Money(adjustments, eur),
                    };
                }),
            ],
        };
    }
}
