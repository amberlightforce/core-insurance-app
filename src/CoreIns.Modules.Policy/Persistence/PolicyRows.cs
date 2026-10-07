using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Policy.Persistence;

// Rows of the pol schema (PRD-05 §7.0/§7.1). Every row carries legal_entity_id. Jobs and quote versions are mutable
// working rows (record_version, optimistic concurrency). Policy facts are bitemporal: valid time [valid_from, valid_to)
// as instants (effective time, REQ-POL-041) and record time [recorded_from, recorded_to) (knownAt). Transactions and
// charge lines are append-only (no UPDATE grant, trigger rejects UPDATE/DELETE); a segment is never changed except to
// close its record period when it is superseded (REQ-POL-078).

/// <summary><c>pol.job</c>: a submission job (PRD-05 §7.1 Job).</summary>
internal sealed class JobRow
{
    public JobId JobId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    public JobNumber JobNumber { get; set; }

    public string JobType { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public bool Referred { get; set; }

    /// <summary>Policy id reserved at submission (ordering key of the job's events); the policy row exists once bound.</summary>
    public PolicyId PolicyId { get; set; }

    public PartyId PolicyholderPartyId { get; set; }

    public AccountId? AccountId { get; set; }

    public string ProductCode { get; set; } = string.Empty;

    public string ProductVersion { get; set; } = string.Empty;

    public string ArtefactHash { get; set; } = string.Empty;

    public string? RatingArtefactHash { get; set; }

    public string ResolutionHash { get; set; } = string.Empty;

    /// <summary>The PFC resolution manifest (jsonb).</summary>
    public string ResolutionManifest { get; set; } = "{}";

    public string Channel { get; set; } = string.Empty;

    public string? ProducerCode { get; set; }

    public string QuoteType { get; set; } = string.Empty;

    public Instant EffectiveAt { get; set; }

    public Instant ExpirationAt { get; set; }

    public string Currency { get; set; } = string.Empty;

    public int CurrentVersionNo { get; set; }

    public PolicyTransactionId? BoundTransactionId { get; set; }

    /// <summary>UW decline id when the job was declined (REQ-POL-156).</summary>
    public Guid? DeclineId { get; set; }

    public int RecordVersion { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant UpdatedAt { get; set; }
}

/// <summary><c>pol.quote_version</c>: one quote version of a job (PRD-05 §7.1 QuoteVersion, PRD-18 §9.2.1).</summary>
internal sealed class QuoteVersionRow
{
    public QuoteId QuoteId { get; set; }

    public JobId JobId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public int VersionNo { get; set; }

    public string State { get; set; } = string.Empty;

    /// <summary>Incremented on every committed edit (optimistic concurrency of pol.Job.updateDraft).</summary>
    public int DraftVersion { get; set; }

    /// <summary>The draft risk tree (jsonb, contract RiskTree).</summary>
    public string RiskTree { get; set; } = "{}";

    public string? WorksheetId { get; set; }

    public string? WorksheetHash { get; set; }

    public bool? Bindable { get; set; }

    /// <summary>Rounded charge lines of the rating (jsonb, contract ChargeLine[]).</summary>
    public string? Charges { get; set; }

    public decimal? Premium { get; set; }

    public decimal? Taxes { get; set; }

    public decimal? Total { get; set; }

    /// <summary>UW issues of the last evaluation (jsonb, contract UwIssue[]).</summary>
    public string? Issues { get; set; }

    public Guid? UwEvaluationId { get; set; }

    public string? ConfigurationHash { get; set; }

    public Instant? QuotedAt { get; set; }

    public Instant? ValidUntil { get; set; }

    public int RecordVersion { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }
}

/// <summary><c>pol.policy</c>: the policy (PRD-05 §7.1 Policy); created by the first bind.</summary>
internal sealed class PolicyRow
{
    public PolicyId PolicyId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    public PolicyNumber PolicyNumber { get; set; }

    public string ProductCode { get; set; } = string.Empty;

    public PartyId PolicyholderPartyId { get; set; }

    public AccountId? AccountId { get; set; }

    /// <summary>Transaction time the policy became known.</summary>
    public Instant RecordedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public int RecordVersion { get; set; }
}

/// <summary>
/// <c>pol.policy_term</c>: a term (PRD-05 §7.1 PolicyTerm), bitemporal: valid [valid_from, valid_to) is the term period,
/// recorded [recorded_from, recorded_to) is when this version was known. Pinned columns never change after bind.
/// </summary>
internal sealed class PolicyTermRow
{
    public Guid TermVersionId { get; set; }

    public PolicyTermId TermId { get; set; }

    public PolicyId PolicyId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public int TermNumber { get; set; }

    public Instant ValidFrom { get; set; }

    public Instant ValidTo { get; set; }

    public Instant RecordedFrom { get; set; }

    public Instant? RecordedTo { get; set; }

    /// <summary>State at bind (Scheduled or InForce); Scheduled → InForce is derived on read (REQ-POL-131).</summary>
    public string State { get; set; } = string.Empty;

    public string ProductVersion { get; set; } = string.Empty;

    public string ArtefactHash { get; set; } = string.Empty;

    public string? RatingArtefactHash { get; set; }

    public string ResolutionHash { get; set; } = string.Empty;

    public string ConfigurationHash { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    public string? ProducerCode { get; set; }

    public string PaymentPlanRef { get; set; } = string.Empty;

    public BusinessDate WrittenDate { get; set; }

    public PolicyTransactionId HeadTransactionId { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary><c>pol.policy_transaction</c>: an append-only bound transaction (PRD-05 §7.1 PolicyTransaction, REQ-POL-075).</summary>
internal sealed class PolicyTransactionRow
{
    public PolicyTransactionId TransactionId { get; set; }

    public PolicyId PolicyId { get; set; }

    public PolicyTermId TermId { get; set; }

    public JobId JobId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Kind { get; set; } = string.Empty;

    /// <summary>Gap-free sequence per policy.</summary>
    public int Sequence { get; set; }

    /// <summary>Valid time (validAt) the transaction takes effect.</summary>
    public Instant EffectiveAt { get; set; }

    /// <summary>Transaction time (knownAt) the transaction was recorded.</summary>
    public Instant RecordedAt { get; set; }

    public string ConfigurationHash { get; set; } = string.Empty;

    public string ArtefactHash { get; set; } = string.Empty;

    public string? RatingArtefactHash { get; set; }

    public string ResolutionHash { get; set; } = string.Empty;

    public string? WorksheetId { get; set; }

    /// <summary>The intent: the bound risk tree and quote reference (jsonb).</summary>
    public string Intent { get; set; } = "{}";

    public decimal Premium { get; set; }

    public decimal Taxes { get; set; }

    public decimal Total { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string Actor { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public string Origin { get; set; } = string.Empty;
}

/// <summary><c>pol.segment</c>: an immutable, bitemporally indexed snapshot of the risk tree (PRD-05 §7.1 Segment, REQ-POL-077/078/080).</summary>
internal sealed class SegmentRow
{
    public SegmentId SegmentId { get; set; }

    public PolicyTermId TermId { get; set; }

    public PolicyId PolicyId { get; set; }

    public PolicyTransactionId TransactionId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public Instant ValidFrom { get; set; }

    public Instant ValidTo { get; set; }

    public Instant RecordedFrom { get; set; }

    public Instant? RecordedTo { get; set; }

    /// <summary>SHA-256 of the RFC 8785 canonical snapshot.</summary>
    public string SnapshotHash { get; set; } = string.Empty;

    /// <summary>The snapshot (jsonb, contract RiskTree).</summary>
    public string Snapshot { get; set; } = "{}";

    public string? WorksheetId { get; set; }
}

/// <summary><c>pol.charge_line</c>: an append-only charge delta frozen on a transaction (PRD-05 §7.1 ChargeDelta, REQ-POL-119).</summary>
internal sealed class ChargeLineRow
{
    public ChargeId ChargeId { get; set; }

    public PolicyTransactionId TransactionId { get; set; }

    public PolicyTermId TermId { get; set; }

    public PolicyId PolicyId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string ElementLocator { get; set; } = string.Empty;

    public string CoverageCode { get; set; } = string.Empty;

    public string ChargeType { get; set; } = string.Empty;

    public string ChargeCategory { get; set; } = string.Empty;

    public string DeltaKind { get; set; } = string.Empty;

    public decimal AnnualRate { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public BusinessDate ValidFrom { get; set; }

    public BusinessDate ValidTo { get; set; }

    public BusinessDate BookingDate { get; set; }

    public string CorrelationKey { get; set; } = string.Empty;

    public int SetIndex { get; set; }

    public int SetSize { get; set; }

    public string? TaxTreatmentRef { get; set; }

    /// <summary>Legal status of the tax or levy value (D-SLC-11).</summary>
    public string? LegalStatus { get; set; }

    /// <summary>The tax or levy value was not Settled.</summary>
    public bool? Provisional { get; set; }

    public Instant RecordedAt { get; set; }
}
