using CoreIns.SharedKernel;

namespace CoreIns.Modules.Party.Domain;

/// <summary>An ACTIVE agreement version with its rate lines, the input of <see cref="CommissionResolver"/>.</summary>
internal sealed record ResolverVersion(
    Guid AgreementId, Guid VersionId, int VersionNo, string? ProductCode, string? LineOfBusiness, string ScopeKey, BusinessDate EffectiveFrom,
    IReadOnlyList<ResolverLine> Lines);

/// <summary>A rate line of a version (<c>BusinessType</c> NEW_BUSINESS, RENEWAL or ANY; <c>Basis</c> NET_PREMIUM or GROSS_PREMIUM).</summary>
internal sealed record ResolverLine(int LineNo, string BusinessType, string? LineOfBusiness, string Basis, decimal RatePct);

/// <summary>The rate that won for one intermediary, with why.</summary>
internal sealed record ResolvedRate(ResolverVersion Version, ResolverLine Line, IReadOnlyList<string> Explanation);

/// <summary>No commission for one intermediary: <c>Reason</c> is NO_AGREEMENT or NO_RATE_LINE.</summary>
internal sealed record ResolutionMiss(string Reason, IReadOnlyList<string> Explanation);

/// <summary>
/// Pure, deterministic choice of the commission rate line (BO-PTY-11, REQ-PTY-010). Among the ACTIVE versions of one intermediary that are in
/// force on the date, scopes are tried most specific first (product and line, then product, then line, then all); within a scope the rate
/// line is chosen by business type (exact before ANY), then line of business (exact before all), then line number. The same inputs always
/// give the same answer; nothing is read or written here.
/// </summary>
internal static class CommissionResolver
{
    public static (ResolvedRate? Rate, ResolutionMiss? Miss) Resolve(
        IReadOnlyList<ResolverVersion> activeVersions, string? productCode, string? lineOfBusiness, BusinessDate date, string businessType, string basis)
    {
        var explanation = new List<string>();

        // The version in force of each agreement: the latest ACTIVE one that started on or before the date (a version ends the day before the next starts).
        var inForce = activeVersions
            .GroupBy(v => v.AgreementId)
            .Select(g => g.Where(v => v.EffectiveFrom <= date).OrderByDescending(v => v.EffectiveFrom).ThenByDescending(v => v.VersionNo).FirstOrDefault())
            .Where(v => v is not null)
            .Select(v => v!)
            .Where(v => (v.ProductCode is null || v.ProductCode == productCode) && (v.LineOfBusiness is null || v.LineOfBusiness == lineOfBusiness))
            .OrderByDescending(v => (v.ProductCode is null ? 0 : 2) + (v.LineOfBusiness is null ? 0 : 1))
            .ThenBy(v => v.ScopeKey, StringComparer.Ordinal)
            .ThenBy(v => v.AgreementId)
            .ToList();

        if (inForce.Count == 0)
        {
            explanation.Add($"No ACTIVE agreement version of the intermediary is in force on {date} for product {productCode ?? "*"} and line {lineOfBusiness ?? "*"}.");
            return (null, new ResolutionMiss("NO_AGREEMENT", explanation));
        }

        foreach (var version in inForce)
        {
            var winner = version.Lines
                .Where(l => l.Basis == basis && (l.BusinessType == businessType || l.BusinessType == "ANY") && (l.LineOfBusiness is null || l.LineOfBusiness == lineOfBusiness))
                .OrderByDescending(l => l.BusinessType == businessType)
                .ThenByDescending(l => l.LineOfBusiness is not null)
                .ThenBy(l => l.LineNo)
                .FirstOrDefault();
            if (winner is null)
            {
                explanation.Add($"Scope {version.ScopeKey} (version {version.VersionNo}) has no rate line for {businessType} on {basis}; the next scope is tried.");
                continue;
            }

            explanation.Add($"Scope {version.ScopeKey} is the most specific agreement in force on {date} with a matching rate line (version {version.VersionNo}, effective {version.EffectiveFrom}).");
            explanation.Add(winner.BusinessType == businessType
                ? $"Rate line {winner.LineNo} matches the business type {businessType} exactly."
                : $"Rate line {winner.LineNo} applies to any business type (no line for {businessType}).");
            explanation.Add(winner.LineOfBusiness is not null
                ? $"Rate line {winner.LineNo} is limited to line {winner.LineOfBusiness}."
                : $"Rate line {winner.LineNo} applies to every line of business.");
            return (new ResolvedRate(version, winner, explanation), null);
        }

        explanation.Add($"Agreements are in force on {date} but none has a rate line for {businessType} on {basis}.");
        return (null, new ResolutionMiss("NO_RATE_LINE", explanation));
    }
}
