using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Party.Persistence;

/// <summary>
/// <c>pty.agency_user</c> (BO-PTY-12, REQ-PTY-243/244): the association of an external-realm identity (an opaque subject; identity itself is
/// PLT's and the external realm's) with an agency (an intermediary), its agency role and its status. A user belongs to one agency.
/// </summary>
internal sealed class AgencyUserRow
{
    public Guid AgencyUserId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public IntermediaryId IntermediaryId { get; set; }

    public string ExternalSubject { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>AGENCY_ADMIN, OFFICE_MANAGER, PRODUCER or AGENCY_STAFF.</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>ACTIVE, SUSPENDED or REMOVED.</summary>
    public string Status { get; set; } = string.Empty;

    public string? StatusReason { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant UpdatedAt { get; set; }
}

/// <summary>
/// <c>pty.agency_grant</c> (BO-PTY-12, REQ-PTY-245): one producer-code access grant of an agency user. A revoked grant keeps its row
/// (<see cref="RevokedAt"/>); the app role has no DELETE. While the user is suspended the open grants stay but PLT holds none.
/// </summary>
internal sealed class AgencyGrantRow
{
    public Guid GrantId { get; set; }

    public Guid AgencyUserId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public IntermediaryId IntermediaryId { get; set; }

    public string ProducerCode { get; set; } = string.Empty;

    public Instant GrantedAt { get; set; }

    public string GrantedBy { get; set; } = string.Empty;

    public Instant? RevokedAt { get; set; }

    public string? RevokedBy { get; set; }
}
