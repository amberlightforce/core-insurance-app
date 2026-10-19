using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Reinsurance.Persistence;

/// <summary>
/// <c>ri.contract</c>: the contract header (PRD-08 §3 RIContract, REQ-RI-030..032, -056..058, -231). Identity columns are
/// frozen by a trigger; the lifecycle status and the maker-checker facts move. The header is the aggregate root: every
/// state-changing command locks it (<c>FOR UPDATE</c>) and checks <see cref="RecordVersion"/> (PITFALLS 15).
/// </summary>
internal sealed class ContractRow
{
    public RiContractId ContractId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    /// <summary>Gapless <c>RI_CONTRACT</c> number per legal entity (REQ-RI-030).</summary>
    public string ContractNumber { get; set; } = string.Empty;

    /// <summary>Stable treaty id across contract years (REQ-RI-231); the first contract's number.</summary>
    public string StableTreatyId { get; set; } = string.Empty;

    public string ContractType { get; set; } = string.Empty;

    public int ContractYear { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Everyone who took part in the content, as actor keys (<c>KIND:id</c>, the principal of a delegated actor too):
    /// creator and every editor. None of them may decide the approval (PITFALLS 5).
    /// </summary>
    public string[] Participants { get; set; } = [];

    public string? SubmittedBy { get; set; }

    public Instant? SubmittedAt { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    public string? DecidedBy { get; set; }

    public Instant? DecidedAt { get; set; }

    public string? ReturnReason { get; set; }

    public Instant? ActivatedAt { get; set; }

    public Instant? ExpiredAt { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant UpdatedAt { get; set; }

    public int RecordVersion { get; set; } = 1;
}

/// <summary>
/// <c>ri.contract_version</c>: the one version of slice 4 (PRD-08 §3 RIContractVersion). Valid period (Athens business
/// dates, half-open) and record period; once <see cref="ApprovedAt"/> is set a trigger refuses every change.
/// </summary>
internal sealed class ContractVersionRow
{
    public Guid VersionId { get; set; }

    public RiContractId ContractId { get; set; }

    public int VersionNo { get; set; } = 1;

    public BusinessDate ValidFrom { get; set; }

    public BusinessDate ValidTo { get; set; }

    public Instant KnownFrom { get; set; }

    public Instant? KnownTo { get; set; }

    /// <summary>Revision of the child rows (section, layers, clause, participations) that are current; older ones stay as history.</summary>
    public int ContentRev { get; set; } = 1;

    public decimal PlacedPct { get; set; }

    /// <summary>SHA-256 of the canonical content (the value the PLT approval is bound to, REQ-RI-057).</summary>
    public string ContentHash { get; set; } = string.Empty;

    public Guid? ApprovalRequestId { get; set; }

    public Instant? ApprovedAt { get; set; }

    public string? ApprovedBy { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary><c>ri.section</c>: the section with its scope (PRD-08 §3 RISection). One section per version in slice 4.</summary>
internal sealed class SectionRow
{
    public Guid SectionId { get; set; }

    public Guid VersionId { get; set; }

    public int Rev { get; set; }

    public int SectionNo { get; set; }

    public string[] ProductCodes { get; set; } = [];

    public string[] CoverageCodes { get; set; } = [];
}

/// <summary><c>ri.layer</c>: attachment, limit, AAD, AAL in NUMERIC (PRD-08 §3 RILayer, REQ-RI-037).</summary>
internal sealed class LayerRow
{
    public Guid LayerId { get; set; }

    public Guid SectionId { get; set; }

    public Guid VersionId { get; set; }

    public int Rev { get; set; }

    public int LayerNo { get; set; }

    public decimal Attachment { get; set; }

    public decimal LimitAmount { get; set; }

    public decimal Aad { get; set; }

    public decimal? Aal { get; set; }

    public string Currency { get; set; } = string.Empty;
}

/// <summary><c>ri.clause</c>: ALAE, interest and inuring rules (PRD-08 §3 ClauseSet, REQ-RI-117).</summary>
internal sealed class ClauseRow
{
    public Guid ClauseId { get; set; }

    public Guid VersionId { get; set; }

    public int Rev { get; set; }

    public bool AlaeIncluded { get; set; }

    public bool StatutoryInterestIncluded { get; set; }

    public string RecoveriesInure { get; set; } = string.Empty;
}

/// <summary>
/// <c>ri.participation</c>: a reinsurer's signed line (PRD-08 §3 RIParticipation, REQ-RI-046/047). The panel applies to
/// every layer of the section; party ids only, no names (PTY owns the personal data).
/// </summary>
internal sealed class ParticipationRow
{
    public Guid ParticipationId { get; set; }

    public Guid VersionId { get; set; }

    public int Rev { get; set; }

    public PartyId ReinsurerPartyId { get; set; }

    public Guid? BrokerPartyId { get; set; }

    public decimal SignedLinePct { get; set; }

    public bool Lead { get; set; }
}
