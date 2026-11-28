using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Party.Persistence;

/// <summary>
/// <c>pty.screening_case</c> (BO-PTY-07, REQ-PTY-006, REQ-PTY-177, REQ-PTY-185): the review of one potential sanctions hit. A case is OPEN until a
/// decision is proposed (PENDING_APPROVAL), and ends CLEARED or TRUE_MATCH when a second person approves it. The subject (a party, or an ad-hoc
/// payee that has no party) is keyed by <see cref="SubjectKey"/>; <see cref="MatchFingerprint"/> identifies what the provider matched, so a
/// cleared case suppresses only the same matches later. <see cref="SubjectName"/> is personal data (P2): read by the case queue only.
/// </summary>
internal sealed class ScreeningCaseRow
{
    public Guid CaseId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string CaseNumber { get; set; } = string.Empty;

    /// <summary>The party, or null for an ad-hoc payee.</summary>
    public PartyId? PartyId { get; set; }

    /// <summary><c>P:{partyId}</c> or <c>A:{hash of the ad-hoc subject}</c>.</summary>
    public string SubjectKey { get; set; } = string.Empty;

    public string? SubjectName { get; set; }

    public string? Country { get; set; }

    public string Status { get; set; } = string.Empty;

    public string MatchFingerprint { get; set; } = string.Empty;

    /// <summary>JSON array of the provider's matches.</summary>
    public string Evidence { get; set; } = "[]";

    /// <summary>Comma-separated list versions the screening ran against.</summary>
    public string ListVersions { get; set; } = string.Empty;

    public string CallerModule { get; set; } = string.Empty;

    public string CallerType { get; set; } = string.Empty;

    public string CallerId { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public string? ProposedDecision { get; set; }

    public string? ProposedBy { get; set; }

    public Instant? ProposedAt { get; set; }

    public string? ProposalComment { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    public string? ContentHash { get; set; }

    /// <summary>Who decided last: the approver of a final decision, or the rejecting checker while the case is OPEN again.</summary>
    public string? DecidedBy { get; set; }

    public Instant? DecidedAt { get; set; }

    public string? DecisionComment { get; set; }
}
