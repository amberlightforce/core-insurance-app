using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Compliance.Dsar;

/// <summary>Case statuses (SL6-CMP-DSAR).</summary>
internal static class DsarStatuses
{
    public const string Received = "RECEIVED";
    public const string IdentityVerified = "IDENTITY_VERIFIED";
    public const string InReview = "IN_REVIEW";
    public const string Completed = "COMPLETED";
    public const string Refused = "REFUSED";
}

/// <summary>Task statuses. NOT_BUILT blocks completion (D-SL6-07).</summary>
internal static class DsarTaskStatuses
{
    public const string Pending = "PENDING";
    public const string Done = "DONE";
    public const string Failed = "FAILED";
    public const string NotBuilt = "NOT_BUILT";
}

/// <summary>Phase a task last ran.</summary>
internal static class DsarPhases
{
    public const string Export = "EXPORT";
    public const string Restrict = "RESTRICT";
    public const string Erase = "ERASE";
}

/// <summary>Request types.</summary>
internal static class DsarRequestTypes
{
    public const string Access = "ACCESS";
    public const string Portability = "PORTABILITY";
    public const string Erasure = "ERASURE";
    public const string Restriction = "RESTRICTION";
    public const string Objection = "OBJECTION";
}

/// <summary>A data-subject request case (cmp.dsar).</summary>
internal sealed class DsarRow
{
    public DsarId DsarId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string RequestType { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public PartyId SubjectPartyId { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant ReceivedAt { get; set; }

    public Instant Deadline { get; set; }

    public bool Extended { get; set; }

    public bool IdentityVerified { get; set; }

    public string? IdentityMethod { get; set; }

    public string? VerifiedBy { get; set; }

    public string? ApprovedBy { get; set; }

    public string? ApprovalAction { get; set; }

    public string? ApprovalReason { get; set; }

    public bool LegalHold { get; set; }

    public string? RefusalReason { get; set; }

    public Instant? CompletedAt { get; set; }

    public string? Notes { get; set; }

    /// <summary>Compiled export package (JSON), once exported.</summary>
    public string? Package { get; set; }

    /// <summary>Per-item outcomes of the last erase or restrict (JSON).</summary>
    public string? ActionResult { get; set; }

    public int RecordVersion { get; set; }
}

/// <summary>One module's task in a case (cmp.dsar_task).</summary>
internal sealed class DsarTaskRow
{
    public Guid TaskId { get; set; }

    public DsarId DsarId { get; set; }

    public string Module { get; set; } = string.Empty;

    public bool ExportOnly { get; set; }

    public string Status { get; set; } = DsarTaskStatuses.Pending;

    public string Phase { get; set; } = DsarPhases.Export;

    /// <summary>Outcomes of the latest phase (JSON).</summary>
    public string Outcomes { get; set; } = "[]";

    public string? ExportPayload { get; set; }

    public bool RedactThirdParty { get; set; }

    public string? DecisionNote { get; set; }

    public int Attempts { get; set; }

    public string? Error { get; set; }

    public Instant? UpdatedAt { get; set; }
}

/// <summary>A disclosure of personal data to a recipient (cmp.disclosure_log).</summary>
internal sealed class DisclosureLogRow
{
    public Guid EntryId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public PartyId? SubjectPartyId { get; set; }

    public DsarId? DsarId { get; set; }

    public string Recipient { get; set; } = string.Empty;

    public string Purpose { get; set; } = string.Empty;

    public string LegalBasis { get; set; } = string.Empty;

    /// <summary>Data categories disclosed (JSON array).</summary>
    public string Categories { get; set; } = "[]";

    public string RecordedBy { get; set; } = string.Empty;

    public Instant RecordedAt { get; set; }
}

internal static class DsarMapping
{
    public static void MapDsar(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DsarRow>(entity =>
        {
            entity.ToTable("dsar", table =>
            {
                table.HasCheckConstraint("ck_dsar_status", "status IN ('RECEIVED','IDENTITY_VERIFIED','IN_REVIEW','COMPLETED','REFUSED')");
                table.HasCheckConstraint("ck_dsar_request_type", "request_type IN ('ACCESS','PORTABILITY','ERASURE','RESTRICTION','OBJECTION')");
                table.HasCheckConstraint("ck_dsar_record_version", "record_version >= 1");
            });
            entity.HasKey(e => e.DsarId).HasName("pk_dsar");
            entity.Property(e => e.DsarId).HasColumnName("dsar_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.RequestType).HasColumnName("request_type");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.SubjectPartyId).HasColumnName("subject_party_id");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.ReceivedAt).HasColumnName("received_at").HasColumnType("timestamptz");
            entity.Property(e => e.Deadline).HasColumnName("deadline").HasColumnType("timestamptz");
            entity.Property(e => e.Extended).HasColumnName("extended");
            entity.Property(e => e.IdentityVerified).HasColumnName("identity_verified");
            entity.Property(e => e.IdentityMethod).HasColumnName("identity_method");
            entity.Property(e => e.VerifiedBy).HasColumnName("verified_by");
            entity.Property(e => e.ApprovedBy).HasColumnName("approved_by");
            entity.Property(e => e.ApprovalAction).HasColumnName("approval_action");
            entity.Property(e => e.ApprovalReason).HasColumnName("approval_reason");
            entity.Property(e => e.LegalHold).HasColumnName("legal_hold");
            entity.Property(e => e.RefusalReason).HasColumnName("refusal_reason");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamptz");
            entity.Property(e => e.Notes).HasColumnName("notes");
            entity.Property(e => e.Package).HasColumnName("package").HasColumnType("jsonb");
            entity.Property(e => e.ActionResult).HasColumnName("action_result").HasColumnType("jsonb");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.HasIndex(e => new { e.LegalEntityId, e.SubjectPartyId }).HasDatabaseName("ix_dsar_subject");
        });

        modelBuilder.Entity<DsarTaskRow>(entity =>
        {
            entity.ToTable("dsar_task", table =>
            {
                table.HasCheckConstraint("ck_dsar_task_status", "status IN ('PENDING','DONE','FAILED','NOT_BUILT')");
                table.HasCheckConstraint("ck_dsar_task_phase", "phase IN ('EXPORT','RESTRICT','ERASE')");
            });
            entity.HasKey(e => e.TaskId).HasName("pk_dsar_task");
            entity.Property(e => e.TaskId).HasColumnName("task_id");
            entity.Property(e => e.DsarId).HasColumnName("dsar_id");
            entity.Property(e => e.Module).HasColumnName("module");
            entity.Property(e => e.ExportOnly).HasColumnName("export_only");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.Phase).HasColumnName("phase");
            entity.Property(e => e.Outcomes).HasColumnName("outcomes").HasColumnType("jsonb");
            entity.Property(e => e.ExportPayload).HasColumnName("export_payload").HasColumnType("jsonb");
            entity.Property(e => e.RedactThirdParty).HasColumnName("redact_third_party");
            entity.Property(e => e.DecisionNote).HasColumnName("decision_note");
            entity.Property(e => e.Attempts).HasColumnName("attempts");
            entity.Property(e => e.Error).HasColumnName("error");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.DsarId, e.Module }).IsUnique().HasDatabaseName("ux_dsar_task_module");
            entity.HasOne<DsarRow>().WithMany().HasForeignKey(e => e.DsarId).HasConstraintName("fk_dsar_task_dsar");
        });

        modelBuilder.Entity<DisclosureLogRow>(entity =>
        {
            entity.ToTable("disclosure_log");
            entity.HasKey(e => e.EntryId).HasName("pk_disclosure_log");
            entity.Property(e => e.EntryId).HasColumnName("entry_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.SubjectPartyId).HasColumnName("subject_party_id");
            entity.Property(e => e.DsarId).HasColumnName("dsar_id");
            entity.Property(e => e.Recipient).HasColumnName("recipient");
            entity.Property(e => e.Purpose).HasColumnName("purpose");
            entity.Property(e => e.LegalBasis).HasColumnName("legal_basis");
            entity.Property(e => e.Categories).HasColumnName("categories").HasColumnType("jsonb");
            entity.Property(e => e.RecordedBy).HasColumnName("recorded_by");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.LegalEntityId, e.SubjectPartyId }).HasDatabaseName("ix_disclosure_log_subject");
        });
    }
}
