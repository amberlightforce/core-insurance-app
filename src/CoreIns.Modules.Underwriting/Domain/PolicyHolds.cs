using System.Globalization;

namespace CoreIns.Modules.Underwriting.Domain;

/// <summary>Scope dimensions of a policy hold (BO-UW-09), as stored.</summary>
internal static class HoldDimension
{
    public const string Postcode = "POSTCODE";
    public const string HazardZone = "HAZARD_ZONE";
    public const string Product = "PRODUCT";
    public const string LineOfBusiness = "LINE_OF_BUSINESS";
    public const string Coverage = "COVERAGE";
}

/// <summary>Hold statuses as stored.</summary>
internal static class HoldStatus
{
    public const string Draft = "Draft";
    public const string Active = "Active";
    public const string Released = "Released";
}

/// <summary>The issue type a hold raises on a job, and the rule id prefix that tells hold issues from rule-set issues.</summary>
internal static class HoldIssue
{
    public const string IssueType = "UW_POLICY_HOLD";
    public const string RulePrefix = "HOLD:";

    public static string Key(Guid holdId) => $"{IssueType}:{holdId:N}";

    public static string RuleId(string holdCode) => RulePrefix + holdCode;
}

/// <summary>A hold as the matcher sees it.</summary>
internal sealed record HoldDefinition(
    Guid HoldId, string Code, string Description, DateOnly EffectiveFrom, DateOnly? EffectiveTo, IReadOnlyList<string> JobTypes, IReadOnlyList<(string Dimension, string Value)> Scopes);

/// <summary>The facts of a job or quote the holds are matched against.</summary>
internal sealed record HoldFacts(
    string? JobType, string? Product, string? LineOfBusiness, IReadOnlyList<string> Coverages, IReadOnlyList<string> Postcodes, IReadOnlyList<string> HazardZones, DateOnly EffectiveDate);

/// <summary>
/// Hold matching (REQ-UW-007), pure: the effective date lies in the hold's window, the job type is one the hold blocks (all when none are
/// listed) and, for every dimension the hold names, at least one of its values matches the facts. A dimension the hold does not name is open.
/// A fact that is unknown (no postcode on the risk) never matches a hold that names that dimension: the hold is conservative only about what it can see.
/// </summary>
internal static class HoldMatcher
{
    public static bool Applies(HoldDefinition hold, HoldFacts facts)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.EffectiveDate < hold.EffectiveFrom || (hold.EffectiveTo is { } to && facts.EffectiveDate > to))
        {
            return false;
        }

        if (hold.JobTypes.Count > 0 && (facts.JobType is null || !hold.JobTypes.Contains(facts.JobType, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        foreach (var group in hold.Scopes.GroupBy(s => s.Dimension, StringComparer.Ordinal))
        {
            var values = group.Select(s => s.Value).ToList();
            var matched = group.Key switch
            {
                HoldDimension.Postcode => facts.Postcodes.Any(p => values.Any(v => PostcodeMatches(v, p))),
                HoldDimension.HazardZone => facts.HazardZones.Any(z => values.Contains(z, StringComparer.OrdinalIgnoreCase)),
                HoldDimension.Product => facts.Product is { } product && values.Contains(product, StringComparer.OrdinalIgnoreCase),
                HoldDimension.LineOfBusiness => facts.LineOfBusiness is { } line && values.Contains(line, StringComparer.OrdinalIgnoreCase),
                HoldDimension.Coverage => facts.Coverages.Any(c => values.Contains(c, StringComparer.OrdinalIgnoreCase)),
                _ => false,
            };
            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Postcodes compare without spaces and case; a pattern ending in an asterisk is a prefix.</summary>
    public static bool PostcodeMatches(string pattern, string postcode)
    {
        var p = Normalise(pattern);
        var value = Normalise(postcode);
        return p.EndsWith('*') ? value.StartsWith(p[..^1], StringComparison.Ordinal) : string.Equals(p, value, StringComparison.Ordinal);
    }

    public static string Normalise(string postcode) => postcode.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpper(CultureInfo.InvariantCulture);
}
