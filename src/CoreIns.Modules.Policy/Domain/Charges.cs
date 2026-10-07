using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Policy.Domain;

/// <summary>
/// A charge line of a term from RAT: the annual rate and, for tax and levy lines, the amount RAT already computed and
/// rounded on the rounded premium (null for premium lines, which POL rounds through MKT).
/// </summary>
internal sealed record ChargeDraft(string ElementLocator, string CoverageCode, string ChargeType, string ChargeCategory, decimal AnnualRate, Money? Amount)
{
    /// <summary>Legal status of the configured tax or levy value (tax and levy lines).</summary>
    public string? LegalStatus { get; init; }

    /// <summary>The tax or levy value is not Settled.</summary>
    public bool? Provisional { get; init; }
}

/// <summary>
/// Turns a RAT rating result into the term's charge lines (REQ-POL-115, REQ-POL-124): rates, not amounts, cross the RAT
/// boundary for premium; taxes and levies are separate charge types computed by RAT after premium (POL never computes
/// tax). SL-POL binds full annual terms only, so the proration factor is exactly one: the term premium is the annual
/// rate before rounding. Pure: no I/O.
/// </summary>
internal static class Charges
{
    /// <summary>The charge lines of a rating response, or POL-ERR-RATING when a line is unusable.</summary>
    public static Result<IReadOnlyList<ChargeDraft>> FromRating(RateRateResponse rating, Currency currency, string vehicleLocator)
    {
        var lines = new List<ChargeDraft>();
        foreach (var rate in rating.Rates)
        {
            if (rate.Currency != currency)
            {
                return Rating($"RAT rated {rate.ChargeType} in {rate.Currency}, the term currency is {currency}.");
            }

            if (string.IsNullOrWhiteSpace(rate.CoverageCode))
            {
                return Rating($"RAT returned {rate.ChargeType} without a coverage code.");
            }

            lines.Add(new ChargeDraft(rate.ElementId, rate.CoverageCode, rate.ChargeType, rate.ChargeCategory ?? ChargeCategories.Premium, rate.AnnualRate, null));
        }

        foreach (var tax in rating.Taxes ?? [])
        {
            if (tax.Amount.Currency != currency)
            {
                return Rating($"RAT computed {tax.ChargeType} in {tax.Amount.Currency}, the term currency is {currency}.");
            }

            if (!tax.Amount.IsRoundedToMinorUnits)
            {
                return Rating($"RAT returned {tax.ChargeType} unrounded ({tax.Amount}).");
            }

            lines.Add(new ChargeDraft(
                vehicleLocator, tax.CoverageCode, tax.ChargeType,
                string.IsNullOrEmpty(tax.ChargeCategory)
                    ? tax.Category == RateRateResponse.TaxeItem.CategoryValue.Levy ? ChargeCategories.Levy : ChargeCategories.Tax
                    : tax.ChargeCategory,
                tax.Rate, tax.Amount)
            {
                LegalStatus = tax.LegalStatus,
                Provisional = tax.Provisional,
            });
        }

        if (lines.Count == 0)
        {
            return Rating("RAT returned no charge lines.");
        }

        return lines;
    }

    /// <summary>Premium, taxes (every non-premium category) and total of rounded lines.</summary>
    public static (Money Premium, Money Taxes, Money Total) Totals(IEnumerable<ChargeLine> lines, Currency currency)
    {
        var all = lines.ToList();
        var premium = Money.Sum(all.Where(l => l.ChargeCategory == ChargeCategories.Premium).Select(l => l.Amount), currency);
        var taxes = Money.Sum(all.Where(l => l.ChargeCategory != ChargeCategories.Premium).Select(l => l.Amount), currency);
        return (premium, taxes, premium + taxes);
    }

    private static DomainError Rating(string detail) => DomainError.Of(ModuleCode.POL, "RATING", detail);
}
