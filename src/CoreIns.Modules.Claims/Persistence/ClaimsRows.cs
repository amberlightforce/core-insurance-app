using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Claims.Persistence;

/// <summary>
/// Storage columns every CLM business row carries (PRD-07 §7.0, contract §3.2.1): legal entity, jurisdiction, creation
/// facts and the optimistic-concurrency version. The claim is not bitemporal; it stores the POL snapshot reference.
/// </summary>
internal abstract class ClaimsRow
{
    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public int RecordVersion { get; set; } = 1;
}

/// <summary><c>clm.claim</c>: the claim header (PRD-07 §7.1, REQ-CLM-061).</summary>
internal sealed class ClaimRow : ClaimsRow
{
    public decimal? FaultInsuredPct { get; set; }
    public string? FaultSource { get; set; }
    public PartyId? CounterpartyInsurerPartyId { get; set; }
    public bool? JointAccidentReport { get; set; }
    public int? VehicleCount { get; set; }
    public bool? AccidentInGreece { get; set; }

    public ClaimId ClaimId { get; set; }

    public ClaimNumber ClaimNumber { get; set; }

    public PolicyId PolicyId { get; set; }

    public PolicyNumber PolicyNumber { get; set; }

    public PartyId InsuredPartyId { get; set; }

    /// <summary>Opaque POL snapshot reference (REQ-CLM-002); never mutated except through re-verification adoption.</summary>
    public string SnapshotRef { get; set; } = string.Empty;

    public Guid? SnapshotSegmentId { get; set; }

    /// <summary>The policy term of the snapshot (null when not in force at the loss date); a key of the claim financial events.</summary>
    public PolicyTermId? PolicyTermId { get; set; }

    public Instant SnapshotValidAt { get; set; }

    public Instant SnapshotKnownAt { get; set; }

    public string SnapshotStatus { get; set; } = string.Empty;

    public bool PolicyInForceAtLoss { get; set; }

    public string? PolicyStatusAtLoss { get; set; }

    /// <summary>Display copy of the snapshot's selected coverage codes (PRD-07 §7.0 denormalised copy): coverage indications only, never decisions.</summary>
    public string[] SnapshotCoverageCodes { get; set; } = [];

    public string ProductCode { get; set; } = string.Empty;

    public string? ProductVersion { get; set; }

    public string LineOfBusiness { get; set; } = string.Empty;

    public Instant LossAt { get; set; }

    /// <summary>Business date of the loss in the legal entity's zone (duplicate check, events).</summary>
    public BusinessDate LossDate { get; set; }

    public BusinessDate NoticeOn { get; set; }

    public string LossCause { get; set; } = string.Empty;

    /// <summary>Loss location, P2 envelope bound to claim_id.</summary>
    public byte[] LossLocationEncrypted { get; set; } = [];

    /// <summary>Description, P2 envelope bound to claim_id.</summary>
    public byte[] DescriptionEncrypted { get; set; } = [];

    public string Channel { get; set; } = string.Empty;

    public string? ReceiptMedium { get; set; }

    public string HandlingSegment { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? SubStatus { get; set; }

    public string? Outcome { get; set; }

    public string? CloseReasonCode { get; set; }

    public bool CoverageInQuestion { get; set; }

    public ClaimId? DuplicateOfClaimId { get; set; }

    public string? DuplicateReasonCode { get; set; }

    /// <summary>Handler user (a field in the slice; WRK participants later, D-SL2-01).</summary>
    public string? Handler { get; set; }

    public int ReopenCount { get; set; }

    /// <summary>The CAT event code the claim is tagged to (BO-CLM-08); null = untagged.</summary>
    public string? CatEventCode { get; set; }

    /// <summary>Tracking milestone code (BO-CLM-08) and when it was set.</summary>
    public string? Milestone { get; set; }

    public Instant? MilestoneAt { get; set; }

    /// <summary>Last exposure sequence issued for this claim (exposure numbers = claim number + sequence, REQ-CLM-043).</summary>
    public int LastExposureSequence { get; set; }

    /// <summary>Last claim financial transaction sequence issued (transaction numbers per claim, D-SL2-07).</summary>
    public int LastTransactionSequence { get; set; }

    public Instant? ClosedAt { get; set; }

    public Instant UpdatedAt { get; set; }
}

/// <summary><c>clm.fnol_snapshot</c>: the FNOL exactly as submitted, immutable (REQ-CLM-044).</summary>
internal sealed class FnolSnapshotRow : ClaimsRow
{
    public Guid FnolId { get; set; }

    public ClaimId ClaimId { get; set; }

    public string Channel { get; set; } = string.Empty;

    public PartyId? ReporterPartyId { get; set; }

    /// <summary>The submitted request as JSON, P2 envelope bound to fnol_id.</summary>
    public byte[] PayloadEncrypted { get; set; } = [];

    public Instant SubmittedAt { get; set; }
}

/// <summary><c>clm.claimant</c>: a PTY party claiming on the claim (PRD-07 §7.1).</summary>
internal sealed class ClaimantRow : ClaimsRow
{
    public ClaimantId ClaimantId { get; set; }

    public ClaimId ClaimId { get; set; }

    public PartyId PartyId { get; set; }

    public string ClaimantType { get; set; } = string.Empty;
}

/// <summary><c>clm.incident</c>: facts of the loss (REQ-CLM-064; vehicle only in the slice, no P3 fields).</summary>
internal sealed class IncidentRow : ClaimsRow
{
    public Guid IncidentId { get; set; }

    public ClaimId ClaimId { get; set; }

    public string IncidentType { get; set; } = string.Empty;

    public string? VehicleRef { get; set; }

    public bool? Drivable { get; set; }

    public string[] DamageAreas { get; set; } = [];
}

/// <summary><c>clm.exposure</c>: one coverage × one claimant (REQ-CLM-062).</summary>
internal sealed class ExposureRow : ClaimsRow
{
    public string StatutoryClocks { get; set; } = "NOT_TRACKED";

    public ExposureId ExposureId { get; set; }

    public ClaimId ClaimId { get; set; }

    public ExposureNumber ExposureNumber { get; set; }

    public int Sequence { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string CoverageCode { get; set; } = string.Empty;

    public ClaimantId ClaimantId { get; set; }

    public Guid? IncidentId { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? SubStatus { get; set; }

    public string? Outcome { get; set; }

    public string CoverageIndication { get; set; } = string.Empty;

    public string CoverageDecision { get; set; } = string.Empty;

    /// <summary>Reason a second open exposure on the same coverage, claimant and incident was allowed (REQ-CLM-063).</summary>
    public string? DuplicateReason { get; set; }

    public Instant? ClosedAt { get; set; }

    public Instant UpdatedAt { get; set; }
}

/// <summary>
/// <c>clm.fnol_draft</c>: an incomplete FNOL saved for later (BO-CLM-02, D-SL2-11). Not a claim: no claim number, no exposure, no
/// clock, no event, never in claim lists. The payload is a P2 envelope bound to the draft id; only the creator reads it.
/// </summary>
internal sealed class FnolDraftRow : ClaimsRow
{
    public const string Draft = "DRAFT";
    public const string Submitted = "SUBMITTED";
    public const string Discarded = "DISCARDED";

    public Guid DraftId { get; set; }

    public string Status { get; set; } = Draft;

    /// <summary>The FNOL request so far as JSON, P2 envelope bound to draft_id.</summary>
    public byte[] PayloadEncrypted { get; set; } = [];

    /// <summary>The claim the draft became on submit.</summary>
    public ClaimId? ClaimId { get; set; }

    public Instant UpdatedAt { get; set; }
}

/// <summary><c>clm.cat_event</c>: the CAT event register (BO-CLM-08, REQ-CLM-210..215). Claims are tagged by code.</summary>
internal sealed class CatEventRow : ClaimsRow
{
    public const string Open = "OPEN";
    public const string Closed = "CLOSED";

    public Guid CatEventId { get; set; }

    public string CatCode { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string[] Perils { get; set; } = [];

    public string Region { get; set; } = string.Empty;

    public Instant WindowFrom { get; set; }

    /// <summary>Null = open-ended window.</summary>
    public Instant? WindowTo { get; set; }

    public string Status { get; set; } = Open;

    public string? CloseReason { get; set; }

    public Instant? ClosedAt { get; set; }

    public Instant UpdatedAt { get; set; }
}

/// <summary><c>clm.certificate_request</c>: a claims-history certificate (BO-CLM-07, REQ-CLM-008). The assembled claim lines are a snapshot (jsonb, no AFM, no free text).</summary>
internal sealed class CertificateRequestRow : ClaimsRow
{
    public const string Requested = "REQUESTED";
    public const string Assembled = "ASSEMBLED";
    public const string Issued = "ISSUED";

    public Guid CertificateId { get; set; }

    public PartyId RequesterPartyId { get; set; }

    /// <summary>Policy numbers asked for; empty = every policy of the requester.</summary>
    public string[] PolicyNumbers { get; set; } = [];

    public BusinessDate PeriodFrom { get; set; }

    public BusinessDate PeriodTo { get; set; }

    /// <summary>The look-back N applied when the request was made (from MKT, or the illustrative default).</summary>
    public int LookbackYears { get; set; }

    /// <summary>Legal status of the look-back value (the MKT status, or UNVERIFIED for the default).</summary>
    public string LegalStatus { get; set; } = "UNVERIFIED";

    public string Status { get; set; } = Requested;

    /// <summary>The assembled claim lines as JSON (an array).</summary>
    public string ClaimsJson { get; set; } = "[]";

    public string? CertificateNumber { get; set; }

    public Guid? DocumentId { get; set; }

    public Instant? IssuedAt { get; set; }

    public Instant UpdatedAt { get; set; }
}
