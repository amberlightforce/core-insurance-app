using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.Rules;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Billing.Queries;

/// <summary>One instalment of a spread: number, due date, gross and the amount per charge category (premium, tax, levy, fee).</summary>
internal sealed record SpreadLine(int No, BusinessDate Due, decimal Amount, IReadOnlyList<(string Category, decimal Amount)> ByCategory);

/// <summary>
/// The NX-EC-11 splitter applied to a hypothetical or selected plan, without persisting anything (BO-BIL-03). Uses the same split and the
/// same schedule rules as <c>TermBilling</c>, so a preview of a term's own plan equals what billing writes.
/// </summary>
internal static class PlanSpread
{
    public static List<SpreadLine> Compute(string planCode, BusinessDate termFrom, BusinessDate today, int? dueDay, IReadOnlyList<(string Category, decimal Amount)> charges)
    {
        var schedule = PaymentPlans.BoundSchedule(planCode, termFrom, today, dueDay);
        var split = InstalmentSplit.Split(
            [.. charges.Select(c => c.Amount)], [.. charges.Select(c => c.Category is not ("TAX" or "LEVY" or "FEE"))], schedule.Count);
        return [.. schedule.Select((s, k) =>
        {
            var parts = charges.Select((c, ci) => (c.Category, Amount: split[ci][k]))
                .GroupBy(p => p.Category, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (g.Key, g.Sum(p => p.Amount))).ToList();
            return new SpreadLine(s.No, s.Due, parts.Sum(p => p.Item2), parts);
        })];
    }
}

/// <summary><c>bil.PaymentPlan.list</c> and <c>bil.BillingPreview.compute</c> (BO-BIL-03).</summary>
internal sealed class PaymentPlanReader(BillingDbContext db, IClock clock, IOptions<BillingOptions> options)
{
    public const string PlanVersion = "1";

    /// <summary>
    /// The seed catalogue: every plan the slice bills, with its instalments and a zero plan fee.
    /// TODO(hardening): read the eligible plans and their fees from the MKT/PFC rule of the product version, channel and producer.
    /// </summary>
    public static PaymentPlanListResponse List(string currencyCode) => new()
    {
        Plans = [.. PaymentPlans.Catalogue.Select(code => new PaymentPlanListResponse.PlanItem
        {
            PlanCode = code,
            PlanVersion = PlanVersion,
            Instalments = PaymentPlans.InstalmentCount(code),
            IntervalMonths = PaymentPlans.IntervalMonths(code),
            Fee = new Money(0m, Currency.FromCode(currencyCode)),
        })],
        Exclusions = [],
    };

    /// <summary>The positive, unquarantined charges of a term's bound transaction (category, amount), in set order: what billing spreads.</summary>
    public async Task<List<(string Category, decimal Amount)>> TermChargesAsync(PlanInstanceRow plan, CancellationToken cancellationToken)
    {
        var quarantined = Codes.Of(ChargeStatus.Quarantined);
        var rows = await db.Charges.AsNoTracking()
            .Where(c => c.TermId == plan.TermId && c.TransactionId == plan.BoundTransactionId && c.Amount > 0m && c.Status != quarantined)
            .OrderBy(c => c.SetIndex).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(c => (c.ChargeCategory, c.Amount))];
    }

    public async Task<Result<BillingPreviewComputeResponse>> PreviewAsync(LegalEntityId legalEntity, BillingPreviewComputeRequest request, CancellationToken cancellationToken)
    {
        if (PaymentPlans.InstalmentCount(request.PlanCode) == 0)
        {
            return DomainError.Of(ModuleCode.BIL, "NOT-ELIGIBLE", $"The plan '{request.PlanCode}' is not offered.");
        }

        BusinessDate termFrom;
        Currency currency;
        List<(string Category, decimal Amount)> charges;
        if (request.TermId is { } termId)
        {
            var plan = await db.PlanInstances.AsNoTracking().SingleOrDefaultAsync(p => p.TermId == termId && p.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
            if (plan is null)
            {
                return BillingErrors.NotFound("term");
            }

            var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.BillingAccountId == plan.BillingAccountId, cancellationToken).ConfigureAwait(false);
            var rows = await TermChargesAsync(plan, cancellationToken).ConfigureAwait(false);
            termFrom = plan.TermFrom;
            currency = Currency.FromCode(account.Currency);
            charges = rows;
            if (charges.Count == 0)
            {
                return DomainError.Of(ModuleCode.BIL, "NOT-ELIGIBLE", "The term has no charges to spread yet.");
            }
        }
        else
        {
            if (request.TermFrom is not { } from || request.Charges is not { Count: > 0 } given)
            {
                return DomainError.Of(ModuleCode.BIL, "VALIDATION", "Give a termId, or termFrom and charges for a quote.");
            }

            currency = given[0].Amount.Currency;
            if (given.Any(c => c.Amount.Currency != currency || c.Amount.Amount <= 0m || !c.Amount.IsRoundedToMinorUnits))
            {
                return DomainError.Of(ModuleCode.BIL, "VALIDATION", "Charges are positive amounts in one currency, rounded to minor units.");
            }

            termFrom = from;
            charges = [.. given.Select(c => (c.Category, c.Amount.Amount))];
        }

        var lines = PlanSpread.Compute(request.PlanCode, termFrom, clock.Now.ToBusinessDate(options.Value.Zone), request.DueDay, charges);
        Money M(decimal amount) => new(amount, currency);
        return new BillingPreviewComputeResponse
        {
            PlanCode = request.PlanCode,
            FullSchedule = [.. lines.Select(l => new BillingPreviewComputeResponse.FullScheduleItem
            {
                InstalmentNo = l.No,
                DueDate = l.Due,
                Amount = M(l.Amount),
                ByCategory = [.. l.ByCategory.Select(p => new BillingPreviewComputeResponse.FullScheduleItem.ByCategoryItem { Category = p.Category, Amount = M(p.Amount) })],
            })],
            FirstDue = new BillingPreviewComputeResponse.FirstDueDetail { DueDate = lines[0].Due, Amount = M(lines[0].Amount) },
            Total = M(lines.Sum(l => l.Amount)),
        };
    }
}
