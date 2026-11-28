using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Modules.Policy.Contracts;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Billing.Commands;

/// <summary><c>bil.DownPayment.initiate</c>: opens (or refreshes) the down payment of a quoted job, the first instalment of its plan (BO-BIL-04).</summary>
internal sealed record InitiateDownPayment(DownPaymentInitiateRequest Request) : ICommand<DownPaymentInitiateResponse>;

internal sealed class InitiateDownPaymentValidator : AbstractValidator<InitiateDownPayment>
{
    public InitiateDownPaymentValidator()
    {
        RuleFor(c => c.Request.Method).NotEmpty().MaximumLength(40);
        RuleFor(c => c.Request.PaymentPlanOption).MaximumLength(40);
        RuleFor(c => c.Request).Must(r => (r.JobId is null) != (r.QuoteId is null)).WithName("jobId").WithMessage("Give exactly one of jobId and quoteId.");
    }
}

internal static class DownPaymentRules
{
    /// <summary>Methods a down payment may be made by; a direct debit needs a mandate, which a job has not got.</summary>
    public static readonly string[] Methods = [ReceiptCodes.BankTransfer, ReceiptCodes.Cashier];

    /// <summary>Status of a row: CLEARED once the cash covers the amount, PENDING until then.</summary>
    public static DownPaymentStatusResponse.StatusValue Status(DownPaymentRow row) =>
        row.ClearedAmount >= row.Amount ? DownPaymentStatusResponse.StatusValue.Cleared : DownPaymentStatusResponse.StatusValue.Pending;

    public static DownPaymentStatusResponse View(DownPaymentRow row) =>
        new() { Status = Status(row), Amount = new Money(row.Amount, Currency.FromCode(row.Currency)), PlanCode = row.PlanCode };
}

internal sealed class InitiateDownPaymentHandler(
    BillingDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IOptions<BillingOptions> options, IServiceProvider services)
    : ICommandHandler<InitiateDownPayment, DownPaymentInitiateResponse>
{
    public async Task<Result<DownPaymentInitiateResponse>> HandleAsync(InitiateDownPayment command, CancellationToken cancellationToken)
    {
        var r = command.Request;
        if (!DownPaymentRules.Methods.Contains(r.Method, StringComparer.Ordinal))
        {
            return DomainError.Of(ModuleCode.BIL, "METHOD-UNAVAILABLE", $"The method '{r.Method}' is not available for a down payment.");
        }

        if (r.JobId is not { } jobId)
        {
            // TODO(hardening): resolve a quote id to its job (POL serves quotes through the job only).
            return DomainError.Of(ModuleCode.BIL, "VALIDATION", "A down payment is initiated by jobId.");
        }

        var plan = string.IsNullOrWhiteSpace(r.PaymentPlanOption) ? PaymentPlans.Annual : r.PaymentPlanOption;
        if (PaymentPlans.InstalmentCount(plan) == 0)
        {
            return DomainError.Of(ModuleCode.BIL, "NOT-ELIGIBLE", $"The plan '{plan}' is not offered.");
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
        var jobs = services.GetRequiredService<IPolicyJobService>();
        var job = (await jobs.GetAsync(jobId.Value.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false)).Job;
        var version = job.Versions.FirstOrDefault(v => v.VersionNo == job.CurrentVersionNo);
        var charges = version?.Charges.Where(c => c.Amount.Amount > 0m).Select(c => (c.ChargeCategory, c.Amount.Amount)).ToList() ?? [];
        var currency = job.Currency;
        if (charges.Count == 0)
        {
            return DomainError.Of(ModuleCode.BIL, "NOT-ELIGIBLE", "The job has no quoted charges to spread; quote it first.");
        }

        var zone = options.Value.Zone;
        var lines = PlanSpread.Compute(plan, job.EffectiveAt.ToBusinessDate(zone), clock.Now.ToBusinessDate(zone), null, charges);
        var row = await db.DownPayments.SingleOrDefaultAsync(d => d.LegalEntityId == legalEntity && d.JobId == jobId.Value, cancellationToken).ConfigureAwait(false);
        if (lines.Count < 2)
        {
            // A single-instalment plan has no down payment; an earlier unpaid row for another plan no longer applies.
            if (row is not null && row.ClearedAmount == 0m)
            {
                db.DownPayments.Remove(row);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return new DownPaymentInitiateResponse { Status = DownPaymentInitiateResponse.StatusValue.NotRequired, Amount = new Money(0m, currency) };
        }

        var amount = lines[0].Amount;
        if (row is null)
        {
            var id = Guid.CreateVersion7();
            row = new DownPaymentRow
            {
                DownPaymentId = id, LegalEntityId = legalEntity, JobId = jobId.Value, PaymentReference = ReceivableReference.Create(id),
                CreatedAt = clock.Now, CreatedBy = context.Actor.ToString(), RecordVersion = 1,
            };
            db.DownPayments.Add(row);
        }
        else
        {
            row.RecordVersion++;
        }

        row.PlanCode = plan;
        row.Method = r.Method;
        row.Amount = amount;
        row.Currency = currency.Code;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new DownPaymentInitiateResponse
        {
            Status = DownPaymentRules.Status(row) == DownPaymentStatusResponse.StatusValue.Cleared
                ? DownPaymentInitiateResponse.StatusValue.Cleared
                : DownPaymentInitiateResponse.StatusValue.Pending,
            Amount = new Money(amount, currency),
            PaymentReference = row.PaymentReference,

            // TODO(hardening): an EPC/SEPA QR payload and a hosted payment link once a payment provider is bound.
        };
    }
}

internal sealed class InitiateDownPaymentAuditor : ICommandAuditor<InitiateDownPayment, DownPaymentInitiateResponse>
{
    public CommandAuditFacts Describe(InitiateDownPayment command, Result<DownPaymentInitiateResponse>? result)
    {
        var key = command.Request.JobId?.Value.ToString("D") ?? string.Empty;
        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.BIL, "DownPayment", key),
            BusinessKeys = BusinessKeys.Empty.With("jobId", key),
            Changes = result is { IsSuccess: true }
                ? AuditDiff.Compute(null, new { plan = command.Request.PaymentPlanOption, method = command.Request.Method, amount = result.Value.Value.Amount.Amount })
                : [],
        };
    }
}

/// <summary>Status read and the cash record of a down payment (BO-BIL-04).</summary>
internal sealed class DownPaymentService(BillingDbContext db, IClock clock, RequestContext context)
{
    public async Task<DownPaymentStatusResponse> StatusAsync(LegalEntityId legalEntity, Guid jobId, CancellationToken cancellationToken)
    {
        var row = await db.DownPayments.AsNoTracking().SingleOrDefaultAsync(d => d.LegalEntityId == legalEntity && d.JobId == jobId, cancellationToken).ConfigureAwait(false);
        return row is null ? new DownPaymentStatusResponse { Status = DownPaymentStatusResponse.StatusValue.NotRequired } : DownPaymentRules.View(row);
    }

    /// <summary>
    /// Records cash against a payment reference (the dev bank; a bank-statement match later). Clears the down payment once the total covers it.
    /// TODO(hardening): take the cash as a receipt on a billing account (the payer's, opened at bind) and publish DownPaymentCleared.
    /// </summary>
    public async Task<Result<DownPaymentStatusResponse>> RecordCashAsync(string reference, decimal? amount, CancellationToken cancellationToken)
    {
        var row = await db.DownPayments.SingleOrDefaultAsync(d => d.PaymentReference == reference, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return BillingErrors.NotFound("down payment");
        }

        var paid = amount ?? row.Amount;
        if (paid <= 0m)
        {
            return DomainError.Of(ModuleCode.BIL, "VALIDATION", "The amount must be positive.");
        }

        row.ClearedAmount += paid;
        row.RecordVersion++;
        if (row.ClearedAmount >= row.Amount)
        {
            row.ClearedAt ??= clock.Now;
            row.ClearedBy = context.Actor.ToString();
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return DownPaymentRules.View(row);
    }
}
