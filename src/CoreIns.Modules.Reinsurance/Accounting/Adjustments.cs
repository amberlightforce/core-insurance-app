using System.Globalization;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreIns.Modules.Reinsurance.Accounting;

/// <summary><c>ri.Adjustment.calculate</c> as a command of the platform pipeline.</summary>
internal sealed record CalculateAdjustment(AdjustmentCalculateRequest Request) : ICommand<AdjustmentCalculateResponse>;

/// <summary>Shape rules: the rate is a percentage.</summary>
internal sealed class CalculateAdjustmentValidator : AbstractValidator<CalculateAdjustment>
{
    public CalculateAdjustmentValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.LayerNo).GreaterThanOrEqualTo(1).When(c => c.Request is not null);
        RuleFor(c => c.Request.RatePct).InclusiveBetween(0m, 100m).When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).MaximumLength(1024).When(c => c.Request is not null);
    }
}

/// <summary>
/// Calculates the adjustment premium of a contract layer and year from the final subject premium (BO-RI-10, REQ-RI-153..159): the
/// premium earned at the rate (never below the minimum premium) less the deposit premium of the layer's current schedule. The result is a
/// PENDING append-only row with its PLT approval request (<c>RI.ADJUSTMENT_APPROVE</c>, amount authority); a layer is adjusted once: a
/// pending or approved adjustment blocks another, a rejected one allows a new calculation.
/// </summary>
internal sealed class CalculateAdjustmentHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    AccountingApprovals approvals) : ICommandHandler<CalculateAdjustment, AdjustmentCalculateResponse>
{
    public async Task<Result<AdjustmentCalculateResponse>> HandleAsync(CalculateAdjustment command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var contract = await ContractSupport.LockAsync(db, legalEntity, request.ContractId, cancellationToken).ConfigureAwait(false);
        if (contract is null)
        {
            return RiErrors.NotFound();
        }

        if (await AccountingGuards.CheckLayerAsync(db, contract, request.LayerNo, request.SubjectPremium, cancellationToken).ConfigureAwait(false) is { } guard)
        {
            return guard;
        }

        if (AccountingGuards.CheckCents("subjectPremium", request.SubjectPremium, allowZero: true) is { } amountError)
        {
            return amountError;
        }

        var layer = request.LayerNo;
        var schedule = await db.DepositSchedules.AsNoTracking().Where(s => s.ContractId == contract.ContractId && s.LayerNo == layer)
            .OrderByDescending(s => s.Revision).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (schedule is null)
        {
            return AccountingGuards.State("The layer has no deposit schedule; set it first (ri.DepositSchedule.reschedule).");
        }

        var earlier = await db.Adjustments.AsNoTracking().Where(a => a.ContractId == contract.ContractId && a.LayerNo == layer).Select(a => a.AdjustmentId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var decided = await db.AccountingDecisions.AsNoTracking().Where(d => d.Kind == "ADJUSTMENT" && earlier.Contains(d.SubjectId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var open = earlier.Any(x => decided.FirstOrDefault(d => d.SubjectId == x) is not { Decision: "REJECTED" });
        if (open)
        {
            return AccountingGuards.State("The layer already has a pending or approved adjustment premium.");
        }

        var (calculated, adjustment) = AccountingMath.Adjustment(request.SubjectPremium.Amount, request.RatePct, schedule.MinimumPremium, schedule.DepositPremium);
        var id = Guid.CreateVersion7();
        var actor = context.Actor.ToString();
        var hash = HashOf(id, contract.ContractId.Value, layer, schedule.ScheduleId, request.SubjectPremium.Amount, request.RatePct, calculated, adjustment);
        Guid requestId;
        try
        {
            requestId = await approvals.RequestAsync(
                AccountingApprovals.AdjustmentSubject, id, hash, new Money(adjustment, request.SubjectPremium.Currency), ContractSupport.ActorKeys(context),
                $"Reinsurance adjustment premium of contract {contract.ContractNumber} layer {layer}, year {contract.ContractYear}",
                new
                {
                    contractNumber = contract.ContractNumber, layerNo = layer, contractYear = contract.ContractYear, scheduleRevision = schedule.Revision,
                    subjectPremium = AccountingViews.Fixed(request.SubjectPremium.Amount), ratePct = request.RatePct.ToString(CultureInfo.InvariantCulture),
                    calculatedPremium = AccountingViews.Fixed(calculated), minimumPremium = AccountingViews.Fixed(schedule.MinimumPremium),
                    depositPremium = AccountingViews.Fixed(schedule.DepositPremium), adjustmentAmount = AccountingViews.Fixed(adjustment),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            return ex.Error;
        }

        var row = new AdjustmentRow
        {
            AdjustmentId = id, LegalEntityId = legalEntity, ContractId = contract.ContractId, LayerNo = layer, ContractYear = contract.ContractYear, ScheduleId = schedule.ScheduleId,
            Currency = contract.Currency.Trim(), SubjectPremium = request.SubjectPremium.Amount, RatePct = request.RatePct, CalculatedPremium = calculated,
            MinimumPremium = schedule.MinimumPremium, DepositPremium = schedule.DepositPremium, AdjustmentAmount = adjustment, ApprovalRequestId = requestId,
            Reason = request.Reason, CreatedAt = clock.Now, CreatedBy = actor,
        };
        db.Adjustments.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new AdjustmentCalculateResponse { Adjustment = AccountingViews.ToView(row, null) };
    }

    /// <summary>The hash PLT binds the approval to: the exact facts of the calculation (the approver recomputes it from the stored row).</summary>
    public static Sha256Hash HashOf(Guid id, Guid contractId, int layer, Guid scheduleId, decimal subject, decimal rate, decimal calculated, decimal adjustment) =>
        AccountingApprovals.HashOf(
            AccountingApprovals.AdjustmentSubject, id,
            contractId.ToString("D"), layer.ToString(CultureInfo.InvariantCulture), scheduleId.ToString("D"), AccountingViews.Fixed(subject),
            AccountingViews.Rate(rate), AccountingViews.Fixed(calculated), AccountingViews.Fixed(adjustment));
}

/// <summary>Audit facts of <c>ri.Adjustment.calculate</c>.</summary>
internal sealed class CalculateAdjustmentAuditor : ICommandAuditor<CalculateAdjustment, AdjustmentCalculateResponse>
{
    public CommandAuditFacts Describe(CalculateAdjustment command, Result<AdjustmentCalculateResponse>? result)
    {
        var contract = command.Request.ContractId.Value.ToString("D");
        var keys = BusinessKeys.Empty.With("riContractId", contract);
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.RI, "Contract", command.Request.ContractId), BusinessKeys = keys };
        }

        var adjustment = ok.Value.Adjustment;
        return new CommandAuditFacts
        {
            ObjectRef = AccountingApprovals.SubjectOf(AccountingApprovals.AdjustmentSubject, adjustment.AdjustmentId),
            BusinessKeys = keys.With("adjustmentId", adjustment.AdjustmentId.ToString("D")),
            Changes = AuditDiff.Compute(
                new { status = "NONE" },
                new { status = "PENDING", layerNo = adjustment.LayerNo, adjustmentAmount = adjustment.AdjustmentAmount.Amount.ToString(CultureInfo.InvariantCulture) }),
        };
    }
}

/// <summary><c>ri.Adjustment.approve</c> (APPROVE or REJECT) as a command of the platform pipeline.</summary>
internal sealed record ApproveAdjustment(AdjustmentApproveRequest Request) : ICommand<AdjustmentApproveResponse>;

/// <summary>Shape rules: a rejection needs its reason.</summary>
internal sealed class ApproveAdjustmentValidator : AbstractValidator<ApproveAdjustment>
{
    public ApproveAdjustmentValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Decision).IsInEnum().When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).MaximumLength(1024).When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).NotEmpty().WithErrorCode("REASON_REQUIRED")
            .When(c => c.Request is { Decision: AdjustmentApproveRequest.DecisionValue.Reject });
    }
}

/// <summary>
/// The checker's decision on an adjustment premium, once: a decided adjustment cannot be decided again (the decision row's key is the
/// adjustment). The person who calculated it cannot decide it (<c>RI-ERR-SOD</c>); PLT checks the checker's amount authority on the
/// absolute adjustment and the hash recomputed from the stored row.
/// </summary>
internal sealed class ApproveAdjustmentHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    AccountingApprovals approvals) : ICommandHandler<ApproveAdjustment, AdjustmentApproveResponse>
{
    public async Task<Result<AdjustmentApproveResponse>> HandleAsync(ApproveAdjustment command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var row = await db.Adjustments.AsNoTracking().SingleOrDefaultAsync(a => a.AdjustmentId == request.AdjustmentId && a.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return AccountingGuards.NotFound("adjustment");
        }

        // The contract row lock serialises the decisions of one contract: a racing second decision waits, then finds the first one.
        await ContractSupport.LockAsync(db, legalEntity, row.ContractId, cancellationToken).ConfigureAwait(false);
        if (await db.AccountingDecisions.AnyAsync(d => d.Kind == "ADJUSTMENT" && d.SubjectId == row.AdjustmentId, cancellationToken).ConfigureAwait(false))
        {
            return AccountingGuards.State("The adjustment was already decided.");
        }

        var keys = ContractSupport.ActorKeys(context);
        if (keys.Contains(row.CreatedBy, StringComparer.Ordinal))
        {
            return RiErrors.Sod("The person who calculated the adjustment cannot decide it.");
        }

        var reject = request.Decision == AdjustmentApproveRequest.DecisionValue.Reject;
        var hash = CalculateAdjustmentHandler.HashOf(
            row.AdjustmentId, row.ContractId.Value, row.LayerNo, row.ScheduleId, row.SubjectPremium, row.RatePct, row.CalculatedPremium, row.AdjustmentAmount);
        var decided = await approvals.DecideAsync(
            AccountingApprovals.AdjustmentSubject, row.AdjustmentId, row.ApprovalRequestId, hash, context.Actor.ToString(), reject, request.Reason, cancellationToken).ConfigureAwait(false);
        if (decided.IsFailure)
        {
            return decided.Error!;
        }

        var decision = new AccountingDecisionRow
        {
            Kind = "ADJUSTMENT", SubjectId = row.AdjustmentId, LegalEntityId = legalEntity, Decision = reject ? "REJECTED" : "APPROVED",
            Reason = request.Reason, DecidedAt = clock.Now, DecidedBy = decided.Value,
        };
        db.AccountingDecisions.Add(decision);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return AccountingGuards.State("The adjustment was already decided.");
        }

        return new AdjustmentApproveResponse { Adjustment = AccountingViews.ToView(row, decision) };
    }
}

/// <summary>Audit facts of <c>ri.Adjustment.approve</c>.</summary>
internal sealed class ApproveAdjustmentAuditor : ICommandAuditor<ApproveAdjustment, AdjustmentApproveResponse>
{
    public CommandAuditFacts Describe(ApproveAdjustment command, Result<AdjustmentApproveResponse>? result)
    {
        var id = command.Request.AdjustmentId;
        var subject = AccountingApprovals.SubjectOf(AccountingApprovals.AdjustmentSubject, id);
        var keys = BusinessKeys.Empty.With("adjustmentId", id.ToString("D"));
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = subject, BusinessKeys = keys };
        }

        return new CommandAuditFacts
        {
            ObjectRef = subject,
            BusinessKeys = keys.With("riContractId", ok.Value.Adjustment.ContractId.Value.ToString("D")),
            Changes = AuditDiff.Compute(new { status = "PENDING" }, new { status = ok.Value.Adjustment.Status.ToString().ToUpperInvariant(), reason = command.Request.Reason }),
        };
    }
}
