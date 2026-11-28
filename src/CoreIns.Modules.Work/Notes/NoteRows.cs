using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Work.Notes;

/// <summary><c>wrk.note</c>: one free-text note linked to a business object (module, type, id). The body here is the latest version.</summary>
internal sealed class NoteRow
{
    /// <summary>Everyone who may read notes.</summary>
    public const string Staff = "STAFF";

    /// <summary>The author, managers and the platform admin.</summary>
    public const string Managers = "MANAGERS";

    /// <summary>The author only.</summary>
    public const string Private = "PRIVATE";

    public Guid NoteId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string ObjectModule { get; set; } = string.Empty;

    public string ObjectType { get; set; } = string.Empty;

    public string ObjectId { get; set; } = string.Empty;

    public string Visibility { get; set; } = Staff;

    public string Body { get; set; } = string.Empty;

    /// <summary>Search key (Greek folding of the language rules) of the latest body.</summary>
    public string SearchKey { get; set; } = string.Empty;

    /// <summary>Number of the latest <see cref="NoteVersionRow"/>.</summary>
    public int CurrentVersion { get; set; } = 1;

    public string CreatedBy { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }

    public string? DeletedBy { get; set; }

    public Instant? DeletedAt { get; set; }

    public string? DeleteReason { get; set; }

    public int RecordVersion { get; set; } = 1;
}

/// <summary><c>wrk.note_version</c>: append-only text history; every edit adds a row, nothing is overwritten.</summary>
internal sealed class NoteVersionRow
{
    public Guid NoteId { get; set; }

    public int Version { get; set; }

    public string Body { get; set; } = string.Empty;

    public string Visibility { get; set; } = NoteRow.Staff;

    public string EditedBy { get; set; } = string.Empty;

    public Instant EditedAt { get; set; }
}
