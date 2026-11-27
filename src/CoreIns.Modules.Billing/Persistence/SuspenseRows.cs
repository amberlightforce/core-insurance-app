using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Billing.Persistence;

/// <summary>States of a suspense item (BO-BIL-08).</summary>
internal static class SuspenseStates
{
    /// <summary>Unapplied cash awaiting a decision: allocate, transfer or refund to the sender.</summary>
    public const string Open = "OPEN";

    /// <summary>A refund to the sender waits for its approval (a rejection reopens the item).</summary>
    public const string RefundPending = "REFUND_PENDING";

    /// <summary>The receipt was applied to invoices.</summary>
    public const string Allocated = "ALLOCATED";

    /// <summary>The cash was paid back to the sender (refund approved and disbursed).</summary>
    public const string Refunded = "REFUNDED";

    public const string ReasonTransferred = "TRANSFERRED";

    public static readonly string[] All = [Open, RefundPending, Allocated, Refunded];
}

/// <summary>
/// An unmatched payment held in suspense, first-class (BO-BIL-08, REQ-BIL-135): one per receipt that could not be applied.
/// The receipt stays the money fact; the item carries the workbench state. <see cref="BillingAccountId"/> follows the receipt's
/// account (it changes with a transfer).
/// </summary>
internal sealed class SuspenseItemRow
{
    public Guid SuspenseItemId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public PaymentId ReceiptId { get; set; }

    public BillingAccountId BillingAccountId { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    /// <summary>The refund to the sender of the item (kept after a rejection, replaced by a new request).</summary>
    public RefundId? RefundId { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant? ResolvedAt { get; set; }

    public string? ResolvedBy { get; set; }

    public int RecordVersion { get; set; }
}
