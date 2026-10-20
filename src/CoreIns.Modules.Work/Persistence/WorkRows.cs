using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Work.Persistence;

/// <summary><c>wrk.activity</c>: one unit of staff work (a task) created from a pattern, with its assignment and SLA clock.</summary>
internal sealed class ActivityRow
{
    public const string Open = "OPEN";
    public const string Completed = "COMPLETED";
    public const string Skipped = "SKIPPED";
    public const string Cancelled = "CANCELLED";

    public const string User = "USER";
    public const string Group = "GROUP";
    public const string Queue = "QUEUE";

    public Guid ActivityId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    /// <summary>Business number shown to staff.</summary>
    public string Number { get; set; } = string.Empty;

    public string PatternCode { get; set; } = string.Empty;

    public string PatternVersion { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Priority { get; set; } = "NORMAL";

    public string SlaPolicy { get; set; } = string.Empty;

    public bool GateBlocking { get; set; }

    /// <summary>OPEN, COMPLETED, SKIPPED or CANCELLED.</summary>
    public string State { get; set; } = Open;

    public string PrimaryModule { get; set; } = string.Empty;

    public string PrimaryType { get; set; } = string.Empty;

    public string PrimaryId { get; set; } = string.Empty;

    /// <summary>Secondary object references as a JSON array of <c>{module,type,id}</c>.</summary>
    public string SecondaryRefs { get; set; } = "[]";

    /// <summary>The data subject when the primary or a secondary object is a party (DSAR export/restriction); null otherwise.</summary>
    public Guid? SubjectPartyId { get; set; }

    public string RaisingModule { get; set; } = string.Empty;

    /// <summary>The event or record that caused it (empty string when none: part of the open-duplicate key).</summary>
    public string CauseRef { get; set; } = string.Empty;

    /// <summary>USER, GROUP or QUEUE.</summary>
    public string AssigneeType { get; set; } = Queue;

    public string AssigneeId { get; set; } = string.Empty;

    /// <summary>The queue the activity was created in (kept after a claim).</summary>
    public string QueueCode { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant? TargetAt { get; set; }

    public Instant? EscalationAt { get; set; }

    /// <summary>Set once by the SLA sweep (or at close) when the target passed; SLABreached is published exactly then.</summary>
    public Instant? BreachedAt { get; set; }

    public Instant UpdatedAt { get; set; }

    public Instant? ClosedAt { get; set; }

    public string? ClosedBy { get; set; }

    public string? Outcome { get; set; }

    public string? Note { get; set; }

    public string? CloseReason { get; set; }

    public int RecordVersion { get; set; } = 1;
}
