using System.Globalization;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Platform;
using CoreIns.Platform.Authority;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Party.Domain;

/// <summary>Commission agreement parameters of the Party module (BO-PTY-10). Pack data in the real stamp.</summary>
internal sealed class CommissionOptions
{
    public const string Section = "Party:Commission";

    /// <summary>The highest rate (percent) a rate line may carry; above it the version is refused (PTY-ERR-RATE-ABOVE-MAX). TODO(hardening): per product and country pack.</summary>
    public decimal MaxRatePct { get; set; } = 40m;

    /// <summary>Rates up to this percent are a STANDARD authority band; higher rates are ELEVATED and need the higher authority.</summary>
    public decimal StandardRatePct { get; set; } = 20m;

    /// <summary>
    /// The last day whose commission is settled; a version cannot take effect on or before it (no backdating). Empty = the last day of
    /// the previous month. TODO(hardening): read the settled period from BIL once commission statements exist.
    /// </summary>
    public DateOnly? SettledThrough { get; set; }
}

/// <summary>The PLT authority type of commission agreement approval (illustrative grants in <c>permissions/pty.json</c>).</summary>
public static class PartyAuthorityTypes
{
    /// <summary>Approve or reject a commission agreement version: limited by expected annual volume and by the rate band.</summary>
    public static AuthorityTypeCode CommissionAgreement { get; } = AuthorityTypeCode.Parse("PTY.COMMISSION_AGREEMENT");

    /// <summary>The PLT approval type of a commission agreement version (owner-decided: PTY decides it, not the generic inbox).</summary>
    public const string CommissionApprovalType = "PTY.COMMISSION_AGREEMENT";

    /// <summary>Approve or reject a party merge (BO-PTY-05): no dimension, the holder of the grant may decide any merge.</summary>
    public static AuthorityTypeCode PartyMerge { get; } = AuthorityTypeCode.Parse("PTY.PARTY_MERGE");

    /// <summary>The PLT approval type of a party merge (owner-decided: PTY decides it, not the generic inbox).</summary>
    public const string PartyMergeApprovalType = "PTY.PARTY_MERGE";

    /// <summary>Approve a screening case decision, Clear or TrueMatch (BO-PTY-07): no dimension, the holder of the grant may decide any case.</summary>
    public static AuthorityTypeCode ScreeningDecision { get; } = AuthorityTypeCode.Parse("PTY.SCREENING_DECISION");

    /// <summary>The PLT approval type of a screening case decision (owner-decided: PTY decides it, not the generic inbox).</summary>
    public const string ScreeningDecisionApprovalType = "PTY.SCREENING_DECISION";

    /// <summary>The amount dimension: expected annual premium volume in EUR.</summary>
    public const string AmountDimension = "amount";

    /// <summary>The code dimension: STANDARD or ELEVATED, from the highest rate.</summary>
    public const string RateBandDimension = "rateBand";

    /// <summary>The definitions.</summary>
    public static IReadOnlyList<AuthorityTypeDefinition> Definitions { get; } =
    [
        new AuthorityTypeDefinition(
            CommissionAgreement, ModuleCode.PTY, new LocalizedText("Έγκριση συμφωνίας προμήθειας", "Approve commission agreement"),
            [
                new AuthorityDimensionDefinition(AmountDimension, DimensionKind.Money),
                new AuthorityDimensionDefinition(RateBandDimension, DimensionKind.Code),
            ]),
        new AuthorityTypeDefinition(PartyMerge, ModuleCode.PTY, new LocalizedText("Έγκριση συγχώνευσης προσώπων", "Approve party merge"), []),
        new AuthorityTypeDefinition(ScreeningDecision, ModuleCode.PTY, new LocalizedText("Έγκριση απόφασης ελέγχου κυρώσεων", "Approve screening case decision"), []),
    ];

    /// <summary>Registers the Party authority types.</summary>
    public static IServiceCollection AddPartyAuthorityTypes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var definition in Definitions)
        {
            services.AddAuthorityType(definition);
        }

        return services;
    }
}

/// <summary>Version status codes stored in <c>pty.commission_agreement_version</c>.</summary>
internal static class CommissionStatus
{
    public const string Draft = "DRAFT";
    public const string PendingApproval = "PENDING_APPROVAL";
    public const string Active = "ACTIVE";
    public const string Rejected = "REJECTED";
}

/// <summary>Pure rules of commission agreements: validation of rate lines, the content hash, the scope key, the authority band.</summary>
internal static class CommissionRules
{
    /// <summary>Decimal places of a rate (percent). The column is numeric(7,4).</summary>
    public const int RateScale = 4;

    public static string ScopeKey(string? productCode, string? lineOfBusiness) => (productCode ?? "*") + "|" + (lineOfBusiness ?? "*");

    /// <summary>The stable text of a rate: four decimals, invariant culture (no rounding: rates with more places are refused).</summary>
    public static string RateText(decimal rate) => rate.ToString("F4", CultureInfo.InvariantCulture);

    /// <summary>True when the rate has at most <see cref="RateScale"/> decimals (checked by scaling, never by rounding).</summary>
    public static bool HasValidScale(decimal rate) => rate * 10_000m == decimal.Truncate(rate * 10_000m);

    public static string Band(decimal maxRatePct, decimal standardRatePct) => maxRatePct <= standardRatePct ? "STANDARD" : "ELEVATED";

    /// <summary>Last day of the previous month, or the configured settled date.</summary>
    public static BusinessDate SettledThrough(CommissionOptions options, BusinessDate today) =>
        options.SettledThrough is { } configured
            ? new BusinessDate(configured)
            : new BusinessDate(new DateOnly(today.Year, today.Month, 1).AddDays(-1));

    /// <summary>The first error of the rate lines, or null: range, scale, duplicates.</summary>
    public static DomainError? CheckLines(IReadOnlyList<CommissionRateLine> lines, CommissionOptions options)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.RatePct < 0m || !HasValidScale(line.RatePct))
            {
                return DomainError.Of(ModuleCode.PTY, "VALIDATION", "A rate must be zero or more, with at most 4 decimals.");
            }

            if (line.RatePct > options.MaxRatePct)
            {
                return DomainError.Of(ModuleCode.PTY, "RATE-ABOVE-MAX", $"A rate of {RateText(line.RatePct)}% is above the maximum of {RateText(options.MaxRatePct)}%.");
            }

            if (!seen.Add(LineKey(Codes.Of(line.BusinessType), line.LineOfBusiness, Codes.Of(line.Basis))))
            {
                return DomainError.Of(ModuleCode.PTY, "VALIDATION", "Two rate lines have the same business type, line of business and basis.");
            }
        }

        return null;
    }

    public static string LineKey(string businessType, string? lineOfBusiness, string basis) =>
        string.Join('|', businessType.ToUpperInvariant(), lineOfBusiness ?? "*", basis.ToUpperInvariant());

    /// <summary>
    /// The hash a decision must carry: everything the checker approves (version, scope, date, volume and the sorted rates), so a change
    /// after submission is detected (PITFALLS 3, 4).
    /// </summary>
    public static Sha256Hash Hash(
        Guid versionId, Guid intermediaryId, string scopeKey, BusinessDate effectiveFrom, decimal volume, string currency,
        IEnumerable<(string BusinessType, string? LineOfBusiness, string Basis, decimal Rate)> lines)
    {
        var rates = lines
            .Select(l => LineKey(l.BusinessType, l.LineOfBusiness, l.Basis) + "=" + RateText(l.Rate))
            .Order(StringComparer.Ordinal);
        var text = string.Join(
            '\n',
            ["PTY.COMMISSION_AGREEMENT", versionId.ToString("D"), intermediaryId.ToString("D"), scopeKey, effectiveFrom.ToString(), volume.ToString("F2", CultureInfo.InvariantCulture) + currency, .. rates]);
        return Sha256Hash.ComputeUtf8(text);
    }
}
