using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Contracts.Common;

namespace CoreIns.Modules.Policy.Domain;

/// <summary>
/// Reads a UW evaluation (uw.Rules.evaluate) into POL's quote outcome. UW never decides POL state (PRD-04 §15): POL
/// derives the Referred flag from open blocking issues, and a decline reaches POL only as <c>uw.DeclineIssued</c>
/// (REQ-POL-156). An issue blocks its point while Open, Rejected or Invalidated (BR-UW-001).
/// </summary>
internal static class UwOutcome
{
    private static readonly HashSet<string> BlockingStatuses = new(StringComparer.Ordinal) { "OPEN", "REJECTED", "INVALIDATED" };

    /// <summary>True when the issue blocks <paramref name="point"/> or an earlier point.</summary>
    public static bool Blocks(UwIssue issue, BlockingPoint point) =>
        issue.BlockingPoint != BlockingPoint.NonBlocking
        && Order(issue.BlockingPoint) <= Order(point)
        && BlockingStatuses.Contains(Normalise(issue.ApprovalStatus));

    /// <summary>True when any issue blocks a later point than PRE_QUOTE (the quote stands, bind waits for approval).</summary>
    public static bool BlocksLater(IEnumerable<UwIssue> issues) =>
        issues.Any(i => i.BlockingPoint is BlockingPoint.PreBind or BlockingPoint.PreIssue && BlockingStatuses.Contains(Normalise(i.ApprovalStatus)));

    private static int Order(BlockingPoint point) => point switch
    {
        BlockingPoint.PreQuote => 0,
        BlockingPoint.PreBind => 1,
        BlockingPoint.PreIssue => 2,
        _ => int.MaxValue,
    };

    // Status codes travel as upper-snake codes; accept the PRD's PascalCase spelling too.
    private static string Normalise(string status) => string.Concat(status.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(status[i - 1]) ? "_" + c : c.ToString())).ToUpperInvariant();
}
