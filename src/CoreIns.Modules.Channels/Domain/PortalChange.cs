using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Channels.Domain;

/// <summary>Lifecycle of a customer's self-service change (BO-CHN-03).</summary>
internal static class PortalChangeStatuses
{
    /// <summary>A POL change job was started and quoted; nothing is bound.</summary>
    public const string Previewed = "PREVIEWED";

    /// <summary>The previewed quote version was bound.</summary>
    public const string Confirmed = "CONFIRMED";

    /// <summary>Handed to staff (a ticket exists); nothing was or will be bound by the customer.</summary>
    public const string Referred = "REFERRED";

    public static readonly IReadOnlyList<string> All = [Previewed, Confirmed, Referred];
}

/// <summary>
/// A customer's change to one of their own policies: ties the POL change job to the customer, remembers the amount the matrix was asked about at
/// preview, and makes confirm idempotent and bound to exactly the previewed quote version. Holds no personal data (ids and an amount).
/// </summary>
internal sealed class PortalChangeRow
{
    public Guid ChangeId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    /// <summary>The customer party (the linked party of the portal identity).</summary>
    public PartyId PartyId { get; set; }

    public PolicyId PolicyId { get; set; }

    public JobId JobId { get; set; }

    /// <summary>The quote version of the job that was previewed and is the only one confirm binds.</summary>
    public int VersionNo { get; set; }

    public Instant EffectiveAt { get; set; }

    /// <summary>Absolute total change in the policy currency: the amount the matrix limit is checked against.</summary>
    public decimal Amount { get; set; }

    public string Currency { get; set; } = "EUR";

    /// <summary>The payment plan option of the term, used when binding.</summary>
    public string PaymentPlanOption { get; set; } = string.Empty;

    public string Status { get; set; } = PortalChangeStatuses.Previewed;

    public Guid? TicketId { get; set; }

    public Guid? TransactionId { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }
}
