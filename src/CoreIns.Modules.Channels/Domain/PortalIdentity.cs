using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Channels.Domain;

/// <summary>Statuses of a portal identity link.</summary>
internal static class PortalIdentityStatuses
{
    public const string Linked = "LINKED";
    public const string Unlinked = "UNLINKED";

    public static readonly IReadOnlyList<string> All = [Linked, Unlinked];
}

/// <summary>
/// One link of a customer sign-in identity (external subject) to a party (BO-CHN-01). An identity has at most one LINKED row; unlinking
/// keeps the row as history (UNLINKED) and a relink creates a new row, so every change is traceable (the app role has no DELETE).
/// </summary>
internal sealed class PortalIdentityRow
{
    public Guid PortalIdentityId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    /// <summary>The external subject: <c>RequestContext.Actor.Id</c> of the signed-in customer.</summary>
    public string Subject { get; set; } = string.Empty;

    public PartyId PartyId { get; set; }

    public string Status { get; set; } = PortalIdentityStatuses.Linked;

    /// <summary>How the person was verified before the link (free text recorded by the staff member).</summary>
    public string Verification { get; set; } = string.Empty;

    public Instant LinkedAt { get; set; }

    public string LinkedBy { get; set; } = string.Empty;

    public Instant? UnlinkedAt { get; set; }

    public string? UnlinkedBy { get; set; }

    public string? UnlinkReason { get; set; }

    public int RecordVersion { get; set; }
}
