using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Claims.Persistence;

/// <summary><c>clm.reserve_line</c>: exposure × cost type × cost category × currency (REQ-CLM-093), created on first use.</summary>
internal sealed class ReserveLineRow : ClaimsRow
{
    public ReserveLineId ReserveLineId { get; set; }

    public ClaimId ClaimId { get; set; }

    public ExposureId ExposureId { get; set; }

    public string CostType { get; set; } = string.Empty;

    public string CostCategory { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    /// <summary>FinalLine (final payment or release to zero) vs OpenLine (PRD-07 §7.3.9). The balance itself is never stored.</summary>
    public bool FinalFlag { get; set; }

    public Instant UpdatedAt { get; set; }
}

/// <summary>
/// <c>clm.transaction_set</c>: the ledger header (D-ARC-34). Its creating transaction id seals its lines; its status moves
/// forward only (trigger); content and approval binding are frozen once written.
/// </summary>
internal sealed class TransactionSetRow : ClaimsRow
{
    public ClaimTransactionSetId SetId { get; set; }

    public ClaimId ClaimId { get; set; }

    public string Status { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public string BasisHash { get; set; } = string.Empty;

    public string? Submitter { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    public string? ApprovalType { get; set; }

    public string? ApprovalSubjectType { get; set; }

    public string? ApprovalSubjectId { get; set; }

    public string? ApprovalPayloadHash { get; set; }

    public string? ApprovalAuthorityType { get; set; }

    public decimal? ApprovalAuthorityAmount { get; set; }

    public string? ApprovalAuthorityCostType { get; set; }

    public string? ReferralRole { get; set; }

    public Guid[] AuthorityCheckIds { get; set; } = [];

    public bool FourEyes { get; set; }

    public string? Approver { get; set; }

    public Guid? ApproverUserId { get; set; }

    public string? RejectionReason { get; set; }

    public Instant? SubmittedAt { get; set; }

    public Instant? ApprovedAt { get; set; }

    public Instant? DecidedAt { get; set; }

    public Instant UpdatedAt { get; set; }

    /// <summary>txid_current() of the creating transaction (database default); the seal of D-ARC-34.</summary>
    public long CreatedTxid { get; set; }
}

/// <summary>
/// <c>clm.financial_transaction</c>: the immutable ledger line (PRD-07 §7.1 ClaimFinancialTransaction). Insert-only for the
/// app role, append-only for every role, sealed to its set's creating transaction (D-ARC-34). Its status is derived from
/// the set and the payment; corrections are linked negative transactions (<see cref="ReversesTxnId"/>).
/// </summary>
internal sealed class FinancialTransactionRow : ClaimsRow
{
    public Guid TxnId { get; set; }

    public ClaimTransactionSetId SetId { get; set; }

    public ClaimId ClaimId { get; set; }

    public ReserveLineId ReserveLineId { get; set; }

    public ExposureId ExposureId { get; set; }

    public int Sequence { get; set; }

    public string TxnNumber { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal FunctionalAmount { get; set; }

    public string FunctionalCurrency { get; set; } = string.Empty;

    public decimal GroupAmount { get; set; }

    public string GroupCurrency { get; set; } = string.Empty;

    public Guid? FxRateId { get; set; }

    public bool? Eroding { get; set; }

    public string? PaymentType { get; set; }

    public ClaimPaymentId? ClaimPaymentId { get; set; }

    public Guid? ReversesTxnId { get; set; }

    public string? ReasonCode { get; set; }

    public bool Proposed { get; set; }

    /// <summary>Business date the transaction was recorded (the FX date, D-SL2-06).</summary>
    public BusinessDate TransactionDate { get; set; }
}

/// <summary><c>clm.claim_payment</c>: one payment of a set (PRD-07 §7.1 ClaimPayment). No IBAN (R-38).</summary>
internal sealed class ClaimPaymentRow : ClaimsRow
{
    public ClaimPaymentId ClaimPaymentId { get; set; }

    public ClaimId ClaimId { get; set; }

    public ClaimTransactionSetId SetId { get; set; }

    public ExposureId ExposureId { get; set; }

    public PartyId PayeePartyId { get; set; }

    public Guid PayeeAccountId { get; set; }

    public string? MaskedAccount { get; set; }

    public string Method { get; set; } = string.Empty;

    public string PaymentType { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? HoldReason { get; set; }

    public DisbursementId? DisbursementId { get; set; }

    public string DisbursementContentHash { get; set; } = string.Empty;

    public string? ApprovalEvidenceRef { get; set; }

    public Instant? SubmittedAt { get; set; }

    public Instant? IssuedAt { get; set; }

    public Instant? ClearedAt { get; set; }

    public Instant UpdatedAt { get; set; }
}

/// <summary>
/// <c>clm.set_approval</c>: one PLT approval request of a referred set, per (authority type, cost type) it needs
/// (D-SL2-13). The set executes only when every one is Approved and each decided authority dominates what CLM computes.
/// </summary>
internal sealed class SetApprovalRow : ClaimsRow
{
    public Guid ApprovalRequestId { get; set; }

    public ClaimTransactionSetId SetId { get; set; }

    public string ApprovalType { get; set; } = string.Empty;

    public string SubjectType { get; set; } = string.Empty;

    public string SubjectId { get; set; } = string.Empty;

    public string PayloadHash { get; set; } = string.Empty;

    public string AuthorityType { get; set; } = string.Empty;

    public string AuthorityCostType { get; set; } = string.Empty;

    public decimal AuthorityAmount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? Checker { get; set; }

    public Guid? CheckerUserId { get; set; }

    public Instant? DecidedAt { get; set; }
}

/// <summary><c>clm.payee_account_view</c>: CLM's masked read model of a BIL payee account (PRD-07 §7.1 PayeeAccountView).</summary>
internal sealed class PayeeAccountViewRow : ClaimsRow
{
    public Guid PayeeAccountId { get; set; }

    public ClaimId ClaimId { get; set; }

    public PartyId PartyId { get; set; }

    public string MaskedIban { get; set; } = string.Empty;

    public string VerificationStatus { get; set; } = string.Empty;

    public BusinessDate? CoolingOffUntil { get; set; }

    public bool IsChange { get; set; }

    public Instant UpdatedAt { get; set; }
}
