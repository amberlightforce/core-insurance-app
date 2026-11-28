using CoreIns.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.Persistence;

/// <summary>
/// <c>fin.posting_rule_draft</c> (BO-FIN-07): a posting-rule set proposal. It is a workflow record, never read by intake: only an approved
/// draft is turned into a new, immutable <c>fin.posting_rule_set</c> version (effective-dated), so authoring cannot change what posts
/// until a second person approves it. The rules and sample events are stored as JSON documents; the content hash covers book, effective
/// date and the rules, so an edited draft can never be approved on an earlier approval (NX-EC-09).
/// </summary>
internal sealed class PostingRuleDraftRow
{
    public Guid DraftId { get; set; }

    public Guid LegalEntityId { get; set; }

    public string LegalEntityCode { get; set; } = string.Empty;

    public string Book { get; set; } = string.Empty;

    /// <summary>Rule-set version in force when the draft was last saved (informational base).</summary>
    public int? BaseVersion { get; set; }

    public BusinessDate EffectiveFrom { get; set; }

    public string Status { get; set; } = string.Empty;

    /// <summary>JSON array of the rule rows.</summary>
    public string Rules { get; set; } = "[]";

    /// <summary>JSON array of the sample events (golden cases).</summary>
    public string SampleEvents { get; set; } = "[]";

    public string ContentHash { get; set; } = string.Empty;

    public string CreatedBy { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string? SubmittedBy { get; set; }

    public Instant? SubmittedAt { get; set; }

    public string? DecidedBy { get; set; }

    public Instant? DecidedAt { get; set; }

    public string? DecisionReason { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    /// <summary>The rule-set version an approval created.</summary>
    public Guid? ActivatedRuleSetId { get; set; }

    public int? ActivatedVersion { get; set; }

    public int RecordVersion { get; set; }
}

internal static class PostingRuleDraftModel
{
    public static void Configure(ModelBuilder model) =>
        model.Entity<PostingRuleDraftRow>(e =>
        {
            e.ToTable("posting_rule_draft", t =>
            {
                t.HasCheckConstraint("ck_posting_rule_draft_status", "status IN ('DRAFT', 'SUBMITTED', 'APPROVED', 'REJECTED', 'WITHDRAWN')");
                t.HasCheckConstraint("ck_posting_rule_draft_hash", "content_hash ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("ck_posting_rule_draft_record_version", "record_version >= 1");
            });
            e.HasKey(r => r.DraftId).HasName("pk_posting_rule_draft");
            e.Property(r => r.DraftId).HasColumnName("draft_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.LegalEntityCode).HasColumnName("legal_entity_code");
            e.Property(r => r.Book).HasColumnName("book");
            e.Property(r => r.BaseVersion).HasColumnName("base_version");
            e.Property(r => r.EffectiveFrom).HasColumnName("effective_from");
            e.Property(r => r.Status).HasColumnName("status");
            e.Property(r => r.Rules).HasColumnName("rules").HasColumnType("jsonb");
            e.Property(r => r.SampleEvents).HasColumnName("sample_events").HasColumnType("jsonb");
            e.Property(r => r.ContentHash).HasColumnName("content_hash").HasColumnType("char(64)");
            e.Property(r => r.CreatedBy).HasColumnName("created_by");
            e.Property(r => r.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(r => r.SubmittedBy).HasColumnName("submitted_by");
            e.Property(r => r.SubmittedAt).HasColumnName("submitted_at").HasColumnType("timestamptz");
            e.Property(r => r.DecidedBy).HasColumnName("decided_by");
            e.Property(r => r.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            e.Property(r => r.DecisionReason).HasColumnName("decision_reason");
            e.Property(r => r.ApprovalRequestId).HasColumnName("approval_request_id");
            e.Property(r => r.ActivatedRuleSetId).HasColumnName("activated_rule_set_id");
            e.Property(r => r.ActivatedVersion).HasColumnName("activated_version");
            e.Property(r => r.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            e.HasIndex(r => new { r.LegalEntityId, r.Book, r.Status }).HasDatabaseName("ix_posting_rule_draft_book");
        });
}
