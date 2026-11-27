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

/// <summary>Contract and layer checks shared by the premium accounting commands.</summary>
internal static class AccountingGuards
{
    /// <summary>
    /// Null when the contract may carry premium accounting: approved, active or expired (premium is settled after the year), the layer
    /// exists in its latest approved terms and the money is in the contract currency.
    /// </summary>
    public static async Task<DomainError?> CheckLayerAsync(ReinsuranceDbContext db, ContractRow contract, int? layerNo, Money? money, CancellationToken cancellationToken)
    {
        var status = ContractStateModel.Parse(contract.Status);
        if (status is not (ContractStatus.Approved or ContractStatus.Active or ContractStatus.Expired))
        {
            return RiErrors.State(status, "given premium accounting");
        }

        if (layerNo is { } layer)
        {
            var latest = await ContractVersions.LatestAsync(db, contract.ContractId, cancellationToken).ConfigureAwait(false);
            var content = await ContractSupport.ReadContentAsync(db, contract, latest, cancellationToken).ConfigureAwait(false);
            if (content.Layers.All(l => l.LayerNo != layer))
            {
                return RiErrors.Validation("layerNo", "LAYER_NOT_FOUND", $"The contract has no layer {layer}.");
            }
        }

        if (money is { } amount && amount.Currency.Code != contract.Currency.Trim())
        {
            return RiErrors.Validation("currency", "CURRENCY_MISMATCH", $"Amounts must be in the contract currency {contract.Currency.Trim()}.");
        }

        return null;
    }

    public static DomainError NotFound(string what) => DomainError.Of(ModuleCode.RI, "NOT-FOUND", $"The {what} does not exist in your legal entity.");

    public static DomainError State(string detail) => DomainError.Of(ModuleCode.RI, "STATE", detail);

    /// <summary>A money amount that must be positive (or zero when allowed) and in whole cents.</summary>
    public static DomainError? CheckCents(string field, Money money, bool allowZero)
    {
        if (money.IsNegative || (!allowZero && money.IsZero) || !money.IsRoundedToMinorUnits)
        {
            return RiErrors.Validation(field, "AMOUNT_INVALID", $"{field} must be {(allowZero ? "zero or more" : "more than zero")} in whole cents.");
        }

        return null;
    }
}

/// <summary><c>ri.DepositSchedule.reschedule</c> as a command of the platform pipeline.</summary>
internal sealed record RescheduleDeposit(DepositScheduleRescheduleRequest Request) : ICommand<DepositScheduleRescheduleResponse>;

/// <summary>Shape rules: instalments and interval are in range, a reason is given.</summary>
internal sealed class RescheduleDepositValidator : AbstractValidator<RescheduleDeposit>
{
    public RescheduleDepositValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.LayerNo).GreaterThanOrEqualTo(1).When(c => c.Request is not null);
        RuleFor(c => c.Request.InstalmentCount).InclusiveBetween(1, 12).When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(1024).When(c => c.Request is not null);
        RuleFor(c => c.Request.ExpectedRevision).GreaterThanOrEqualTo(0).When(c => c.Request?.ExpectedRevision is not null);
    }
}

/// <summary>
/// Sets or replaces the minimum and deposit premium schedule of a contract layer for the contract's year (BO-RI-10, REQ-RI-153..159).
/// A schedule is a revision row, never an update: the reschedule is the next revision. The deposit splits over the instalments with the
/// shared largest-remainder allocator, so the instalments sum exactly to the deposit. Refused once an adjustment premium was calculated
/// for the layer (the adjustment is bound to the schedule it was calculated against).
/// </summary>
internal sealed class RescheduleDepositHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities) : ICommandHandler<RescheduleDeposit, DepositScheduleRescheduleResponse>
{
    public async Task<Result<DepositScheduleRescheduleResponse>> HandleAsync(RescheduleDeposit command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var contract = await ContractSupport.LockAsync(db, legalEntity, request.ContractId, cancellationToken).ConfigureAwait(false);
        if (contract is null)
        {
            return RiErrors.NotFound();
        }

        if (await AccountingGuards.CheckLayerAsync(db, contract, request.LayerNo, request.DepositPremium, cancellationToken).ConfigureAwait(false) is { } guard)
        {
            return guard;
        }

        if (request.MinimumPremium.Currency != request.DepositPremium.Currency)
        {
            return RiErrors.Validation("minimumPremium", "CURRENCY_MISMATCH", "The minimum and the deposit premium are in one currency.");
        }

        if ((AccountingGuards.CheckCents("depositPremium", request.DepositPremium, allowZero: false)
            ?? AccountingGuards.CheckCents("minimumPremium", request.MinimumPremium, allowZero: true)) is { } amountError)
        {
            return amountError;
        }

        if (request.MinimumPremium.Amount > request.DepositPremium.Amount)
        {
            return RiErrors.Validation("minimumPremium", "MINIMUM_ABOVE_DEPOSIT", "The minimum premium cannot be above the deposit premium.");
        }

        var interval = (int)request.IntervalMonths;
        if (!AccountingMath.IntervalsMonths.Contains(interval))
        {
            return RiErrors.Validation("intervalMonths", "INTERVAL_MONTHS", "The interval is 1, 2, 3, 4, 6 or 12 months.");
        }

        var layer = request.LayerNo;
        var latest = await db.DepositSchedules.AsNoTracking().Where(s => s.ContractId == contract.ContractId && s.LayerNo == layer)
            .OrderByDescending(s => s.Revision).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (request.ExpectedRevision is { } expected && expected != (latest?.Revision ?? 0))
        {
            return RiErrors.Stale($"The schedule changed meanwhile (current revision {latest?.Revision ?? 0}).");
        }

        if (await db.Adjustments.AnyAsync(a => a.ContractId == contract.ContractId && a.LayerNo == layer, cancellationToken).ConfigureAwait(false))
        {
            return AccountingGuards.State("An adjustment premium was already calculated for this layer; the schedule can no longer change.");
        }

        var instalments = AccountingMath.Instalments(request.DepositPremium.Amount, request.InstalmentCount, request.FirstDueDate, interval);
        var row = new DepositScheduleRow
        {
            ScheduleId = Guid.CreateVersion7(), LegalEntityId = legalEntity, ContractId = contract.ContractId, LayerNo = layer, ContractYear = contract.ContractYear,
            Revision = (latest?.Revision ?? 0) + 1, Currency = contract.Currency.Trim(), MinimumPremium = request.MinimumPremium.Amount, DepositPremium = request.DepositPremium.Amount,
            Instalments = AccountingViews.ToJson(instalments), Reason = request.Reason, CreatedAt = clock.Now, CreatedBy = context.Actor.ToString(),
        };
        db.DepositSchedules.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return RiErrors.Stale("The schedule changed meanwhile.");
        }

        return new DepositScheduleRescheduleResponse { Schedule = AccountingViews.ToView(row, current: true) };
    }
}

/// <summary>Audit facts of <c>ri.DepositSchedule.reschedule</c>.</summary>
internal sealed class RescheduleDepositAuditor : ICommandAuditor<RescheduleDeposit, DepositScheduleRescheduleResponse>
{
    public CommandAuditFacts Describe(RescheduleDeposit command, Result<DepositScheduleRescheduleResponse>? result)
    {
        var subject = ObjectRef.For(ModuleCode.RI, "Contract", command.Request.ContractId);
        var keys = BusinessKeys.Empty.With("riContractId", command.Request.ContractId.Value.ToString("D"));
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = subject, BusinessKeys = keys };
        }

        var schedule = ok.Value.Schedule;
        return new CommandAuditFacts
        {
            ObjectRef = subject,
            BusinessKeys = keys.With("scheduleId", schedule.ScheduleId.ToString("D")),
            Changes = AuditDiff.Compute(
                new { layerNo = schedule.LayerNo, revision = schedule.Revision - 1 },
                new { layerNo = schedule.LayerNo, revision = schedule.Revision, deposit = schedule.DepositPremium.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture), reason = command.Request.Reason }),
        };
    }
}

/// <summary>The deposit schedule and adjustment queries (<c>ri.DepositSchedule.get</c>), scoped to the caller's legal entity.</summary>
internal sealed class DepositScheduleReader(ReinsuranceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
{
    public async Task<Result<DepositScheduleGetResponse>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var row = await db.DepositSchedules.AsNoTracking().SingleOrDefaultAsync(s => s.ScheduleId == id && s.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return AccountingGuards.NotFound("deposit schedule");
        }

        var current = !await db.DepositSchedules.AnyAsync(s => s.ContractId == row.ContractId && s.LayerNo == row.LayerNo && s.Revision > row.Revision, cancellationToken).ConfigureAwait(false);
        var adjustment = await db.Adjustments.AsNoTracking().Where(a => a.ContractId == row.ContractId && a.LayerNo == row.LayerNo && a.ContractYear == row.ContractYear)
            .OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        AccountingDecisionRow? decision = null;
        if (adjustment is not null)
        {
            decision = await db.AccountingDecisions.AsNoTracking().SingleOrDefaultAsync(d => d.Kind == "ADJUSTMENT" && d.SubjectId == adjustment.AdjustmentId, cancellationToken).ConfigureAwait(false);
        }

        return new DepositScheduleGetResponse
        {
            Schedule = AccountingViews.ToView(row, current),
            Adjustment = adjustment is null ? null : AccountingViews.ToView(adjustment, decision),
        };
    }
}
