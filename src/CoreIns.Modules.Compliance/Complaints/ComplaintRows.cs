using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Compliance.Complaints;

/// <summary>Complaint states (PRD-11 section 7.3.4): Received, Acknowledged, UnderInvestigation, Answered, Escalated, Closed.</summary>
internal static class ComplaintStatuses
{
    public const string Received = "RECEIVED";
    public const string Acknowledged = "ACKNOWLEDGED";
    public const string UnderInvestigation = "UNDER_INVESTIGATION";
    public const string Answered = "ANSWERED";
    public const string Escalated = "ESCALATED";
    public const string Closed = "CLOSED";
}

/// <summary>Where the reply stands: a draft needs a second user's approval before the complaint is Answered.</summary>
internal static class ComplaintReplyStatuses
{
    public const string None = "NONE";
    public const string PendingApproval = "PENDING_APPROVAL";
    public const string Returned = "RETURNED";
    public const string Approved = "APPROVED";
}

/// <summary>Reply outcomes.</summary>
internal static class ComplaintOutcomes
{
    public const string Upheld = "UPHELD";
    public const string PartiallyUpheld = "PARTIALLY_UPHELD";
    public const string Rejected = "REJECTED";

    public static bool IsUpheld(string? outcome) => outcome is Upheld or PartiallyUpheld;
}

/// <summary>A complaint (cmp.complaint). Links and actions are small JSON arrays on the row.</summary>
internal sealed class ComplaintRow
{
    public ComplaintId ComplaintId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Number { get; set; } = string.Empty;

    public string Status { get; set; } = ComplaintStatuses.Received;

    public Instant ReceivedAt { get; set; }

    public string? Channel { get; set; }

    public PartyId? ComplainantPartyId { get; set; }

    public string? ComplainantType { get; set; }

    public string Description { get; set; } = string.Empty;

    public string? Language { get; set; }

    public string? Category { get; set; }

    public string? Cause { get; set; }

    public string? ProductLine { get; set; }

    public bool Vulnerable { get; set; }

    /// <summary>Linked objects (JSON array of module, type, id).</summary>
    public string Links { get; set; } = "[]";

    /// <summary>Investigation steps (JSON array).</summary>
    public string Actions { get; set; } = "[]";

    public Instant? AcknowledgedAt { get; set; }

    public string? AcknowledgementNote { get; set; }

    public string ReplyStatus { get; set; } = ComplaintReplyStatuses.None;

    public string? Outcome { get; set; }

    public string? ReplyText { get; set; }

    public string? ReplyAuthor { get; set; }

    public string? ReplyApprovedBy { get; set; }

    public string? RedressAmount { get; set; }

    public string? RootCause { get; set; }

    public string? AdrBody { get; set; }

    public string? AdrReference { get; set; }

    public Instant? AnsweredAt { get; set; }

    public Instant? ClosedAt { get; set; }

    public string? ClosedReason { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>The reply clock instance (null when no rule exists for the jurisdiction).</summary>
    public Guid? ReplyClockId { get; set; }

    public bool RulesMissing { get; set; }

    public int RecordVersion { get; set; }
}

internal static class ComplaintMapping
{
    public static void MapComplaints(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ComplaintRow>(entity =>
        {
            entity.ToTable("complaint", table =>
            {
                table.HasCheckConstraint("ck_complaint_status", "status IN ('RECEIVED','ACKNOWLEDGED','UNDER_INVESTIGATION','ANSWERED','ESCALATED','CLOSED')");
                table.HasCheckConstraint("ck_complaint_reply_status", "reply_status IN ('NONE','PENDING_APPROVAL','RETURNED','APPROVED')");
                table.HasCheckConstraint("ck_complaint_outcome", "outcome IS NULL OR outcome IN ('UPHELD','PARTIALLY_UPHELD','REJECTED')");
                table.HasCheckConstraint("ck_complaint_record_version", "record_version >= 1");
            });
            entity.HasKey(e => e.ComplaintId).HasName("pk_complaint");
            entity.Property(e => e.ComplaintId).HasColumnName("complaint_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Number).HasColumnName("number");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.ReceivedAt).HasColumnName("received_at").HasColumnType("timestamptz");
            entity.Property(e => e.Channel).HasColumnName("channel");
            entity.Property(e => e.ComplainantPartyId).HasColumnName("complainant_party_id");
            entity.Property(e => e.ComplainantType).HasColumnName("complainant_type");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.Language).HasColumnName("language");
            entity.Property(e => e.Category).HasColumnName("category");
            entity.Property(e => e.Cause).HasColumnName("cause");
            entity.Property(e => e.ProductLine).HasColumnName("product_line");
            entity.Property(e => e.Vulnerable).HasColumnName("vulnerable");
            entity.Property(e => e.Links).HasColumnName("links").HasColumnType("jsonb");
            entity.Property(e => e.Actions).HasColumnName("actions").HasColumnType("jsonb");
            entity.Property(e => e.AcknowledgedAt).HasColumnName("acknowledged_at").HasColumnType("timestamptz");
            entity.Property(e => e.AcknowledgementNote).HasColumnName("acknowledgement_note");
            entity.Property(e => e.ReplyStatus).HasColumnName("reply_status");
            entity.Property(e => e.Outcome).HasColumnName("outcome");
            entity.Property(e => e.ReplyText).HasColumnName("reply_text");
            entity.Property(e => e.ReplyAuthor).HasColumnName("reply_author");
            entity.Property(e => e.ReplyApprovedBy).HasColumnName("reply_approved_by");
            entity.Property(e => e.RedressAmount).HasColumnName("redress_amount");
            entity.Property(e => e.RootCause).HasColumnName("root_cause");
            entity.Property(e => e.AdrBody).HasColumnName("adr_body");
            entity.Property(e => e.AdrReference).HasColumnName("adr_reference");
            entity.Property(e => e.AnsweredAt).HasColumnName("answered_at").HasColumnType("timestamptz");
            entity.Property(e => e.ClosedAt).HasColumnName("closed_at").HasColumnType("timestamptz");
            entity.Property(e => e.ClosedReason).HasColumnName("closed_reason");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.ReplyClockId).HasColumnName("reply_clock_id");
            entity.Property(e => e.RulesMissing).HasColumnName("rules_missing");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.HasIndex(e => new { e.LegalEntityId, e.Number }).IsUnique().HasDatabaseName("ux_complaint_number");
            entity.HasIndex(e => new { e.LegalEntityId, e.Status }).HasDatabaseName("ix_complaint_status");
        });
    }
}
