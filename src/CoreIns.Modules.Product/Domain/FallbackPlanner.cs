using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Product.Domain;

/// <summary>What the planner needs to know of one version of a product.</summary>
/// <param name="Id">The version row id.</param>
/// <param name="Number">The version number.</param>
/// <param name="Status">The lifecycle status code.</param>
/// <param name="IsAbstract">Abstract bases are never sources or defects.</param>
/// <param name="Channels">The channel scope.</param>
/// <param name="NewBusiness">The new-business window.</param>
/// <param name="Renewal">The renewal window.</param>
/// <param name="ArtefactHash">The artefact hash.</param>
/// <param name="Replaced">True when an applied fall-back already replaced this version (it is defective and never a source).</param>
internal sealed record VersionFacts(
    ProductVersionId Id,
    ProductVersionNumber Number,
    string Status,
    bool IsAbstract,
    IReadOnlyList<string> Channels,
    DateRange NewBusiness,
    DateRange Renewal,
    string ArtefactHash,
    bool Replaced);

/// <summary>The server-derived plan of a fall-back: nothing in it is chosen by the client (PITFALLS 4, 7).</summary>
internal sealed record FallbackPlan(
    VersionFacts Defective,
    VersionFacts Source,
    ProductVersionNumber NewVersion,
    DateRange NewBusinessWindow,
    DateRange NewRenewalWindow,
    DateRange ClosedWindow);

/// <summary>
/// REQ-PFC-213 / D-SL5-09: the source is the highest earlier Locked, non-abstract, not-replaced version with the same channel
/// scope; the new number is the next free minor of the source's major; the new version takes the new-business window
/// [fallbackDate, defective's old end) and renewal window [fallbackDate, open); the defective version's new-business window
/// is shortened to [its start, fallbackDate). Pure; the handler loads the facts and applies the plan.
/// </summary>
internal static class FallbackPlanner
{
    public const string Locked = "LOCKED";

    public static TimeZoneInfo Athens { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");

    public static Result<FallbackPlan> Plan(IReadOnlyList<VersionFacts> versions, ProductVersionNumber defectiveNumber, BusinessDate fallbackDate)
    {
        var defective = versions.FirstOrDefault(v => v.Number == defectiveNumber);
        if (defective is null || defective.Status != Locked)
        {
            return DomainError.Of(ModuleCode.PFC, "NO-VERSION", $"Version {defectiveNumber} does not exist as a Locked version of the product.");
        }

        if (defective.IsAbstract)
        {
            return DomainError.Of(ModuleCode.PFC, "ABSTRACT", $"Version {defectiveNumber} is an abstract base and cannot be fallen back from.");
        }

        if (defective.Replaced)
        {
            return DomainError.Of(ModuleCode.PFC, "FALLBACK-STATE", $"Version {defectiveNumber} was already replaced by a fall-back.");
        }

        // The defective version must be in force for new business before the fall-back date, so closing leaves a non-empty window.
        var window = defective.NewBusiness;
        if (!window.Contains(fallbackDate) || window.Start == fallbackDate)
        {
            return DomainError.Of(ModuleCode.PFC, "FALLBACK-STATE",
                $"Version {defectiveNumber} has no new-business time to close on {fallbackDate} (window {window}).");
        }

        var source = versions
            .Where(v => v.Number < defectiveNumber && v.Status == Locked && !v.IsAbstract && !v.Replaced)
            .OrderByDescending(v => v.Number)
            .FirstOrDefault();
        if (source is null)
        {
            return DomainError.Of(ModuleCode.PFC, "FALLBACK-SOURCE", $"Version {defectiveNumber} has no earlier published version to copy.");
        }

        if (!source.Channels.Order(StringComparer.Ordinal).SequenceEqual(defective.Channels.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return DomainError.Of(ModuleCode.PFC, "FALLBACK-SOURCE", $"Version {source.Number} has another channel scope than {defectiveNumber}.");
        }

        var nextMinor = versions.Where(v => v.Number.Major == source.Number.Major).Max(v => v.Number.Minor) + 1;
        return new FallbackPlan(
            defective,
            source,
            new ProductVersionNumber(source.Number.Major, nextMinor),
            new DateRange(fallbackDate, window.End),
            DateRange.Open(fallbackDate),
            new DateRange(window.Start, fallbackDate));
    }

    /// <summary>The Athens business date of an instant (PITFALLS 14). The zone lookup throws when unknown: it never falls back to UTC.</summary>
    public static BusinessDate AthensDate(Instant instant) => instant.ToBusinessDate(Athens);
}
