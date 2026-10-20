using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Work.Domain;

/// <summary>A work queue: the staff roles that may see and claim its activities.</summary>
/// <param name="Code">Queue code (the assignee id of a QUEUE assignment).</param>
/// <param name="Title">English title.</param>
/// <param name="Roles">Roles that may see the queue and claim from it. A role outside this list never sees the activity.</param>
internal sealed record WorkQueue(string Code, string Title, IReadOnlyList<string> Roles);

/// <summary>An activity pattern (PRD-13 REQ-WRK-001): what is created, for whom, and the SLA it carries.</summary>
/// <param name="Code">Pattern code.</param>
/// <param name="Version">Pattern version.</param>
/// <param name="Title">English title.</param>
/// <param name="Category">Event category.</param>
/// <param name="Priority">NORMAL or HIGH.</param>
/// <param name="Queue">Queue code the activity starts in.</param>
/// <param name="SlaPolicy">SLA policy code carried by SLABreached.</param>
/// <param name="TargetMinutes">Minutes from creation to the SLA target (calendar minutes).</param>
/// <param name="EscalationMinutes">Minutes from creation to escalation.</param>
/// <param name="GateBlocking">True when an open instance blocks the gate on its primary object (wrk.Activity.blockingStatus).</param>
/// <param name="RaisingModules">Modules that may create the pattern; any other raising module is refused.</param>
internal sealed record ActivityPattern(
    string Code, string Version, string Title, string Category, string Priority, string Queue, string SlaPolicy,
    int TargetMinutes, int EscalationMinutes, bool GateBlocking, IReadOnlyList<ModuleCode> RaisingModules);

/// <summary>
/// The activity patterns and queues the Work core serves (NX-HD-05): the PSR and pack-rollback review activities (D-SL5-10, D-SL6-04).
/// TODO(hardening): patterns and queues are configuration in PRD-13 (MKT-owned); they are code constants until that registry exists.
/// </summary>
internal static class WorkCatalog
{
    public const string PsrQueue = "PSR";
    public const string PackRollbackQueue = "PACK-ROLLBACK-REVIEW";

    public const string PackRollbackReview = "WRK-MKT-PACK-ROLLBACK-REVIEW";
    public const string PsrException = "WRK-POL-NONPAY-PSR-EXCEPTION";
    public const string PsrHold = "WRK-BIL-NONPAY-PSR-HOLD";
    public const string PsrNoticeReturned = "WRK-BIL-NONPAY-NOTICE-RETURNED";

    /// <summary>Roles of the PSR (policy service) queue: the same people who see POL's PSR queue.</summary>
    private static readonly string[] PsrRoles = ["Staff.UnderwritingManager", "Staff.Underwriter", "Platform.Admin"];

    public static IReadOnlyList<WorkQueue> Queues { get; } =
    [
        new(PsrQueue, "Policy service representative review", PsrRoles),
        // Only the UW manager may review a pack rollback in POL (pol.PackRollbackException.review); the queue is no wider.
        new(PackRollbackQueue, "Pack rollback review", ["Staff.UnderwritingManager", "Platform.Admin"]),
    ];

    public static IReadOnlyList<ActivityPattern> Patterns { get; } =
    [
        new(PackRollbackReview, "1", "Review transactions recorded under a rolled-back pack version", "REVIEW", "HIGH", PackRollbackQueue, "WRK-SLA-PACK-ROLLBACK",
            TargetMinutes: 2 * 24 * 60, EscalationMinutes: 3 * 24 * 60, GateBlocking: false, [ModuleCode.MKT, ModuleCode.POL]),
        new(PsrException, "1", "Decide a non-payment cancellation held for the PSR", "REVIEW", "HIGH", PsrQueue, "WRK-SLA-PSR",
            TargetMinutes: 24 * 60, EscalationMinutes: 2 * 24 * 60, GateBlocking: false, [ModuleCode.POL]),
        new(PsrHold, "1", "Vulnerable-customer hold on a delinquency case", "REVIEW", "HIGH", PsrQueue, "WRK-SLA-PSR",
            TargetMinutes: 24 * 60, EscalationMinutes: 2 * 24 * 60, GateBlocking: false, [ModuleCode.BIL]),
        new(PsrNoticeReturned, "1", "Non-payment notice came back undelivered", "REVIEW", "NORMAL", PsrQueue, "WRK-SLA-PSR",
            TargetMinutes: 3 * 24 * 60, EscalationMinutes: 5 * 24 * 60, GateBlocking: false, [ModuleCode.BIL]),
    ];

    public static ActivityPattern? Pattern(string code) => Patterns.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.Ordinal));

    public static WorkQueue? Queue(string code) => Queues.FirstOrDefault(q => string.Equals(q.Code, code, StringComparison.Ordinal));

    /// <summary>Queues the roles may see.</summary>
    public static IReadOnlyList<WorkQueue> QueuesFor(IEnumerable<string> roles)
    {
        var set = roles.ToHashSet(StringComparer.Ordinal);
        return [.. Queues.Where(q => q.Roles.Any(set.Contains))];
    }
}
