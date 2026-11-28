using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Compliance.Submissions;

/// <summary>Submission states: PLANNED, IN_PREPARATION, READY (approval requested or granted), SUBMITTED, ACKNOWLEDGED, REJECTED, RESUBMITTED (replaced by a newer version).</summary>
internal static class SubmissionStatuses
{
    public const string Planned = "PLANNED";
    public const string InPreparation = "IN_PREPARATION";
    public const string Ready = "READY";
    public const string Submitted = "SUBMITTED";
    public const string Acknowledged = "ACKNOWLEDGED";
    public const string Rejected = "REJECTED";
    public const string Resubmitted = "RESUBMITTED";

    /// <summary>The content (evidence links) can still change.</summary>
    public static bool IsEditable(string status) => status is Planned or InPreparation;

    /// <summary>Not yet filed: an overdue due date counts.</summary>
    public static bool IsOpen(string status) => status is Planned or InPreparation or Ready;
}

/// <summary>Where the maker-checker stands.</summary>
internal static class SubmissionApprovalStatuses
{
    public const string None = "NONE";
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
}

/// <summary>A regulatory submission (cmp.submission). From SUBMITTED on only the response fields and the status change; a resubmission is a new row (next version) that links back.</summary>
internal sealed class SubmissionRow
{
    public Guid SubmissionId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Number { get; set; } = string.Empty;

    public int Version { get; set; } = 1;

    public Guid? PreviousVersionId { get; set; }

    public string Status { get; set; } = SubmissionStatuses.Planned;

    public string SubmissionType { get; set; } = string.Empty;

    public string Regulator { get; set; } = string.Empty;

    public string PeriodLabel { get; set; } = string.Empty;

    public BusinessDate? PeriodFrom { get; set; }

    public BusinessDate? PeriodTo { get; set; }

    public BusinessDate? DueDate { get; set; }

    /// <summary>Verified or Unverified (D-USR-19).</summary>
    public string DueLegalStatus { get; set; } = "Unverified";

    /// <summary>Linked artefacts (JSON array of kind, refId, hash, label, addedBy, addedAt).</summary>
    public string Evidence { get; set; } = "[]";

    /// <summary>Everyone who prepared or changed the content (JSON array of actor keys); none of them may approve.</summary>
    public string Participants { get; set; } = "[]";

    public string ApprovalStatus { get; set; } = SubmissionApprovalStatuses.None;

    public Guid? ApprovalRequestId { get; set; }

    /// <summary>Hash of the content frozen when approval was requested.</summary>
    public string? ContentHash { get; set; }

    public string PreparedBy { get; set; } = string.Empty;

    public string? RequestedBy { get; set; }

    public string? ApprovedBy { get; set; }

    public string? FilingChannel { get; set; }

    public Instant? FiledAt { get; set; }

    public string? Filer { get; set; }

    public string? FilingReference { get; set; }

    public string? ReceiptHash { get; set; }

    public string? ResponseOutcome { get; set; }

    public string? ResponseReference { get; set; }

    public string? ResponseNote { get; set; }

    public Instant? RespondedAt { get; set; }

    public string? Note { get; set; }

    public Instant CreatedAt { get; set; }

    public int RecordVersion { get; set; }
}

/// <summary>An append-only history line: a status transition or a comment (cmp.submission_history).</summary>
internal sealed class SubmissionHistoryRow
{
    public Guid HistoryId { get; set; }

    public Guid SubmissionId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string? FromStatus { get; set; }

    public string? ToStatus { get; set; }

    public string Actor { get; set; } = string.Empty;

    public Instant At { get; set; }

    public string? Text { get; set; }
}

internal static class SubmissionMapping
{
    public static void MapSubmissions(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SubmissionRow>(entity =>
        {
            entity.ToTable("submission", table =>
            {
                table.HasCheckConstraint("ck_submission_status", "status IN ('PLANNED','IN_PREPARATION','READY','SUBMITTED','ACKNOWLEDGED','REJECTED','RESUBMITTED')");
                table.HasCheckConstraint("ck_submission_approval_status", "approval_status IN ('NONE','PENDING','APPROVED')");
                table.HasCheckConstraint("ck_submission_due_legal_status", "due_legal_status IN ('Verified','Unverified')");
                table.HasCheckConstraint("ck_submission_version", "version >= 1 AND record_version >= 1");
            });
            entity.HasKey(e => e.SubmissionId).HasName("pk_submission");
            entity.Property(e => e.SubmissionId).HasColumnName("submission_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Number).HasColumnName("number");
            entity.Property(e => e.Version).HasColumnName("version");
            entity.Property(e => e.PreviousVersionId).HasColumnName("previous_version_id");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.SubmissionType).HasColumnName("submission_type");
            entity.Property(e => e.Regulator).HasColumnName("regulator");
            entity.Property(e => e.PeriodLabel).HasColumnName("period_label");
            entity.Property(e => e.PeriodFrom).HasColumnName("period_from");
            entity.Property(e => e.PeriodTo).HasColumnName("period_to");
            entity.Property(e => e.DueDate).HasColumnName("due_date");
            entity.Property(e => e.DueLegalStatus).HasColumnName("due_legal_status");
            entity.Property(e => e.Evidence).HasColumnName("evidence").HasColumnType("jsonb");
            entity.Property(e => e.Participants).HasColumnName("participants").HasColumnType("jsonb");
            entity.Property(e => e.ApprovalStatus).HasColumnName("approval_status");
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.ContentHash).HasColumnName("content_hash");
            entity.Property(e => e.PreparedBy).HasColumnName("prepared_by");
            entity.Property(e => e.RequestedBy).HasColumnName("requested_by");
            entity.Property(e => e.ApprovedBy).HasColumnName("approved_by");
            entity.Property(e => e.FilingChannel).HasColumnName("filing_channel");
            entity.Property(e => e.FiledAt).HasColumnName("filed_at").HasColumnType("timestamptz");
            entity.Property(e => e.Filer).HasColumnName("filer");
            entity.Property(e => e.FilingReference).HasColumnName("filing_reference");
            entity.Property(e => e.ReceiptHash).HasColumnName("receipt_hash");
            entity.Property(e => e.ResponseOutcome).HasColumnName("response_outcome");
            entity.Property(e => e.ResponseReference).HasColumnName("response_reference");
            entity.Property(e => e.ResponseNote).HasColumnName("response_note");
            entity.Property(e => e.RespondedAt).HasColumnName("responded_at").HasColumnType("timestamptz");
            entity.Property(e => e.Note).HasColumnName("note");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.HasIndex(e => new { e.LegalEntityId, e.Number }).IsUnique().HasDatabaseName("ux_submission_number");
            entity.HasIndex(e => new { e.LegalEntityId, e.Status }).HasDatabaseName("ix_submission_status");
            entity.HasIndex(e => e.PreviousVersionId).IsUnique().HasDatabaseName("ux_submission_previous_version");
        });

        modelBuilder.Entity<SubmissionHistoryRow>(entity =>
        {
            entity.ToTable("submission_history", table => table.HasCheckConstraint("ck_submission_history_kind", "kind IN ('TRANSITION','COMMENT')"));
            entity.HasKey(e => e.HistoryId).HasName("pk_submission_history");
            entity.Property(e => e.HistoryId).HasColumnName("history_id");
            entity.Property(e => e.SubmissionId).HasColumnName("submission_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.FromStatus).HasColumnName("from_status");
            entity.Property(e => e.ToStatus).HasColumnName("to_status");
            entity.Property(e => e.Actor).HasColumnName("actor");
            entity.Property(e => e.At).HasColumnName("at").HasColumnType("timestamptz");
            entity.Property(e => e.Text).HasColumnName("text");
            entity.HasIndex(e => new { e.SubmissionId, e.At }).HasDatabaseName("ix_submission_history_submission");
        });
    }
}
