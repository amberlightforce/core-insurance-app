using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Billing.Commands;

/// <summary><c>bil.PaymentPlan.select</c>: sets the plan, method and due day of a term that is not billed yet (BO-BIL-03).</summary>
internal sealed record SelectPaymentPlan(PaymentPlanSelectRequest Request) : ICommand<PaymentPlanSelectResponse>;

/// <summary><c>bil.PaymentPlan.change</c>: changes a term's method and due day (and its plan while nothing is billed) (BO-BIL-03).</summary>
internal sealed record ChangePaymentPlan(PaymentPlanChangeRequest Request) : ICommand<PaymentPlanChangeResponse>;

internal static class PaymentPlanRules
{
    public const string DirectDebit = "DIRECT_DEBIT";

    /// <summary>Methods a term may be paid by; DIRECT_DEBIT needs an ACTIVE SEPA mandate on the term's billing account (BO-BIL-06a).</summary>
    public static readonly string[] Methods = [ReceiptCodes.BankTransfer, ReceiptCodes.Cashier, DirectDebit];

    public static void Common<T>(AbstractValidator<T> validator, Func<T, string> plan, Func<T, string> method, Func<T, int?> dueDay, Func<T, Guid?> term, Func<T, Guid?> job)
    {
        validator.RuleFor(c => plan(c)).NotEmpty().MaximumLength(40);
        validator.RuleFor(c => method(c)).NotEmpty().MaximumLength(40);
        validator.RuleFor(c => dueDay(c)).InclusiveBetween(1, 31).When(c => dueDay(c) is not null);
        validator.RuleFor(c => c).Must(c => (term(c) is null) != (job(c) is null)).WithName("termId").WithMessage("Give exactly one of termId and jobId.");
    }

    /// <summary>The term's plan instance after the common checks, or the refusal.</summary>
    public static async Task<Result<(PlanInstanceRow Plan, string Currency)>> LoadAsync(
        BillingDbContext db, LegalEntityId legalEntity, Guid? termId, Guid? jobId, string planCode, string method, CancellationToken cancellationToken)
    {
        if (termId is null || jobId is not null)
        {
            return DomainError.Of(ModuleCode.BIL, "VALIDATION", "A plan is selected by termId; a job has no billing term until it is bound.");
        }

        if (PaymentPlans.InstalmentCount(planCode) == 0)
        {
            return DomainError.Of(ModuleCode.BIL, "NOT-ELIGIBLE", $"The plan '{planCode}' is not offered.");
        }

        if (!Methods.Contains(method, StringComparer.Ordinal))
        {
            return DomainError.Of(ModuleCode.BIL, "NOT-ELIGIBLE", $"The method '{method}' is not available for a payment plan.");
        }

        var id = new PolicyTermId(termId.Value);
        var plan = await db.PlanInstances.SingleOrDefaultAsync(p => p.TermId == id && p.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return BillingErrors.NotFound("term");
        }

        if (plan.CancelledEffective is not null)
        {
            return DomainError.Of(ModuleCode.BIL, "NOT-ELIGIBLE", "The term is cancelled; its plan can no longer change.");
        }

        if (string.Equals(method, DirectDebit, StringComparison.Ordinal))
        {
            var active = Codes.Of(MandateStatus.Active);
            if (!await db.Mandates.AsNoTracking().AnyAsync(m => m.BillingAccountId == plan.BillingAccountId && m.LegalEntityId == legalEntity && m.Status == active, cancellationToken).ConfigureAwait(false))
            {
                return DomainError.Of(ModuleCode.BIL, "NOT-ELIGIBLE", "Direct debit needs an active signed mandate on the billing account.");
            }
        }

        var currency = await db.Accounts.AsNoTracking().Where(a => a.BillingAccountId == plan.BillingAccountId).Select(a => a.Currency).SingleAsync(cancellationToken).ConfigureAwait(false);
        return (plan, currency);
    }

    /// <summary>Applies the choice (a billed term keeps its plan) and returns the schedule the term is or will be billed in.</summary>
    public static async Task<Result<PaymentPlanSelectResponse>> ApplyAsync(
        BillingDbContext db, PaymentPlanReader reader, IClock clock, IOptions<BillingOptions> options, PlanInstanceRow plan, string currencyCode,
        string planCode, string method, int? dueDay, bool change, CancellationToken cancellationToken)
    {
        var billed = await db.Invoices.AnyAsync(i => i.TermId == plan.TermId, cancellationToken).ConfigureAwait(false);
        if (billed && (!change || !string.Equals(plan.PlanCode, planCode, StringComparison.Ordinal)))
        {
            // TODO(hardening): re-spread the unpaid instalments of a billed term onto the new plan (void and reissue the open invoices).
            return DomainError.Of(ModuleCode.BIL, "NOT-ELIGIBLE", change
                ? "The term is already billed; only its method and due day can change."
                : "The term is already billed; use a plan change for its method and due day.");
        }

        plan.PlanCode = planCode;
        plan.Method = method;
        plan.DueDay = dueDay;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var charges = await reader.TermChargesAsync(plan, cancellationToken).ConfigureAwait(false);
        var lines = PlanSpread.Compute(planCode, plan.TermFrom, clock.Now.ToBusinessDate(options.Value.Zone), dueDay, charges);
        var currency = Currency.FromCode(currencyCode);
        return new PaymentPlanSelectResponse
        {
            PlanInstanceId = plan.PlanInstanceId,
            PlanCode = planCode,
            PlanVersion = PaymentPlanReader.PlanVersion,
            DownPayment = lines.Count > 1 ? new Money(lines[0].Amount, currency) : null,
            Schedule = [.. lines.Select(l => new PaymentPlanSelectResponse.ScheduleItem { DueDate = l.Due, Amount = new Money(l.Amount, currency) })],
        };
    }
}

internal sealed class SelectPaymentPlanValidator : AbstractValidator<SelectPaymentPlan>
{
    public SelectPaymentPlanValidator() =>
        PaymentPlanRules.Common(this, c => c.Request.PlanCode, c => c.Request.Method, c => c.Request.DueDay, c => c.Request.TermId?.Value, c => c.Request.JobId?.Value);
}

internal sealed class ChangePaymentPlanValidator : AbstractValidator<ChangePaymentPlan>
{
    public ChangePaymentPlanValidator() =>
        PaymentPlanRules.Common(this, c => c.Request.PlanCode, c => c.Request.Method, c => c.Request.DueDay, c => c.Request.TermId?.Value, c => c.Request.JobId?.Value);
}

internal sealed class SelectPaymentPlanHandler(
    BillingDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, PaymentPlanReader reader, IClock clock, IOptions<BillingOptions> options)
    : ICommandHandler<SelectPaymentPlan, PaymentPlanSelectResponse>
{
    public async Task<Result<PaymentPlanSelectResponse>> HandleAsync(SelectPaymentPlan command, CancellationToken cancellationToken)
    {
        var r = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
        var loaded = await PaymentPlanRules.LoadAsync(db, legalEntity, r.TermId?.Value, r.JobId?.Value, r.PlanCode, r.Method, cancellationToken).ConfigureAwait(false);
        return loaded.IsFailure
            ? loaded.Error!
            : await PaymentPlanRules.ApplyAsync(db, reader, clock, options, loaded.Value.Plan, loaded.Value.Currency, r.PlanCode, r.Method, r.DueDay, change: false, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class ChangePaymentPlanHandler(
    BillingDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, PaymentPlanReader reader, IClock clock, IOptions<BillingOptions> options)
    : ICommandHandler<ChangePaymentPlan, PaymentPlanChangeResponse>
{
    public async Task<Result<PaymentPlanChangeResponse>> HandleAsync(ChangePaymentPlan command, CancellationToken cancellationToken)
    {
        var r = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
        var loaded = await PaymentPlanRules.LoadAsync(db, legalEntity, r.TermId?.Value, r.JobId?.Value, r.PlanCode, r.Method, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var applied = await PaymentPlanRules.ApplyAsync(db, reader, clock, options, loaded.Value.Plan, loaded.Value.Currency, r.PlanCode, r.Method, r.DueDay, change: true, cancellationToken).ConfigureAwait(false);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        var a = applied.Value;
        return new PaymentPlanChangeResponse
        {
            PlanInstanceId = a.PlanInstanceId,
            PlanCode = a.PlanCode,
            PlanVersion = a.PlanVersion,
            DownPayment = a.DownPayment,
            Schedule = [.. a.Schedule.Select(s => new PaymentPlanChangeResponse.ScheduleItem { DueDate = s.DueDate, Amount = s.Amount })],
        };
    }
}

internal sealed class PaymentPlanAuditor<TCommand, TResponse> : ICommandAuditor<TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    public CommandAuditFacts Describe(TCommand command, Result<TResponse>? result)
    {
        var (term, plan, method, dueDay) = command switch
        {
            SelectPaymentPlan c => (c.Request.TermId?.Value, c.Request.PlanCode, c.Request.Method, c.Request.DueDay),
            ChangePaymentPlan c => (c.Request.TermId?.Value, c.Request.PlanCode, c.Request.Method, c.Request.DueDay),
            _ => ((Guid?)null, string.Empty, string.Empty, (int?)null),
        };
        var key = term?.ToString("D") ?? string.Empty;
        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.BIL, "PlanInstance", key),
            BusinessKeys = BusinessKeys.Empty.With("termId", key),
            Changes = result is { IsSuccess: true } ? AuditDiff.Compute(null, new { operation = typeof(TCommand).Name, plan, method, dueDay }) : [],
        };
    }
}
