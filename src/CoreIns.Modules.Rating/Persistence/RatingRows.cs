using CoreIns.SharedKernel;

namespace CoreIns.Modules.Rating.Persistence;

// Rows of the rat schema (PRD-03 §7.1, SL-RAT-UW subset). Tables, artefacts and worksheets are content-addressed and
// immutable: the key is the SHA-256 of the canonical content, an identical write is a no-op, nothing is updated or deleted.

/// <summary><c>rat.rate_table_version</c>: a rate table as a decision-table definition. <see cref="TableHash"/> is the engine's content hash.</summary>
internal sealed class RateTableVersionRow
{
    public string TableHash { get; set; } = string.Empty;

    public string TableCode { get; set; } = string.Empty;

    public string VersionNo { get; set; } = string.Empty;

    public string HitPolicy { get; set; } = string.Empty;

    /// <summary>ILLUSTRATIVE_TEST_DATA or APPROVED (D-SLC-04).</summary>
    public string DataStatus { get; set; } = string.Empty;

    public string Definition { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary><c>rat.rating_artifact</c>: the manifest that pins table hashes, steps and tax plan to a product version.</summary>
internal sealed class RatingArtifactRow
{
    public string ArtefactHash { get; set; } = string.Empty;

    public string ArtefactCode { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string ProductCode { get; set; } = string.Empty;

    public string ProductVersion { get; set; } = string.Empty;

    public string EngineVersion { get; set; } = string.Empty;

    public string DataStatus { get; set; } = string.Empty;

    public string Definition { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary><c>rat.rate_activation</c>: which artefact is live for a product version from a date (REQ-RAT-064).</summary>
internal sealed class RateActivationRow
{
    public Guid ActivationId { get; set; }

    public string ArtefactHash { get; set; } = string.Empty;

    public string ProductCode { get; set; } = string.Empty;

    public string ProductVersion { get; set; } = string.Empty;

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public string Status { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary><c>rat.worksheet</c>: the canonical worksheet; <see cref="WorksheetId"/> is the SHA-256 of its canonical JSON (D-API-12).</summary>
internal sealed class WorksheetRow
{
    public string WorksheetId { get; set; } = string.Empty;

    public Guid LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    public string ArtefactHash { get; set; } = string.Empty;

    public string ConfigurationHash { get; set; } = string.Empty;

    public string InputHash { get; set; } = string.Empty;

    public string EngineVersion { get; set; } = string.Empty;

    public string DataStatus { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary><c>rat.worksheet_index</c>: which quote, job or transaction a worksheet was produced for (retention state QUOTE / ATTACHED / DRY_RUN).</summary>
internal sealed class WorksheetIndexRow
{
    public long IndexId { get; set; }

    public string WorksheetId { get; set; } = string.Empty;

    public Guid LegalEntityId { get; set; }

    public Guid? QuoteId { get; set; }

    public Guid? JobId { get; set; }

    public Guid? TransactionId { get; set; }

    public string Mode { get; set; } = string.Empty;

    public string RetentionState { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }
}
