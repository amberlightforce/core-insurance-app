namespace CoreIns.Modules.Underwriting.Domain;

/// <summary>Issue statuses as stored (PRD-04 §7.3).</summary>
internal static class IssueStatus
{
    public const string Open = "Open";
    public const string Approved = "Approved";
    public const string ApprovedWithConditions = "ApprovedWithConditions";
    public const string Rejected = "Rejected";
    public const string Invalidated = "Invalidated";
    public const string Closed = "Closed";

    /// <summary>An issue blocks its point while Open or Rejected (BR-UW-001; an Invalidated approval is always followed by a new Open issue for its key).</summary>
    public static bool Blocks(string status) => status is Open or Rejected or Invalidated;

    /// <summary>Approved in either form.</summary>
    public static bool IsApproved(string status) => status is Approved or ApprovedWithConditions;
}

/// <summary>A non-terminal issue of the job as the reconciliation sees it.</summary>
/// <param name="IssueId">Issue id.</param>
/// <param name="IssueKey">Issue key.</param>
/// <param name="Status">Open, Approved, ApprovedWithConditions or Rejected.</param>
/// <param name="Fingerprint">Fingerprint at the last evaluation (null for issues raised before fingerprints existed).</param>
/// <param name="DecisionFingerprint">Fingerprint the decider saw (null while Open).</param>
internal sealed record ReconcilableIssue(Guid IssueId, string IssueKey, string Status, string? Fingerprint, string? DecisionFingerprint);

/// <summary>What happens to one issue key on an evaluation.</summary>
internal enum IssueAction
{
    /// <summary>No issue for the key yet: raise a new Open issue.</summary>
    Raise,

    /// <summary>An Open issue for the key: keep it and store the current fingerprint.</summary>
    KeepOpen,

    /// <summary>An approval whose facts are unchanged: it stays Approved and no longer blocks (REQ-UW-092).</summary>
    KeepApproved,

    /// <summary>A rejection whose facts are unchanged: it stays Rejected and keeps blocking.</summary>
    KeepRejected,

    /// <summary>An approval whose facts changed: Invalidated (VALUE_CHANGED) and a new Open issue for the key (REQ-UW-093).</summary>
    InvalidateAndRaise,

    /// <summary>A rejection whose facts changed: Closed (VALUE_CHANGED) and a new Open issue for the key.</summary>
    CloseAndRaise,

    /// <summary>The rule no longer hits: an Open or Rejected issue is Closed (RULE_NO_LONGER_HITS).</summary>
    Close,
}

/// <summary>One step of the reconciliation.</summary>
/// <param name="Action">Action.</param>
/// <param name="IssueKey">Issue key.</param>
/// <param name="Existing">The issue acted on (null for <see cref="IssueAction.Raise"/>).</param>
internal sealed record ReconciliationStep(IssueAction Action, string IssueKey, ReconcilableIssue? Existing);

/// <summary>
/// Reconciles an evaluation's hits with the job's non-terminal issues by issue key (REQ-UW-059, -092, -093; PRD-04 §7.3).
/// Pure: the evaluate command applies the steps. An approval holds while the fingerprint of the facts the rules read is
/// the one the decider saw; an approved issue whose rule stops hitting stays Approved (it blocks nothing).
/// </summary>
internal static class IssueReconciliation
{
    public static IReadOnlyList<ReconciliationStep> Plan(IReadOnlyCollection<ReconcilableIssue> existing, IReadOnlyCollection<string> hitKeys, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(hitKeys);
        var steps = new List<ReconciliationStep>();
        foreach (var key in hitKeys.Distinct(StringComparer.Ordinal))
        {
            var issue = existing.FirstOrDefault(i => string.Equals(i.IssueKey, key, StringComparison.Ordinal));
            var unchanged = issue?.DecisionFingerprint is { } seen && string.Equals(seen, fingerprint, StringComparison.Ordinal);
            var action = issue?.Status switch
            {
                null => IssueAction.Raise,
                IssueStatus.Open => IssueAction.KeepOpen,
                IssueStatus.Approved or IssueStatus.ApprovedWithConditions => unchanged ? IssueAction.KeepApproved : IssueAction.InvalidateAndRaise,
                IssueStatus.Rejected => unchanged ? IssueAction.KeepRejected : IssueAction.CloseAndRaise,
                _ => IssueAction.Raise,
            };
            steps.Add(new ReconciliationStep(action, key, issue));
        }

        foreach (var gone in existing.Where(i => !hitKeys.Contains(i.IssueKey, StringComparer.Ordinal) && i.Status is IssueStatus.Open or IssueStatus.Rejected))
        {
            steps.Add(new ReconciliationStep(IssueAction.Close, gone.IssueKey, gone));
        }

        return steps;
    }
}
