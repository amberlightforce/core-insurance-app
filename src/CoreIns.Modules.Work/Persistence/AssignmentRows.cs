using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Work.Persistence;

/// <summary>
/// <c>wrk.assignee_profile</c>: a staff member the assignment rules may route to (BO-WRK-02). Stand-in for the platform user directory
/// until BO-PLT-05 exists; <c>IWorkUserDirectory</c> is the port that will switch over.
/// </summary>
internal sealed class AssigneeProfileRow
{
    public LegalEntityId LegalEntityId { get; set; }

    /// <summary>The user id the token carries (an assignee id string).</summary>
    public string UserId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Comma separated roles of the user (what the queue's role list is matched against).</summary>
    public string Roles { get; set; } = string.Empty;

    /// <summary>Comma separated skills for skill based rules.</summary>
    public string Skills { get; set; } = string.Empty;

    public bool Active { get; set; } = true;
}

/// <summary><c>wrk.assignment_rule</c>: an ordered routing rule for a queue (and optionally one pattern).</summary>
internal sealed class AssignmentRuleRow
{
    public const string RoundRobinOpenCount = "ROUND_ROBIN_OPEN_COUNT";

    public Guid RuleId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    /// <summary>Business code shown in the rule path and in <c>ActivityAssigned.ruleRef</c>.</summary>
    public string Code { get; set; } = string.Empty;

    public string QueueCode { get; set; } = string.Empty;

    /// <summary>Null: the rule applies to every pattern of the queue.</summary>
    public string? PatternCode { get; set; }

    public string? RequiredRole { get; set; }

    public string? RequiredSkill { get; set; }

    public string Strategy { get; set; } = RoundRobinOpenCount;

    /// <summary>Lower runs first.</summary>
    public int Priority { get; set; }

    public bool Active { get; set; } = true;
}
