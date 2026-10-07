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
}

/// <summary><c>pfc.artifact</c>: the canonical JSON of a compiled version, keyed by its SHA-256; never changed (REQ-PFC-197, -227).</summary>
internal sealed class ArtifactRow
{
    public string ArtefactHash { get; set; } = string.Empty;

    public string CanonicalJson { get; set; } = string.Empty;

    public int SizeBytes { get; set; }

    public Instant StoredAt { get; set; }
}
