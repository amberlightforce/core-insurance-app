using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Billing.Persistence;

internal sealed class ReceivableRow
{
    public Guid ReceivableId { get; set; }
    public LegalEntityId LegalEntityId { get; set; }
    public BillingAccountId BillingAccountId { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public PartyId CounterpartyPartyId { get; set; }
    public ClaimId? ClaimId { get; set; }
    public Guid? RecoveryId { get; set; }
    public string? StatementRef { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public BusinessDate DueDate { get; set; }
    public string PaymentReference { get; set; } = string.Empty;
    public Instant RegisteredAt { get; set; }
    public string RegisteredBy { get; set; } = string.Empty;
    public int RecordVersion { get; set; }
}

internal sealed class ReceivableAllocationRow
{
    public Guid AllocationId { get; set; }
    public LegalEntityId LegalEntityId { get; set; }
    public Guid ReceivableId { get; set; }
    public PaymentId ReceiptId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public Instant AllocatedAt { get; set; }
}
