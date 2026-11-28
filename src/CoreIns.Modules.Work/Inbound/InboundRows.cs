using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Work.Inbound;

/// <summary>
/// <c>wrk.inbound_document</c> (BO-WRK-11): one received file. The bytes live in the DOC archive (<see cref="ArchiveId"/>); this row holds the
/// intake facts, the malware verdict and the manual keying. A file is QUARANTINED until a scanner says clean; an infected file is never stored.
/// </summary>
internal sealed class InboundDocumentRow
{
    /// <summary>Received, not yet (successfully) scanned. Nothing but reject and reprocess is allowed.</summary>
    public const string Quarantined = "QUARANTINED";

    /// <summary>The scanner said clean.</summary>
    public const string Clean = "CLEAN";

    /// <summary>The scanner found malware; terminal apart from reject. The bytes were not stored.</summary>
    public const string Infected = "INFECTED";

    /// <summary>A clerk keyed the class and data by hand (D-USR-03: no OCR).</summary>
    public const string Verified = "VERIFIED";

    /// <summary>Refused by a clerk.</summary>
    public const string Rejected = "REJECTED";

    /// <summary>Divided into child documents.</summary>
    public const string Split = "SPLIT";

    public const string VerdictClean = "CLEAN";
    public const string VerdictInfected = "INFECTED";
    public const string VerdictUnavailable = "UNAVAILABLE";

    public Guid InboundDocumentId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public Guid EnvelopeId { get; set; }

    public string InboundNumber { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    /// <summary>The DOC archive entry of the original bytes; null for an infected file.</summary>
    public Guid? ArchiveId { get; set; }

    public string Channel { get; set; } = string.Empty;

    /// <summary>JSON of the sender as given at intake.</summary>
    public string Sender { get; set; } = "{}";

    /// <summary>JSON of the envelope metadata as given at intake.</summary>
    public string EnvelopeMetadata { get; set; } = "{}";

    public string? SuggestedClass { get; set; }

    /// <summary>The document class a clerk keyed at verify.</summary>
    public string? DocumentClass { get; set; }

    /// <summary>Linked business object as <c>MOD/Type/id</c>.</summary>
    public string? LinkObject { get; set; }

    /// <summary>Manually keyed data (JSON object).</summary>
    public string KeyedData { get; set; } = "{}";

    public string State { get; set; } = Quarantined;

    public string? ScanVerdict { get; set; }

    public string? ScanSignature { get; set; }

    public string? Scanner { get; set; }

    public Instant? ScannedAt { get; set; }

    public Guid? ParentId { get; set; }

    public string? PageRange { get; set; }

    public string? RejectReason { get; set; }

    /// <summary>The redacted copy; the original (<see cref="ArchiveId"/>) is never replaced.</summary>
    public Guid? RedactedArchiveId { get; set; }

    public string? RedactionReason { get; set; }

    /// <summary>True once a redaction exists: the original stays on hold and is not deleted by retention.</summary>
    public bool LegalHold { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }

    public string? VerifiedBy { get; set; }

    public Instant? VerifiedAt { get; set; }

    public int RecordVersion { get; set; } = 1;
}
