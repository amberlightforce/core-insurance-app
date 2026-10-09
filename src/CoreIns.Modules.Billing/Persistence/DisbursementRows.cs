using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Billing.Persistence;

/// <summary>
/// A payee bank account (PaymentInstrument of type BANK_ACCOUNT, REQ-BIL-343, REQ-BIL-103). The IBAN is P2: only its
/// AES-256-GCM envelope (row-bound to <see cref="PayeeAccountId"/>), its keyed blind index and its last four characters
/// (the masked display) are stored. A change supersedes the previous account with a new valid period; the IBAN, party
/// and purpose of a row are frozen by a trigger.
/// </summary>
internal sealed class PayeeAccountRow
{
    public Guid PayeeAccountId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public PartyId PartyId { get; set; }

    public string Purpose { get; set; } = string.Empty;

    public byte[] IbanEncrypted { get; set; } = [];

    public string IbanBlindIndex { get; set; } = string.Empty;

    /// <summary>Last four characters of the IBAN (masked display, REQ-BIL-345).</summary>
    public string IbanLast4 { get; set; } = string.Empty;

    public string HolderName { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string? EvidenceRef { get; set; }

    public string VerificationStatus { get; set; } = string.Empty;

    public string? VopResult { get; set; }

    public string? VopSuggestedName { get; set; }

    public Instant? VopCheckedAt { get; set; }

    public BusinessDate ValidFrom { get; set; }

    public BusinessDate? ValidTo { get; set; }

    public BusinessDate CoolingOffUntil { get; set; }

    /// <summary>True when the account superseded another account of the party for the purpose (REQ-BIL-199).</summary>
    public bool IsChange { get; set; }

    public Guid? SupersedesId { get; set; }

    public string Status { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public int RecordVersion { get; set; }
}

/// <summary>
/// A disbursement of the shared disbursement service (REQ-BIL-009, REQ-BIL-197). Amount, currency, payee, source and
/// number are frozen by a trigger; only the state, its timestamps and the entry links move. No IBAN or name is stored
/// here: the payee account is a reference.
/// </summary>
internal sealed class DisbursementRow
{
    public DisbursementId DisbursementId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    public string DisbursementNumber { get; set; } = string.Empty;

    public string SourceModule { get; set; } = string.Empty;

    public string SourceType { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    public ClaimId? ClaimId { get; set; }

    public PartyId PayeePartyId { get; set; }

    public Guid PayeeAccountId { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public BusinessDate? RequestedValueDate { get; set; }

    public BusinessDate? ValueDate { get; set; }

    public string ApprovalEvidenceRef { get; set; } = string.Empty;

    public string ApprovalContentHash { get; set; } = string.Empty;

    public string? PurposeText { get; set; }

    /// <summary>Business reference of the paid object for sources without a claim (the credit set of a refund): part of the duplicate key (REQ-BIL-202).</summary>
    public string? BusinessRef { get; set; }

    public string? StatementRef { get; set; }

    public string? Lines { get; set; }

    public Guid? ReleaseApprovalRequestId { get; set; }

    public string? ReleaseApprovedBy { get; set; }

    public string State { get; set; } = string.Empty;

    public string ScreeningResult { get; set; } = string.Empty;

    public string? ScreeningListVersions { get; set; }

    public Instant ScreenedAt { get; set; }

    public string VopResult { get; set; } = string.Empty;

    public string? BankReference { get; set; }

    public Instant RequestedAt { get; set; }

    public Instant? ApprovedAt { get; set; }

    public Instant? ReleasedAt { get; set; }

    public Instant? IssuedAt { get; set; }

    public Instant? ClearedAt { get; set; }

    public Guid? ReleaseEntryId { get; set; }

    public Guid? ClearEntryId { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public int RecordVersion { get; set; }
}

