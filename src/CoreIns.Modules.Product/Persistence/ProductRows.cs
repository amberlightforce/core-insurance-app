using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Product.Persistence;

// Rows of the pfc schema (PRD-02 §3.1, slice subset). Every row carries legal_entity_id, jurisdiction and audit columns
// (PRD-02 §3). The compiled artefact is content-addressed and write-once.

/// <summary><c>pfc.product</c>: a product of a legal entity; its code is immutable (REQ-PFC-031).</summary>
internal sealed class ProductRow
{
    public ProductId ProductId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string LineCode { get; set; } = string.Empty;

    public string LineFamily { get; set; } = string.Empty;

    public string NameEl { get; set; } = string.Empty;

    public string NameEn { get; set; } = string.Empty;

    public string ProductType { get; set; } = string.Empty;

    public string CustomerType { get; set; } = string.Empty;

    public int RecordVersion { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary><c>pfc.product_version</c>: one version of a product with its windows and artefact hash (REQ-PFC-032, -166).</summary>
internal sealed class ProductVersionRow
{
    public ProductVersionId ProductVersionId { get; set; }

    public ProductId ProductId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    public int Major { get; set; }

    public int Minor { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? LifecycleSubstate { get; set; }

    public bool IsAbstract { get; set; }

    public string[] Channels { get; set; } = [];

    public string ContractCurrency { get; set; } = string.Empty;

    public BusinessDate NewBusinessFrom { get; set; }

    public BusinessDate? NewBusinessTo { get; set; }

    public BusinessDate RenewalFrom { get; set; }

    public BusinessDate? RenewalTo { get; set; }

    public string ArtefactHash { get; set; } = string.Empty;

    public int SchemaVersion { get; set; }

    public int RecordVersion { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant? LockedAt { get; set; }

    /// <summary>The earlier version whose content this fall-back version copies (REQ-PFC-213); null for an ordinary version. Write-once.</summary>
    public ProductVersionId? FallbackOfVersionId { get; set; }

    /// <summary>The defective version this fall-back version replaces for new business (REQ-PFC-213); null for an ordinary version. Write-once.</summary>
    public ProductVersionId? ReplacesVersionId { get; set; }
}

/// <summary>
/// <c>pfc.fallback_request</c>: one emergency fall-back (REQ-PFC-213, D-SL5-09) from request to decision. Everything the
/// decision executes (source, new version number, date, reason) is derived by the server at request time and bound into
/// <see cref="PayloadHash"/>, the content hash of the in-process PLT approval (type PFC.Fallback).
/// </summary>
internal sealed class FallbackRequestRow
{
    public Guid FallbackId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    public ProductId ProductId { get; set; }

    public ProductVersionId DefectiveVersionId { get; set; }

    public ProductVersionId SourceVersionId { get; set; }

    public int NewMajor { get; set; }

    public int NewMinor { get; set; }

    /// <summary>The Athens business date the new version's new-business window starts and the defective version's ends.</summary>
    public BusinessDate FallbackDate { get; set; }

    public string Status { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string PayloadHash { get; set; } = string.Empty;

    public Guid ApprovalRequestId { get; set; }

    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>The maker's principal when the maker is a service or AI agent acting for a person (PITFALLS 5).</summary>
    public string? RequestedByPrincipal { get; set; }

    public Instant RequestedAt { get; set; }

    public string? DecidedBy { get; set; }

    public Instant? DecidedAt { get; set; }

    public string? DecisionReason { get; set; }

    public ProductVersionId? NewVersionId { get; set; }

    public int RecordVersion { get; set; }
}

/// <summary><c>pfc.artifact</c>: the canonical JSON of a compiled version, keyed by its SHA-256; never changed (REQ-PFC-197, -227).</summary>
internal sealed class ArtifactRow
{
    public string ArtefactHash { get; set; } = string.Empty;

    public string CanonicalJson { get; set; } = string.Empty;

    public int SizeBytes { get; set; }

    public Instant StoredAt { get; set; }
}

/// <summary>
/// <c>pfc.change_set</c>: an editable draft of a product version (BO-PFC-01, REQ-PFC-182..188). The base version stays Locked and
/// untouched; the draft is a copy of its artefact as JSON, changed only through the edit command.
/// </summary>
internal sealed class ChangeSetRow
{
    public Guid ChangeSetId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    public ProductId ProductId { get; set; }

    public ProductVersionId BaseVersionId { get; set; }

    public string Status { get; set; } = string.Empty;

    public string DraftJson { get; set; } = string.Empty;

    public int RecordVersion { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary><c>pfc.change_set_edit</c>: the append-only operation log of a change set (one row per patch operation).</summary>
internal sealed class ChangeSetEditRow
{
    public Guid EditId { get; set; }

    public Guid ChangeSetId { get; set; }

    public int Seq { get; set; }

    public string Op { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    /// <summary>JSON of the value before the operation; null when the path did not exist.</summary>
    public string? OldJson { get; set; }

    /// <summary>JSON of the value after the operation; null for a removal.</summary>
    public string? NewJson { get; set; }

    public string Actor { get; set; } = string.Empty;

    public Instant At { get; set; }
}

/// <summary>
/// <c>pfc.release_request</c>: one pass of a change set through submit, sign-off and schedule (BO-PFC-04). Everything the approval
/// binds (identity, version number, candidate artefact hash) is derived by the server at submit and bound into
/// <see cref="PayloadHash"/>, the content hash of the in-process PLT approval (type PFC.Release). The version row is created and
/// Locked only at the activation date, so a Locked version stays immutable and a schedule can be cancelled.
/// </summary>
internal sealed class ReleaseRequestRow
{
    public Guid ReleaseId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    public ProductId ProductId { get; set; }

    public Guid ChangeSetId { get; set; }

    public int NewMajor { get; set; }

    public int NewMinor { get; set; }

    public string Status { get; set; } = string.Empty;

    public string PayloadHash { get; set; } = string.Empty;

    /// <summary>The hash the compiled draft has as submitted (before the schedule sets the windows).</summary>
    public string CandidateArtefactHash { get; set; } = string.Empty;

    public Guid ApprovalRequestId { get; set; }

    public string SubmittedBy { get; set; } = string.Empty;

    /// <summary>The submitter's principal when the submitter is a service or AI agent acting for a person.</summary>
    public string? SubmittedByPrincipal { get; set; }

    public Instant SubmittedAt { get; set; }

    public string? DecidedBy { get; set; }

    public Instant? DecidedAt { get; set; }

    public string? DecisionComment { get; set; }

    public BusinessDate? ActivationDate { get; set; }

    public BusinessDate? NewBusinessFrom { get; set; }

    public BusinessDate? NewBusinessTo { get; set; }

    public BusinessDate? RenewalFrom { get; set; }

    public BusinessDate? RenewalTo { get; set; }

    public string? ScheduledBy { get; set; }

    public Instant? ScheduledAt { get; set; }

    public string? ScheduleReason { get; set; }

    public ProductVersionId? PublishedVersionId { get; set; }

    public Instant? PublishedAt { get; set; }

    public int RecordVersion { get; set; }
}
