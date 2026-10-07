using System.Text.Json;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Policy.Domain;

/// <summary>An unrounded charge line of a term: an annual rate from RAT and its term amount before rounding.</summary>
internal sealed record ChargeDraft(string ElementLocator, string CoverageCode, string ChargeType, string ChargeCategory, decimal AnnualRate, Money Unrounded);

/// <summary>
/// Turns a RAT rating result into the term's charge lines (REQ-POL-115, REQ-POL-124): rates, not amounts, cross the RAT
/// boundary; premium and the tax and levy lines RAT returns after premium are separate charge types. POL never computes
/// tax itself. SL-POL binds full annual terms only, so the proration factor is exactly one and the term amount equals the
/// annual rate; rounding is applied afterwards through <c>mkt.Rounding.apply</c> (REQ-POL-123). Pure: no I/O.
/// </summary>
internal static class Charges
{
    /// <summary>The charge lines of a rating response, or POL-ERR-RATING when a line is unusable.</summary>
    public static Result<IReadOnlyList<ChargeDraft>> FromRating(RateRateResponse rating, Currency currency)
    {
        var lines = new List<ChargeDraft>();
        foreach (var rate in rating.Rates)
        {
            var line = Line(rate, ChargeCategories.Premium, currency);
            if (line.IsFailure)
            {
                return line.Error!;
            }

            lines.Add(line.Value);
        }

        foreach (var (tax, index) in (rating.Taxes ?? []).Select((t, i) => (t, i)))
        {
            RateRateResponse.RateItem? item;
            try
            {
                item = tax.Deserialize<RateRateResponse.RateItem>(SharedKernelJson.Options);
            }
            catch (JsonException ex)
            {
                return Rating($"Tax line {index} from RAT does not have the rate-item shape: {ex.Message}");
            }

            if (item is null || item.ChargeCategory is null)
            {
                return Rating($"Tax line {index} from RAT has no charge category.");
            }

            var line = Line(item, item.ChargeCategory, currency);
            if (line.IsFailure)
            {
                return line.Error!;
            }

            lines.Add(line.Value);
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

    private static Result<ChargeDraft> Line(RateRateResponse.RateItem rate, string defaultCategory, Currency currency)
    {
        if (rate.Currency != currency)
        {
            return Rating($"RAT rated {rate.ChargeType} in {rate.Currency}, the term currency is {currency}.");
        }

        if (string.IsNullOrWhiteSpace(rate.CoverageCode))
        {
            return Rating($"RAT returned {rate.ChargeType} on {rate.ElementId} without a coverage code.");
        }

        // Full annual term: proration factor 1 (the annual rate is the term amount before rounding).
        return new ChargeDraft(rate.ElementId, rate.CoverageCode, rate.ChargeType, rate.ChargeCategory ?? defaultCategory, rate.AnnualRate, new Money(rate.AnnualRate, currency));
    }

    private static DomainError Rating(string detail) => DomainError.Of(ModuleCode.POL, "RATING", detail);
}
