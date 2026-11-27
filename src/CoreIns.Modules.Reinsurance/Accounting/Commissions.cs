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

/// <summary><c>ri.Commission.adjust</c> as a command of the platform pipeline.</summary>
internal sealed record AdjustCommission(CommissionAdjustRequest Request) : ICommand<CommissionAdjustResponse>;

/// <summary>Shape rules: rates are percentages, the scale has distinct steps.</summary>
internal sealed class AdjustCommissionValidator : AbstractValidator<AdjustCommission>
{
    public AdjustCommissionValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.ProvisionalRatePct).InclusiveBetween(0m, 100m).When(c => c.Request is not null);
        RuleFor(c => c.Request.Scale).NotEmpty().Must(scale => scale.Count <= 50).When(c => c.Request is not null);
        RuleForEach(c => c.Request.Scale).ChildRules(step =>
        {
            step.RuleFor(s => s.CommissionRatePct).InclusiveBetween(0m, 100m);
            step.RuleFor(s => s.LossRatioFromPct).InclusiveBetween(0m, 10000m);
        }).When(c => c.Request?.Scale is not null);
        RuleFor(c => c.Request.Reason).MaximumLength(1024).When(c => c.Request is not null);
    }
}

/// <summary>
/// Calculates a sliding-scale commission for a contract year (BO-RI-10, REQ-RI-160..166): the loss ratio (incurred loss over ceded premium)
/// picks the scale step, the earned commission is the ceded premium at that rate, and the adjustment is the earned commission less the
/// provisional commission already allowed. The row is append-only (the table is the history) and PENDING until decided once through PLT
/// (<c>RI.ADJUSTMENT_APPROVE</c>, on the absolute adjustment amount). Several calculations may exist for a year; a new one is refused while
/// another is pending.
/// </summary>
internal sealed class AdjustCommissionHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    AccountingApprovals approvals) : ICommandHandler<AdjustCommission, CommissionAdjustResponse>
{
    public async Task<Result<CommissionAdjustResponse>> HandleAsync(AdjustCommission command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var contract = await ContractSupport.LockAsync(db, legalEntity, request.ContractId, cancellationToken).ConfigureAwait(false);
        if (contract is null)
        {
            return RiErrors.NotFound();
        }

        if (await AccountingGuards.CheckLayerAsync(db, contract, layerNo: null, request.CededPremium, cancellationToken).ConfigureAwait(false) is { } guard)
        {
            return guard;
        }

        if (request.IncurredLoss.Currency != request.CededPremium.Currency)
        {
            return RiErrors.Validation("incurredLoss", "CURRENCY_MISMATCH", "The ceded premium and the incurred loss are in one currency.");
        }

        if ((AccountingGuards.CheckCents("cededPremium", request.CededPremium, allowZero: false) ?? AccountingGuards.CheckCents("incurredLoss", request.IncurredLoss, allowZero: true)) is { } amountError)
        {
            return amountError;
        }

        var scale = request.Scale.Select(s => (From: s.LossRatioFromPct, Rate: s.CommissionRatePct)).OrderBy(s => s.From).ToList();
        if (scale.Select(s => s.From).Distinct().Count() != scale.Count)
        {
            return RiErrors.Validation("scale", "SCALE_DUPLICATE_STEP", "The scale has two steps from the same loss ratio.");
        }

        var lossRatio = AccountingMath.LossRatioPct(request.IncurredLoss.Amount, request.CededPremium.Amount)!.Value;
        if (AccountingMath.EarnedRatePct(lossRatio, scale) is not { } earnedRate)
        {
            return RiErrors.Validation("scale", "SCALE_NO_STEP", $"No scale step applies to the loss ratio {lossRatio}%; the scale must start at 0%.");
        }

        var year = contract.ContractYear;
        var earlier = await db.Commissions.AsNoTracking().Where(c => c.ContractId == contract.ContractId && c.ContractYear == year).Select(c => c.CommissionId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var decided = await db.AccountingDecisions.AsNoTracking().Where(d => d.Kind == "COMMISSION" && earlier.Contains(d.SubjectId)).Select(d => d.SubjectId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (earlier.Any(x => !decided.Contains(x)))
        {
            return AccountingGuards.State("Another commission calculation of this contract year is still pending.");
        }

        var provisional = AccountingMath.Commission(request.CededPremium.Amount, request.ProvisionalRatePct);
        var earned = AccountingMath.Commission(request.CededPremium.Amount, earnedRate);
        var adjustment = earned - provisional;
        var id = Guid.CreateVersion7();
        var hash = HashOf(id, contract.ContractId.Value, request.CededPremium.Amount, request.IncurredLoss.Amount, lossRatio, request.ProvisionalRatePct, earnedRate, adjustment);
        Guid requestId;
        try
        {
            requestId = await approvals.RequestAsync(
                AccountingApprovals.CommissionSubject, id, hash, new Money(adjustment, request.CededPremium.Currency), ContractSupport.ActorKeys(context),
                $"Reinsurance commission adjustment of contract {contract.ContractNumber}, year {year}",
                new
                {
                    contractNumber = contract.ContractNumber, contractYear = year, cededPremium = AccountingViews.Fixed(request.CededPremium.Amount),
                    incurredLoss = AccountingViews.Fixed(request.IncurredLoss.Amount), lossRatioPct = AccountingViews.Rate(lossRatio),
                    provisionalRatePct = request.ProvisionalRatePct.ToString(CultureInfo.InvariantCulture), earnedRatePct = earnedRate.ToString(CultureInfo.InvariantCulture),
                    adjustmentAmount = AccountingViews.Fixed(adjustment),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            return ex.Error;
        }

        var row = new CommissionRow
        {
            CommissionId = id, LegalEntityId = legalEntity, ContractId = contract.ContractId, ContractYear = year, Currency = contract.Currency.Trim(),
            CededPremium = request.CededPremium.Amount, IncurredLoss = request.IncurredLoss.Amount, LossRatioPct = lossRatio, ProvisionalRatePct = request.ProvisionalRatePct,
            EarnedRatePct = earnedRate, ProvisionalCommission = provisional, EarnedCommission = earned, AdjustmentAmount = adjustment, Scale = AccountingViews.ToJson(scale),
            ApprovalRequestId = requestId, Reason = request.Reason, CreatedAt = clock.Now, CreatedBy = context.Actor.ToString(),
        };
        db.Commissions.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new CommissionAdjustResponse { Commission = AccountingViews.ToView(row, null) };
    }

    /// <summary>The hash PLT binds the approval to: the exact facts of the calculation (the approver recomputes it from the stored row).</summary>
    public static Sha256Hash HashOf(Guid id, Guid contractId, decimal ceded, decimal incurred, decimal lossRatio, decimal provisionalRate, decimal earnedRate, decimal adjustment) =>
        AccountingApprovals.HashOf(
            AccountingApprovals.CommissionSubject, id,
            contractId.ToString("D"), AccountingViews.Fixed(ceded), AccountingViews.Fixed(incurred), AccountingViews.Rate(lossRatio),
            AccountingViews.Rate(provisionalRate), AccountingViews.Rate(earnedRate), AccountingViews.Fixed(adjustment));
}

/// <summary>Audit facts of <c>ri.Commission.adjust</c>.</summary>
internal sealed class AdjustCommissionAuditor : ICommandAuditor<AdjustCommission, CommissionAdjustResponse>
{
    public CommandAuditFacts Describe(AdjustCommission command, Result<CommissionAdjustResponse>? result)
    {
        var keys = BusinessKeys.Empty.With("riContractId", command.Request.ContractId.Value.ToString("D"));
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.RI, "Contract", command.Request.ContractId), BusinessKeys = keys };
        }

        var commission = ok.Value.Commission;
        return new CommandAuditFacts
        {
            ObjectRef = AccountingApprovals.SubjectOf(AccountingApprovals.CommissionSubject, commission.CommissionId),
            BusinessKeys = keys.With("commissionId", commission.CommissionId.ToString("D")),
            Changes = AuditDiff.Compute(
                new { status = "NONE" },
                new { status = "PENDING", earnedRatePct = commission.EarnedRatePct.ToString(CultureInfo.InvariantCulture), adjustmentAmount = commission.AdjustmentAmount.Amount.ToString(CultureInfo.InvariantCulture) }),
        };
    }
}

/// <summary><c>ri.Commission.approve</c> (APPROVE or REJECT) as a command of the platform pipeline.</summary>
internal sealed record ApproveCommission(CommissionApproveRequest Request) : ICommand<CommissionApproveResponse>;

/// <summary>Shape rules: a rejection needs its reason.</summary>
internal sealed class ApproveCommissionValidator : AbstractValidator<ApproveCommission>
{
    public ApproveCommissionValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Decision).IsInEnum().When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).MaximumLength(1024).When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).NotEmpty().WithErrorCode("REASON_REQUIRED")
            .When(c => c.Request is { Decision: CommissionApproveRequest.DecisionValue.Reject });
    }
}

/// <summary>The checker's decision on a commission calculation, once; same rules as <see cref="ApproveAdjustmentHandler"/>.</summary>
internal sealed class ApproveCommissionHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    AccountingApprovals approvals) : ICommandHandler<ApproveCommission, CommissionApproveResponse>
{
    public async Task<Result<CommissionApproveResponse>> HandleAsync(ApproveCommission command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var row = await db.Commissions.AsNoTracking().SingleOrDefaultAsync(c => c.CommissionId == request.CommissionId && c.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return AccountingGuards.NotFound("commission calculation");
        }

        await ContractSupport.LockAsync(db, legalEntity, row.ContractId, cancellationToken).ConfigureAwait(false);
        if (await db.AccountingDecisions.AnyAsync(d => d.Kind == "COMMISSION" && d.SubjectId == row.CommissionId, cancellationToken).ConfigureAwait(false))
        {
            return AccountingGuards.State("The commission calculation was already decided.");
        }

        if (ContractSupport.ActorKeys(context).Contains(row.CreatedBy, StringComparer.Ordinal))
        {
            return RiErrors.Sod("The person who calculated the commission cannot decide it.");
        }

        var reject = request.Decision == CommissionApproveRequest.DecisionValue.Reject;
        var hash = AdjustCommissionHandler.HashOf(
            row.CommissionId, row.ContractId.Value, row.CededPremium, row.IncurredLoss, row.LossRatioPct, row.ProvisionalRatePct, row.EarnedRatePct, row.AdjustmentAmount);
        var decided = await approvals.DecideAsync(
            AccountingApprovals.CommissionSubject, row.CommissionId, row.ApprovalRequestId, hash, context.Actor.ToString(), reject, request.Reason, cancellationToken).ConfigureAwait(false);
        if (decided.IsFailure)
        {
            return decided.Error!;
        }

        var decision = new AccountingDecisionRow
        {
            Kind = "COMMISSION", SubjectId = row.CommissionId, LegalEntityId = legalEntity, Decision = reject ? "REJECTED" : "APPROVED",
            Reason = request.Reason, DecidedAt = clock.Now, DecidedBy = decided.Value,
        };
        db.AccountingDecisions.Add(decision);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return AccountingGuards.State("The commission calculation was already decided.");
        }

        return new CommissionApproveResponse { Commission = AccountingViews.ToView(row, decision) };
    }
}

/// <summary>Audit facts of <c>ri.Commission.approve</c>.</summary>
internal sealed class ApproveCommissionAuditor : ICommandAuditor<ApproveCommission, CommissionApproveResponse>
{
    public CommandAuditFacts Describe(ApproveCommission command, Result<CommissionApproveResponse>? result)
    {
        var id = command.Request.CommissionId;
        var subject = AccountingApprovals.SubjectOf(AccountingApprovals.CommissionSubject, id);
        var keys = BusinessKeys.Empty.With("commissionId", id.ToString("D"));
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = subject, BusinessKeys = keys };
        }

        return new CommandAuditFacts
        {
            ObjectRef = subject,
            BusinessKeys = keys.With("riContractId", ok.Value.Commission.ContractId.Value.ToString("D")),
            Changes = AuditDiff.Compute(new { status = "PENDING" }, new { status = ok.Value.Commission.Status.ToString().ToUpperInvariant(), reason = command.Request.Reason }),
        };
    }
}

/// <summary><c>ri.Commission.history</c>: the append-only calculations, newest first, offset cursor, scoped to the caller's legal entity.</summary>
internal sealed class CommissionHistoryReader(ReinsuranceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public async Task<Result<CommissionHistoryPage>> ListAsync(Guid? contractId, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var size = limit ?? DefaultLimit;
        if (size is < 1 or > MaxLimit)
        {
            return RiErrors.Validation("limit", "LIMIT_RANGE", $"The limit is between 1 and {MaxLimit}.");
        }

        var offset = 0;
        if (cursor is not null && !int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset))
        {
            return RiErrors.Validation("cursor", "CURSOR", "The cursor is not valid.");
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var query = db.Commissions.AsNoTracking().Where(c => c.LegalEntityId == legalEntity);
        if (contractId is { } contract)
        {
            var typed = new RiContractId(contract);
            query = query.Where(c => c.ContractId == typed);
        }

        var rows = await query.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.CommissionId).Skip(offset).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(size).ToList();
        var ids = page.Select(r => r.CommissionId).ToList();
        var decisions = (await db.AccountingDecisions.AsNoTracking().Where(d => d.Kind == "COMMISSION" && ids.Contains(d.SubjectId)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(d => d.SubjectId);
        return new CommissionHistoryPage
        {
            Items = [.. page.Select(r => new CommissionHistoryItem { Commission = AccountingViews.ToView(r, decisions.GetValueOrDefault(r.CommissionId)) })],
            NextCursor = rows.Count > size ? (offset + size).ToString(CultureInfo.InvariantCulture) : null,
            Limit = size,
        };
    }
}
