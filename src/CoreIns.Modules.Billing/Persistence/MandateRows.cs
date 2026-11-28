using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Billing.Persistence;

/// <summary>
/// A payer's payment instrument (BO-BIL-06a, REQ-BIL-103): a bank account whose IBAN is P2, so only its AES-256-GCM envelope
/// (row-bound to <see cref="InstrumentId"/>), keyed blind index and last four characters are stored. Removal is a state change.
/// </summary>
internal sealed class PaymentInstrumentRow
{
    public Guid InstrumentId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public PartyId PartyId { get; set; }

    public string Kind { get; set; } = "BANK_ACCOUNT";

    public byte[] IbanEncrypted { get; set; } = [];

    public string IbanBlindIndex { get; set; } = string.Empty;

    public string IbanLast4 { get; set; } = string.Empty;

    public string HolderName { get; set; } = string.Empty;

    public string? VopResult { get; set; }

    public string Status { get; set; } = "ACTIVE";

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant? RemovedAt { get; set; }

    public int RecordVersion { get; set; }
}

/// <summary>
/// A SEPA Core mandate (BO-BIL-06a, REQ-BIL-106..108): DRAFT until signed with a consent evidence id, then ACTIVE with sequence type
/// FRST (RCUR once a collection used it, BO-BIL-06b); an amendment moves it to another instrument and returns it to FRST; CANCELLED is
/// final. A migrated mandate (origin MIGRATION) shares the id of its <c>migration_mandate</c> row and carries the masked IBAN only.
/// </summary>
internal sealed class MandateRow
{
    public Guid MandateId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public BillingAccountId BillingAccountId { get; set; }

    public PartyId PayerPartyId { get; set; }

    public Guid? InstrumentId { get; set; }

    /// <summary>SEPA unique mandate reference (UMR), at most 35 characters.</summary>
    public string MandateReference { get; set; } = string.Empty;

    public string Scheme { get; set; } = "SEPA_CORE";

    public string SequenceType { get; set; } = "FRST";

    public string Status { get; set; } = "DRAFT";

    public string? IbanMasked { get; set; }

    public BusinessDate? SignatureDate { get; set; }

    public string? SignatureEvidenceId { get; set; }

    public int AmendmentCount { get; set; }

    public string Origin { get; set; } = "NATIVE";

    public Instant? FirstUsedAt { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant? CancelledAt { get; set; }

    public string? CancelReason { get; set; }

    public int RecordVersion { get; set; }
}
