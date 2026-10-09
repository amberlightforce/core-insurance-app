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

    /// <summary>The expiring term this renewal term continues (<c>RenewalBound.predecessorTermId</c>); null for a new-business term.</summary>
    public PolicyTermId? PredecessorTermId { get; set; }

    /// <summary>Effective date of the term's cancellation (<c>PolicyCancelled</c>): the planned items from this date are stopped (REQ-BIL-074).</summary>
    public BusinessDate? CancelledEffective { get; set; }

    /// <summary>Cancellation source of the term's cancellation (shared code list, REQ-POL-205).</summary>
    public string? CancellationSource { get; set; }
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

    /// <summary>Transaction kind of the POL transaction (MKT TaxTransactionKind code); null only on a pre-slice-3 delta (legacy new business).</summary>
    public string? TransactionKind { get; set; }

    /// <summary>Cancellation source of a CANCELLATION or VOID transaction.</summary>
    public string? CancellationSource { get; set; }

    /// <summary>MKT treatment rule that decided a tax or levy line of a servicing transaction.</summary>
    public string? TreatmentRuleId { get; set; }

    /// <summary>Version of <see cref="TreatmentRuleId"/>.</summary>
    public string? TreatmentRuleVersion { get; set; }
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

    /// <summary>For a CREDIT_NOTE, the invoice it corrects (REQ-BIL-091); null for an INVOICE.</summary>
    public InvoiceId? OriginalInvoiceId { get; set; }

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

    /// <summary>Carried from the charge: the POL transaction kind (servicing items; null on a legacy new-business item).</summary>
    public string? TransactionKind { get; set; }

    /// <summary>Carried from the charge: the cancellation source of a cancellation item.</summary>
    public string? CancellationSource { get; set; }

    /// <summary>Carried from the charge: the treatment rule of a tax or levy item on a servicing transaction.</summary>
    public string? TreatmentRuleId { get; set; }

    /// <summary>For an item of a credit note, the original invoice item it credits; null on an invoice item.</summary>
    public Guid? CreditsItemId { get; set; }
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

    /// <summary>The billing account of an account entry; null for a disbursement entry (SL2-BIL-DISB).</summary>
    public BillingAccountId? BillingAccountId { get; set; }

    /// <summary>The disbursement of a disbursement entry (DISBURSEMENT_RELEASED, DISBURSEMENT_CLEARED).</summary>
    public DisbursementId? DisbursementId { get; set; }

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

    public DisbursementId? DisbursementId { get; set; }

    /// <summary>Disbursement source type (REQ-BIL-354), e.g. CLM_CLAIM_PAYMENT.</summary>
    public string? SourceType { get; set; }

    /// <summary>Id of the disbursement's source object (e.g. the CLM claim payment).</summary>
    public string? SourceId { get; set; }

    public ClaimId? ClaimId { get; set; }

    /// <summary>Transaction kind dimension (servicing entries, always set there).</summary>
    public string? TransactionKind { get; set; }

    /// <summary>Cancellation source dimension (cancellation-sourced entries, always set there).</summary>
    public string? CancellationSource { get; set; }

    /// <summary>Treatment rule dimension (tax and levy lines of servicing entries, always set there).</summary>
    public string? TreatmentRuleId { get; set; }
}

/// <summary>
/// A credit note item's credit used against an invoice item (REQ-BIL-073): the credit note offsets the original invoice's
/// open balance. Append-only; Σ per credit item ≤ the credit item and Σ with the cash allocations ≤ the invoice item are
/// enforced by the database. No ledger entry: the credit is already in LA-02 through CREDIT_BILLED. The credit that is
/// left unapplied is the account's credit balance (<see cref="CreditApplicationTargets"/> lists the uses a refund adds).
/// </summary>
internal sealed class CreditApplicationRow
{
    public Guid CreditApplicationId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public BillingAccountId BillingAccountId { get; set; }

    public InvoiceId CreditNoteId { get; set; }

    public Guid CreditItemId { get; set; }

    /// <summary>What the credit went to: <see cref="CreditApplicationTargets.InvoiceItem"/> now; SL3-BIL-REFUND adds its own.</summary>
    public string TargetKind { get; set; } = string.Empty;

    public InvoiceId? TargetInvoiceId { get; set; }

    public Guid? TargetInvoiceItemId { get; set; }

    /// <summary>The refund a <see cref="CreditApplicationTargets.Refund"/> or <see cref="CreditApplicationTargets.Netting"/> application belongs to (REQ-BIL-182).</summary>
    public RefundId? RefundId { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string Actor { get; set; } = string.Empty;

    public Instant RecordedAt { get; set; }
}

/// <summary>Targets a credit can be applied to.</summary>
internal static class CreditApplicationTargets
{
    /// <summary>The open balance of an invoice item (the original invoice, REQ-BIL-073).</summary>
    public const string InvoiceItem = "INVOICE_ITEM";

    /// <summary>Credit paid out by an approved refund (SL3-BIL-REFUND, REQ-BIL-181); no target item.</summary>
    public const string Refund = "REFUND";

    /// <summary>Credit netted against an open invoice item of another term or document of the account before refunding (REQ-BIL-182).</summary>
    public const string Netting = "NETTING";
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

/// <summary>
/// A policy refund (REQ-BIL-007, -181…-191): proposed from the remaining credit of a billing account, approved by rule or by
/// a second person under <c>BIL.REFUND</c> authority on the refund total, then paid through the disbursement service
/// (source BIL_REFUND). The amount, payee and lines are frozen once the refund is approved (trigger); a rejected refund is
/// never reopened (a resubmission is a new refund pointing back to it, so a rejection cannot be laundered, PITFALLS 6).
/// </summary>
internal sealed class RefundRow
{
    public RefundId RefundId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    public BillingAccountId BillingAccountId { get; set; }

    public string State { get; set; } = string.Empty;

    public string ApprovalState { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public PartyId PayeePartyId { get; set; }

    public Guid PayeeAccountId { get; set; }

    /// <summary>True when the payee account replaced an earlier account of the party (REQ-BIL-188 dimension payeeChanged).</summary>
    public bool PayeeChanged { get; set; }

    /// <summary>Actor who registered the payee account used (REQ-BIL-189: the person who changed the payee cannot approve).</summary>
    public string PayeeAccountChangedBy { get; set; } = string.Empty;

    public string PayoutMethod { get; set; } = string.Empty;

    public string ReasonCode { get; set; } = string.Empty;

    public string? Comment { get; set; }

    /// <summary>The credit notes the requester selected (null = all credit of the account).</summary>
    public Guid[]? SelectedCreditNotes { get; set; }

    /// <summary>The business reference of the credit paid out: SHA-256 over the sorted credit item ids and amounts (duplicate key of the disbursement).</summary>
    public string CreditSetKey { get; set; } = string.Empty;

    /// <summary>The refund this one resubmits after a rejection; such a refund is always approved by a second person.</summary>
    public RefundId? ResubmitsRefundId { get; set; }

    /// <summary>Everyone who took part in making this refund: the requester, resubmitters and (inherited) editors (REQ-BIL-189).</summary>
    public string[] Participants { get; set; } = [];

    /// <summary>The actor who requested or last resubmitted it (the maker of the approval request).</summary>
    public string RequestedBy { get; set; } = string.Empty;

    public Guid? ApprovalRequestId { get; set; }

    public string? ApprovalContentHash { get; set; }

    public string? DecidedBy { get; set; }

    public Instant? DecidedAt { get; set; }

    public string? DecisionComment { get; set; }

    public DisbursementId? DisbursementId { get; set; }

    public Guid? ApprovedEntryId { get; set; }

    public Instant ProposedAt { get; set; }

    public Instant? PaidAt { get; set; }

    public int RecordVersion { get; set; }
}

/// <summary>A credit item (of a credit note) a refund pays out, with the amount (append-only; the breakdown of REQ-BIL-184).</summary>
internal sealed class RefundCreditRow
{
    public Guid RefundCreditId { get; set; }

    public RefundId RefundId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public InvoiceId CreditNoteId { get; set; }

    public Guid CreditItemId { get; set; }

    public PolicyId PolicyId { get; set; }

    public PolicyTermId TermId { get; set; }

    public PolicyTransactionId TransactionId { get; set; }

    public string ChargeType { get; set; } = string.Empty;

    public string ChargeCategory { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;
}

/// <summary>A credit netted against an open invoice item before the refund (append-only, REQ-BIL-182).</summary>
internal sealed class RefundNettingRow
{
    public Guid RefundNettingId { get; set; }

    public RefundId RefundId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public InvoiceId CreditNoteId { get; set; }

    public Guid CreditItemId { get; set; }

    public InvoiceId TargetInvoiceId { get; set; }

    public Guid TargetInvoiceItemId { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;
}
