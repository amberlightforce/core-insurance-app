using System.Text.Json;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Claims.Persistence;

/// <summary>Recovery case; balances remain derived from sealed financial transactions.</summary>
internal sealed class RecoveryRow : ClaimsRow
{
    public RecoveryId RecoveryId { get; set; }
    public ClaimId ClaimId { get; set; }
    public ExposureId? ExposureId { get; set; }
    public string Type { get; set; } = string.Empty;
    public PartyId CounterpartyPartyId { get; set; }
    public decimal ExpectedAmount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Status { get; set; } = "OPEN";
    public JsonDocument Milestones { get; set; } = JsonDocument.Parse("[]");
    public decimal? SalvageEstimate { get; set; }
    public PartyId? BuyerPartyId { get; set; }
    public decimal? SalePrice { get; set; }
    public Guid? ReceivableId { get; set; }
    public string? PaymentReference { get; set; }
    public Guid? FsCaseId { get; set; }
    public Guid? FsStatementId { get; set; }
    public BusinessDate? DemandedOn { get; set; }
    public string? WriteOffReason { get; set; }
    public string AllocationRule { get; set; } = "PRO_RATA_PAID";
    public Instant UpdatedAt { get; set; }
}

/// <summary>Friendly settlement case, with the exact eligibility rule provenance.</summary>
internal sealed class FsCaseRow : ClaimsRow
{
    public Guid FsCaseId { get; set; }
    public ClaimId ClaimId { get; set; }
    public ExposureId? ExposureId { get; set; }
    public RecoveryId? RecoveryId { get; set; }
    public string Role { get; set; } = string.Empty;
    public PartyId CounterpartyInsurerPartyId { get; set; }
    public JsonDocument? EligibilityResult { get; set; }
    public string? EligibilityRuleId { get; set; }
    public string? EligibilityRuleVersion { get; set; }
    public string? LegalStatus { get; set; }
    public bool Provisional { get; set; }
    public decimal? ClearingValue { get; set; }
    public string Currency { get; set; } = "EUR";
    public string? ClearingReference { get; set; }
    public string? NotificationReference { get; set; }
    public string? DisputeReason { get; set; }
    public string Status { get; set; } = "ELIGIBILITY_PENDING";
    public Guid? StatementLineId { get; set; }
    public Instant UpdatedAt { get; set; }
}

/// <summary>One net settlement per legal entity, period and counterparty.</summary>
internal sealed class FsStatementRow : ClaimsRow
{
    public Guid FsStatementId { get; set; }
    public string Period { get; set; } = string.Empty;
    public PartyId CounterpartyInsurerPartyId { get; set; }
    public string ExternalReference { get; set; } = string.Empty;
    public decimal NetAmount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Direction { get; set; } = "PAYABLE";
    public string Status { get; set; } = "PENDING";
    public ApprovalRequestId? ApprovalRequestId { get; set; }
    public DisbursementId? DisbursementId { get; set; }
    public Guid? ReceivableId { get; set; }
    public Instant UpdatedAt { get; set; }
}

/// <summary>Imported clearing evidence, reconciled against a case and its financial leg.</summary>
internal sealed class FsStatementLineRow : ClaimsRow
{
    public Guid FsStatementLineId { get; set; }
    public Guid FsStatementId { get; set; }
    public string ExternalReference { get; set; } = string.Empty;
    public string ClearingReference { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string MatchStatus { get; set; } = "UNMATCHED";
    public Guid? FsCaseId { get; set; }
    public ClaimId? ClaimId { get; set; }
    public RecoveryId? RecoveryId { get; set; }
    public ClaimPaymentId? ClaimPaymentId { get; set; }
    public string? ExceptionReason { get; set; }
    public Instant UpdatedAt { get; set; }
}
