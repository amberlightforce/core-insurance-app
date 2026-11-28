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
/// <param name="SuggestedAssignee">Default user offered new activities of the pattern (BO-WRK-03); null for none.</param>
/// <param name="AmountBandPercent">Per amount band code, the percentage of the SLA minutes that applies (50 halves them); null for none.</param>
/// <param name="Active">False: the pattern can no longer be created (running activities are untouched).</param>
internal sealed record ActivityPattern(
    string Code, string Version, string Title, string Category, string Priority, string Queue, string SlaPolicy,
    int TargetMinutes, int EscalationMinutes, bool GateBlocking, IReadOnlyList<ModuleCode> RaisingModules,
    string? SuggestedAssignee = null, IReadOnlyDictionary<string, int>? AmountBandPercent = null, bool Active = true);

/// <summary>
/// The seed of the queue and pattern catalogue (BO-WRK-03): the migration inserts exactly these rows into <c>wrk.queue</c> and
/// <c>wrk.activity_pattern</c>; the running catalogue is read from the tables (<see cref="WorkCatalogue"/>), never from here.
/// Raisers refer to the pattern codes below.
/// </summary>
internal static class WorkCatalog
{
    public const string PsrQueue = "PSR";
    public const string PackRollbackQueue = "PACK-ROLLBACK-REVIEW";
    public const string UwReferralQueue = "UW-REFERRAL";
    public const string ScreeningQueue = "SCREENING";

    public const string CmpOpsQueue = "CMP-OPS";
    public const string DocOpsQueue = "DOC-OPS";
    public const string FinOpsQueue = "FIN-OPS";

    public const string UwReferralInformation = "WRK-UW-REFERRAL-INFORMATION";
    public const string UwReferralReferUp = "WRK-UW-REFERRAL-REFER-UP";

    public const string ScreeningHit = "WRK-PTY-SCREENING-HIT";

    public const string PackRollbackReview = "WRK-MKT-PACK-ROLLBACK-REVIEW";
    public const string PsrException = "WRK-POL-NONPAY-PSR-EXCEPTION";
    public const string PsrHold = "WRK-BIL-NONPAY-PSR-HOLD";
    public const string PsrNoticeReturned = "WRK-BIL-NONPAY-NOTICE-RETURNED";

    public const string CmpMissingRule = "WRK-CMP-MISSING-RULE";
    public const string CmpComplaintEscalated = "WRK-CMP-COMPLAINT-ESCALATED";
    public const string DocDeliveryConflict = "WRK-DOC-DELIVERY-CONFLICT";
    public const string CmpFiscalRejected = "WRK-CMP-FISCAL-REJECTED";
    public const string CmpDsarTaskFailed = "WRK-CMP-DSAR-TASK-FAILED";

    /// <summary>Roles of the PSR (policy service) queue: the same people who see POL's PSR queue.</summary>
    private static readonly string[] PsrRoles = ["Staff.UnderwritingManager", "Staff.Underwriter", "Platform.Admin"];

    public static IReadOnlyList<WorkQueue> SeedQueues { get; } =
    [
        // BO-PTY-07: potential sanctions hits wait for compliance (the same roles as pty.Screening.caseGet).
        new(ScreeningQueue, "Sanctions screening review", ["Staff.ComplianceOfficer", "Platform.Admin"]),
        new(PsrQueue, "Policy service representative review", PsrRoles),
        // Only the UW manager may review a pack rollback in POL (pol.PackRollbackException.review); the queue is no wider.
        new(PackRollbackQueue, "Pack rollback review", ["Staff.UnderwritingManager", "Platform.Admin"]),
        // BO-UW-05: referral follow-ups (information requested, referred up) are for the underwriters.
        new(UwReferralQueue, "Underwriting referral follow-up", ["Staff.UnderwritingManager", "Staff.Underwriter", "Platform.Admin"]),
        // BO-WRK-04: back-office queues for compliance, document and finance operations.
        new(CmpOpsQueue, "Compliance operations", ["Staff.ComplianceOfficer", "Platform.Admin"]),
        new(DocOpsQueue, "Document operations", ["Staff.ComplianceOfficer", "Staff.UnderwritingManager", "Platform.Admin"]),
        new(FinOpsQueue, "Finance operations", ["Staff.Finance", "Staff.ComplianceOfficer", "Platform.Admin"]),
    ];

    public static IReadOnlyList<ActivityPattern> SeedPatterns { get; } =
    [
        new(ScreeningHit, "1", "Review a potential sanctions screening hit", "REVIEW", "HIGH", ScreeningQueue, "WRK-SLA-SCREENING",
            TargetMinutes: 24 * 60, EscalationMinutes: 2 * 24 * 60, GateBlocking: false, [ModuleCode.PTY]),
        new(PackRollbackReview, "1", "Review transactions recorded under a rolled-back pack version", "REVIEW", "HIGH", PackRollbackQueue, "WRK-SLA-PACK-ROLLBACK",
            TargetMinutes: 2 * 24 * 60, EscalationMinutes: 3 * 24 * 60, GateBlocking: false, [ModuleCode.MKT, ModuleCode.POL]),
        new(UwReferralInformation, "1", "Underwriting referral waits for information", "REVIEW", "NORMAL", UwReferralQueue, "WRK-SLA-UW-REFERRAL",
            TargetMinutes: 3 * 24 * 60, EscalationMinutes: 5 * 24 * 60, GateBlocking: false, [ModuleCode.UW]),
        new(UwReferralReferUp, "1", "Underwriting referral referred up to a higher authority", "REVIEW", "HIGH", UwReferralQueue, "WRK-SLA-UW-REFERRAL",
            TargetMinutes: 24 * 60, EscalationMinutes: 2 * 24 * 60, GateBlocking: false, [ModuleCode.UW]),
        new(PsrException, "1", "Decide a non-payment cancellation held for the PSR", "REVIEW", "HIGH", PsrQueue, "WRK-SLA-PSR",
            TargetMinutes: 24 * 60, EscalationMinutes: 2 * 24 * 60, GateBlocking: false, [ModuleCode.POL]),
        new(PsrHold, "1", "Vulnerable-customer hold on a delinquency case", "REVIEW", "HIGH", PsrQueue, "WRK-SLA-PSR",
            TargetMinutes: 24 * 60, EscalationMinutes: 2 * 24 * 60, GateBlocking: false, [ModuleCode.BIL]),
        new(PsrNoticeReturned, "1", "Non-payment notice came back undelivered", "REVIEW", "NORMAL", PsrQueue, "WRK-SLA-PSR",
            TargetMinutes: 3 * 24 * 60, EscalationMinutes: 5 * 24 * 60, GateBlocking: false, [ModuleCode.BIL]),
        // BO-WRK-04: system-generated patterns raised by CMP and DOC. SLA targets are working constants until a policy owner sets them.
        new(CmpMissingRule, "1", "Complaint has no matching handling rule", "REVIEW", "HIGH", CmpOpsQueue, "WRK-SLA-CMP-OPS",
            TargetMinutes: 24 * 60, EscalationMinutes: 2 * 24 * 60, GateBlocking: false, [ModuleCode.CMP]),
        new(CmpComplaintEscalated, "1", "Complaint escalated to the alternative dispute resolution body", "REVIEW", "HIGH", CmpOpsQueue, "WRK-SLA-CMP-OPS",
            TargetMinutes: 24 * 60, EscalationMinutes: 2 * 24 * 60, GateBlocking: false, [ModuleCode.CMP]),
        new(DocDeliveryConflict, "1", "Document delivery providers disagree on the outcome", "REVIEW", "NORMAL", DocOpsQueue, "WRK-SLA-DOC-OPS",
            TargetMinutes: 3 * 24 * 60, EscalationMinutes: 5 * 24 * 60, GateBlocking: false, [ModuleCode.DOC]),
        new(CmpFiscalRejected, "1", "Fiscal document rejected by the tax authority channel", "REVIEW", "HIGH", FinOpsQueue, "WRK-SLA-FIN-OPS",
            TargetMinutes: 24 * 60, EscalationMinutes: 2 * 24 * 60, GateBlocking: false, [ModuleCode.CMP]),
        new(CmpDsarTaskFailed, "1", "A data subject request task failed in a module", "REVIEW", "HIGH", CmpOpsQueue, "WRK-SLA-CMP-OPS",
            TargetMinutes: 24 * 60, EscalationMinutes: 2 * 24 * 60, GateBlocking: false, [ModuleCode.CMP]),
    ];
}

/// <summary>An immutable view of the queue and pattern tables, as read through <see cref="WorkCatalogue"/>.</summary>
internal sealed class CatalogueSnapshot(IReadOnlyList<WorkQueue> queues, IReadOnlyList<ActivityPattern> patterns)
{
    public IReadOnlyList<WorkQueue> Queues { get; } = queues;

    /// <summary>Every pattern, inactive ones included (the admin listing).</summary>
    public IReadOnlyList<ActivityPattern> Patterns { get; } = patterns;

    /// <summary>The active pattern of the code; an inactive or unknown one is null (fail closed).</summary>
    public ActivityPattern? Pattern(string code) => Patterns.FirstOrDefault(p => p.Active && string.Equals(p.Code, code, StringComparison.Ordinal));

    public WorkQueue? Queue(string code) => Queues.FirstOrDefault(q => string.Equals(q.Code, code, StringComparison.Ordinal));

    /// <summary>Queues the roles may see.</summary>
    public IReadOnlyList<WorkQueue> QueuesFor(IEnumerable<string> roles)
    {
        var set = roles.ToHashSet(StringComparer.Ordinal);
        return [.. Queues.Where(q => q.Roles.Any(set.Contains))];
    }
}
