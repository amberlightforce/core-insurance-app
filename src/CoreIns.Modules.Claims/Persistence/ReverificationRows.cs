using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Claims.Persistence;

/// <summary>
/// <c>clm.reverification</c>: one re-verification demand of a claim per cause (REQ-CLM-057, D-SL3-03 d). Written by the
/// <c>PolicyChanged</c> / <c>PolicyCancelled</c> consumers when POL reports the claim's snapshot superseded; unique per
/// (claim, cause event) so a redelivery or a replay raises nothing twice. The binding (claim, cause, old and new ref) is
/// frozen; only the decision moves (Open → Kept / Adopted, REQ-CLM-058). Holds no personal data besides the encrypted comment.
/// </summary>
internal sealed class ReverificationRow : ClaimsRow
{
    public const string Open = "OPEN";
    public const string Kept = "KEPT";
    public const string Adopted = "ADOPTED";

    public Guid ReverificationId { get; set; }

    public ClaimId ClaimId { get; set; }

    /// <summary>Event id of the POL event that caused the demand (the idempotency key together with the claim).</summary>
    public Guid CauseEventId { get; set; }

    public string CauseEventType { get; set; } = string.Empty;

    /// <summary>The ref the claim held when the demand was raised.</summary>
    public string OldSnapshotRef { get; set; } = string.Empty;

    /// <summary>POL's <c>supersession.successorRef</c> at that time.</summary>
    public string NewSnapshotRef { get; set; } = string.Empty;

    public Instant RaisedAt { get; set; }

    public string Status { get; set; } = Open;

    public string? ReasonCode { get; set; }

    /// <summary>Free-text comment of the decision, P2 envelope bound to the reverification id.</summary>
    public byte[]? CommentEncrypted { get; set; }

    /// <summary>True when the adoption removed the cover of at least one exposure.</summary>
    public bool? CoverageInQuestion { get; set; }

    public Instant? DecidedAt { get; set; }

    public string? DecidedBy { get; set; }
}
