using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Billing.Persistence;

/// <summary>A billing account (REQ-BIL-030): one Active account per payer, legal entity and currency in the slice (REQ-BIL-033).</summary>
internal sealed class BillingAccountRow
{
    public BillingAccountId BillingAccountId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    public string AccountNumber { get; set; } = string.Empty;

    public PartyId PayerPartyId { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public int RecordVersion { get; set; }
}

/// <summary>A policy term attached to an account with its plan instance (REQ-BIL-031, REQ-BIL-052), from <c>PolicyBound</c>.</summary>
internal sealed class PlanInstanceRow
{
    public PolicyTermId TermId { get; set; }

    public Guid PlanInstanceId { get; set; }

    public BillingAccountId BillingAccountId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public PolicyId PolicyId { get; set; }

    public string PolicyNumber { get; set; } = string.Empty;

    public int TermNumber { get; set; }

    public PolicyTransactionId BoundTransactionId { get; set; }

    public string ProductCode { get; set; } = string.Empty;

    public string ProductVersion { get; set; } = string.Empty;

    public string ArtefactHash { get; set; } = string.Empty;

    public string PlanCode { get; set; } = string.Empty;

    public string BillMode { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public BusinessDate TermFrom { get; set; }

    public BusinessDate TermTo { get; set; }

    public Guid SourceEventId { get; set; }

    public long SourceSequence { get; set; }

    public Instant CreatedAt { get; set; }
}

/// <summary>A consumed POL charge delta, frozen as received (REQ-BIL-002, REQ-BIL-067); <see cref="Status"/> moves.</summary>
internal sealed class ChargeRow
{
    public ChargeId ChargeId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public Guid SetId { get; set; }

    public int SetSize { get; set; }

    public int SetIndex { get; set; }

    public PolicyId PolicyId { get; set; }

    public PolicyTermId TermId { get; set; }

    public PolicyTransactionId TransactionId { get; set; }

    public string ElementLocator { get; set; } = string.Empty;

    public string CoverageCode { get; set; } = string.Empty;

    public string ChargeType { get; set; } = string.Empty;

    public string ChargeCategory { get; set; } = string.Empty;

    public string DeltaKind { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public BusinessDate ValidFrom { get; set; }

    public BusinessDate? ValidTo { get; set; }

    public BusinessDate BookingDate { get; set; }

    public string CorrelationKey { get; set; } = string.Empty;

    public string? TaxTreatmentRef { get; set; }

    public Guid SourceEventId { get; set; }

    public long SourceSequence { get; set; }

    public Instant ReceivedAt { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? QuarantineReason { get; set; }

    public string? FiscalCategoryKey { get; set; }

    /// <summary>Legal status of the rate behind a tax or levy line, as POL sent it (D-SLC-19a); null for premium.</summary>
    public string? LegalStatus { get; set; }

    /// <summary>True when the line rests on a value that is not Settled (D-SLC-19a).</summary>
    public bool Provisional { get; set; }

    public Guid? WrittenEntryId { get; set; }
}

/// <summary>An invoice — a non-fiscal payment demand (D3, REQ-BIL-086, REQ-BIL-087).</summary>
internal sealed class InvoiceRow
{
    public InvoiceId InvoiceId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    public BillingAccountId BillingAccountId { get; set; }

    public string InvoiceNumber { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public PolicyId PolicyId { get; set; }

    public PolicyTermId TermId { get; set; }

    public PolicyTransactionId TransactionId { get; set; }

    public BusinessDate IssueDate { get; set; }

    public BusinessDate DueDate { get; set; }

    public string Method { get; set; } = string.Empty;

    public decimal Total { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string FiscalStatus { get; set; } = string.Empty;

    public string? FiscalTriggerPoint { get; set; }

    public FiscalDocumentId? FiscalDocumentId { get; set; }

    public string? FiscalDocumentType { get; set; }

    public string? FiscalSeries { get; set; }

    public string? FiscalNumber { get; set; }

    public string? FiscalMark { get; set; }

    public string? FiscalUid { get; set; }

    public string[] FiscalRejectionCodes { get; set; } = [];

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant? UpdatedAt { get; set; }

    public int RecordVersion { get; set; }
}

/// <summary>An invoice item carrying its POL charge (REQ-BIL-067).</summary>
internal sealed class InvoiceItemRow
{
    public Guid InvoiceItemId { get; set; }

    public InvoiceId InvoiceId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public ChargeId ChargeId { get; set; }

    public PolicyTermId TermId { get; set; }

    public PolicyTransactionId TransactionId { get; set; }

    public string ElementLocator { get; set; } = string.Empty;

    public string CoverageCode { get; set; } = string.Empty;

    public string ChargeType { get; set; } = string.Empty;

    public string ChargeCategory { get; set; } = string.Empty;

    public string? FiscalCategoryKey { get; set; }

    /// <summary>Carried from the charge (D-SLC-19a).</summary>
    public string? LegalStatus { get; set; }

    /// <summary>Carried from the charge (D-SLC-19a).</summary>
    public bool Provisional { get; set; }

    public BusinessDate ValidFrom { get; set; }

    public BusinessDate? ValidTo { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public int LineNo { get; set; }
}

/// <summary>A receipt — an incoming payment (REQ-BIL-126).</summary>
internal sealed class ReceiptRow
{
    public PaymentId ReceiptId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    public BillingAccountId BillingAccountId { get; set; }

    public string ReceiptNumber { get; set; } = string.Empty;

    public string Channel { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public BusinessDate ValueDate { get; set; }

    public BusinessDate AccountingDate { get; set; }

    public InvoiceId? InvoiceRef { get; set; }

    public string? BankReference { get; set; }

    public string State { get; set; } = string.Empty;

    public string? SuspenseReason { get; set; }

    public Instant RecordedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public int RecordVersion { get; set; }
}

/// <summary>An allocation row (REQ-BIL-130): append-only; Σ per receipt ≤ receipt and Σ per item ≤ item are enforced by the database.</summary>
internal sealed class AllocationRow
{
    public Guid AllocationId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public PaymentId ReceiptId { get; set; }

    public InvoiceId InvoiceId { get; set; }

    public Guid InvoiceItemId { get; set; }

    public PolicyTermId TermId { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string RuleId { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string Actor { get; set; } = string.Empty;

    public Instant RecordedAt { get; set; }
}

/// <summary>An account of the sub-ledger chart (PRD-06 §7.1.4, REQ-BIL-282), seeded as data.</summary>
internal sealed class LedgerAccountRow
{
    public string AccountCode { get; set; } = string.Empty;

    public string NameEn { get; set; } = string.Empty;

    public string NameEl { get; set; } = string.Empty;

    public string AccountType { get; set; } = string.Empty;

    public string NormalBalance { get; set; } = string.Empty;
}

/// <summary>A billing-ledger rule (REQ-BIL-286), seeded as data; changes are maker-checker (not built).</summary>
internal sealed class LedgerRuleRow
{
    public string RuleId { get; set; } = string.Empty;

    public int Version { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string ChargeCategory { get; set; } = string.Empty;

    public string BillMode { get; set; } = string.Empty;

    public string Jurisdiction { get; set; } = string.Empty;

    public string Qualifier { get; set; } = string.Empty;

    public string DebitAccount { get; set; } = string.Empty;

    public string CreditAccount { get; set; } = string.Empty;

    public string AmountExpression { get; set; } = string.Empty;

    public BusinessDate ValidFrom { get; set; }

    public BusinessDate? ValidTo { get; set; }

    public string Source { get; set; } = string.Empty;
}

/// <summary>A sub-ledger entry header (REQ-BIL-279, REQ-BIL-284). Append-only.</summary>
internal sealed class LedgerEntryRow
{
    public Guid EntryId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    public BillingAccountId BillingAccountId { get; set; }

    public string EntryType { get; set; } = string.Empty;

    public BusinessDate AccountingDate { get; set; }

    public BusinessDate BusinessDate { get; set; }

    public Instant RecordedAt { get; set; }

    public Guid? CauseEventId { get; set; }

    public string CauseOperation { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>Business lineage keys (JSON object, D5).</summary>
    public string LineageKeys { get; set; } = "{}";

    public Guid? ReversesEntryId { get; set; }
}

/// <summary>A sub-ledger line (REQ-BIL-279, REQ-BIL-283). Append-only.</summary>
internal sealed class LedgerLineRow
{
    public Guid LineId { get; set; }

    public Guid EntryId { get; set; }

    public int LineNo { get; set; }

    public string AccountCode { get; set; } = string.Empty;

    public string Side { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string RuleId { get; set; } = string.Empty;

    public LegalEntityId LegalEntityId { get; set; }

    public BillingAccountId? BillingAccountId { get; set; }

    public PolicyId? PolicyId { get; set; }

    public PolicyTermId? TermId { get; set; }

    public PolicyTransactionId? TransactionId { get; set; }

    public ChargeId? ChargeId { get; set; }

    public string? ChargeType { get; set; }

    public string? ChargeCategory { get; set; }

    public string? CoverageCode { get; set; }

    public string? ProductCode { get; set; }

    public string? BillMode { get; set; }

    public InvoiceId? InvoiceId { get; set; }

    public Guid? InvoiceItemId { get; set; }

    public PaymentId? ReceiptId { get; set; }

    public Guid? AllocationId { get; set; }
}

/// <summary>An intake exception (quarantined delta, blocked set, unsupported plan, failed fiscal request).</summary>
internal sealed class IntakeExceptionRow
{
    public Guid ExceptionId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string ReasonCode { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public Guid? SourceEventId { get; set; }

    public Instant RaisedAt { get; set; }
}
