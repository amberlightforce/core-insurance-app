using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Channels.Domain;

/// <summary>Notification kinds and the class a customer can switch on or off (BO-CHN-04).</summary>
internal static class NotificationKinds
{
    public const string RenewalOffered = "RENEWAL_OFFERED";
    public const string InvoiceIssued = "INVOICE_ISSUED";
    public const string ClaimUpdated = "CLAIM_UPDATED";
    public const string DocumentReady = "DOCUMENT_READY";
    public const string MessageReply = "MESSAGE_REPLY";

    public const string ClassRenewal = "RENEWAL";
    public const string ClassBilling = "BILLING";
    public const string ClassClaim = "CLAIM";
    public const string ClassDocument = "DOCUMENT";
    public const string ClassMessage = "MESSAGE";

    public static readonly IReadOnlyList<string> Classes = [ClassRenewal, ClassBilling, ClassClaim, ClassDocument, ClassMessage];

    public static readonly IReadOnlyList<string> Kinds = [RenewalOffered, InvoiceIssued, ClaimUpdated, DocumentReady, MessageReply];

    public static string ClassOf(string kind) => kind switch
    {
        RenewalOffered => ClassRenewal,
        InvoiceIssued => ClassBilling,
        ClaimUpdated => ClassClaim,
        DocumentReady => ClassDocument,
        MessageReply => ClassMessage,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>Message directions and topics.</summary>
internal static class MessageValues
{
    public const string Customer = "CUSTOMER";
    public const string Staff = "STAFF";

    public static readonly IReadOnlyList<string> Directions = [Customer, Staff];

    public static readonly IReadOnlyList<string> Topics = ["GENERAL", "POLICY", "BILLING", "CLAIM", "DOCUMENT"];

    public const int MaxTextLength = 4000;
}

/// <summary>
/// One in-app notification of a customer party, derived from an owner event. Holds no personal data: only the kind and an opaque subject
/// reference; the portal renders the text. <see cref="SourceKey"/> makes a redelivered event a no-op.
/// </summary>
internal sealed class NotificationRow
{
    public Guid NotificationId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public PartyId PartyId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string NotificationClass { get; set; } = string.Empty;

    public string? SubjectType { get; set; }

    public string? SubjectId { get; set; }

    /// <summary>Event id (or message id) the notification came from; unique per party and kind.</summary>
    public string SourceKey { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public Instant? ReadAt { get; set; }
}

/// <summary>A customer's on/off choice for one notification class. No row means on.</summary>
internal sealed class NotificationSubscriptionRow
{
    public Guid SubscriptionId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public PartyId PartyId { get; set; }

    public string NotificationClass { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public Instant UpdatedAt { get; set; }
}

/// <summary>One secure message of a thread between a customer party and staff. Append-only.</summary>
internal sealed class MessageRow
{
    public Guid MessageId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public Guid ThreadId { get; set; }

    /// <summary>The customer party the thread belongs to.</summary>
    public PartyId PartyId { get; set; }

    public string Direction { get; set; } = MessageValues.Customer;

    public string Topic { get; set; } = "GENERAL";

    public PolicyId? PolicyId { get; set; }

    public string Text { get; set; } = string.Empty;

    public Instant SentAt { get; set; }

    /// <summary>The sender's actor (<c>kind:id</c>).</summary>
    public string SentBy { get; set; } = string.Empty;
}

/// <summary>Records that a portal identity opened a document (first and last time, count): the "opened by portal" record.</summary>
internal sealed class DocumentOpenRow
{
    public Guid DocumentOpenId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public Guid DocumentId { get; set; }

    public PartyId PartyId { get; set; }

    public Instant FirstOpenedAt { get; set; }

    public Instant LastOpenedAt { get; set; }

    public int OpenCount { get; set; }
}
