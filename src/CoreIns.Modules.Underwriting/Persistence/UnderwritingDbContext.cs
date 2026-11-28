using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CoreIns.Modules.Underwriting.Persistence;

/// <summary><c>uw.rule_set_version</c>: a rule set version (decision table, test cases) pinned by its content hash.</summary>
internal sealed class RuleSetVersionRow
{
    public string RuleSetCode { get; set; } = string.Empty;

    public string VersionNo { get; set; } = string.Empty;

    public string ProductCode { get; set; } = string.Empty;

    public string Checkpoint { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public DateOnly EffectiveFrom { get; set; }

    public string DataStatus { get; set; } = string.Empty;

    public string Definition { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>Incremented on every change (optimistic concurrency of edits).</summary>
    public int RecordVersion { get; set; } = 1;

    public string? SubmittedBy { get; set; }

    public Instant? SubmittedAt { get; set; }

    /// <summary>The plt.Approval request of the release (UW.RULESET_APPROVAL).</summary>
    public Guid? ApprovalRequestId { get; set; }

    public string? DecidedBy { get; set; }

    public Instant? DecidedAt { get; set; }

    public string? DecisionReason { get; set; }
}

/// <summary><c>uw.issue_type</c>: the catalogue of issue types (code, names, severity, default lane, blocking point).</summary>
internal sealed class IssueTypeRow
{
    public string Code { get; set; } = string.Empty;

    public string NameEl { get; set; } = string.Empty;

    public string NameEn { get; set; } = string.Empty;

    public string Severity { get; set; } = string.Empty;

    public string DefaultLane { get; set; } = string.Empty;

    public string BlockingPoint { get; set; } = string.Empty;

    public bool Active { get; set; } = true;

    public int RecordVersion { get; set; } = 1;

    public Instant UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary><c>uw.evaluation</c>: one non-dry evaluation with its rule-set version, outcome and trace (REQ-UW-042).</summary>
internal sealed class EvaluationRow
{
    public Guid EvaluationId { get; set; }

    public Guid LegalEntityId { get; set; }

    public Guid JobId { get; set; }

    public string Checkpoint { get; set; } = string.Empty;

    public string RuleSetCode { get; set; } = string.Empty;

    public string RuleSetVersion { get; set; } = string.Empty;

    public string RuleSetHash { get; set; } = string.Empty;

    public string SnapshotRef { get; set; } = string.Empty;

    public string? SnapshotHash { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public string Lane { get; set; } = string.Empty;

    public string Trace { get; set; } = string.Empty;

    /// <summary>Actors who created, edited, quoted or bound the job, as POL reported them (SOD-UW-02); empty for HTTP evaluations.</summary>
    public List<string> JobParticipants { get; set; } = [];

    /// <summary>Producer code on the job, as POL reported it (SOD-UW-02).</summary>
    public string? ProducerCode { get; set; }

    /// <summary>
    /// The risk facts the rules read, in a derived form without personal data (vehicle facts, the youngest driver's age band,
    /// claims count; never a birth date), for the referral workbench. POL's in-process evaluations only; null before D-USR-13.
    /// </summary>
    public string? Facts { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>
/// <c>uw.issue</c>: a UW issue (PRD-04 §7.3). Open until decided (Approved, Rejected) or until the rule stops hitting (Closed);
/// an approval whose facts changed is Invalidated and a new Open issue takes its key. The fingerprint is the SHA-256 of the
/// facts the rules read at the last evaluation; the decision fingerprint is the one the decider saw (REQ-UW-091).
/// </summary>
internal sealed class IssueRow
{
    public Guid IssueId { get; set; }

    public Guid LegalEntityId { get; set; }

    public Guid JobId { get; set; }

    public string IssueType { get; set; } = string.Empty;

    public string IssueKey { get; set; } = string.Empty;

    public string BlockingPoint { get; set; } = string.Empty;

    public string Severity { get; set; } = string.Empty;

    public string Lane { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string RuleId { get; set; } = string.Empty;

    public string MessageEn { get; set; } = string.Empty;

    public string MessageEl { get; set; } = string.Empty;

    public Guid RaisedEvaluationId { get; set; }

    public Guid? ClosedEvaluationId { get; set; }

    public string? CloseReason { get; set; }

    public int RecordVersion { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant? ClosedAt { get; set; }

    public string? Fingerprint { get; set; }

    public string? DecisionFingerprint { get; set; }

    public string? Decision { get; set; }

    public string? DecidedBy { get; set; }

    public Instant? DecidedAt { get; set; }

    public string? DecisionReason { get; set; }

    public string? DecisionMessage { get; set; }

    public Guid? AuthorityCheckId { get; set; }
}

/// <summary>
/// <c>uw.referral_state</c>: the handling state of a referred job (BO-UW-05): lifecycle, paused SLA, refer-up target, and the soft
/// review lock (owner and lease expiry). Decisions stay on <c>uw.issue</c>. Optimistic on <c>record_version</c>.
/// </summary>
internal sealed class ReferralStateRow
{
    public Guid LegalEntityId { get; set; }

    public Guid JobId { get; set; }

    public string Lifecycle { get; set; } = "Active";

    public bool AwaitingInfo { get; set; }

    public string? InfoComment { get; set; }

    public Instant? SlaPausedAt { get; set; }

    public Instant? SlaDue { get; set; }

    public string Priority { get; set; } = "NORMAL";

    public string? RequiredRole { get; set; }

    public string? ReferredBy { get; set; }

    public string RequestComment { get; set; } = string.Empty;

    public string RequestedBy { get; set; } = string.Empty;

    public Instant RequestedAt { get; set; }

    public string? LockOwner { get; set; }

    public Instant? LockAcquiredAt { get; set; }

    public Instant? LockExpiresAt { get; set; }

    public int RecordVersion { get; set; } = 1;

    public Instant UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary>
/// <c>uw.decline</c>: the decline record and refusal register entry (BO-UW-07). Reasons and declined pairs are JSON; the review of an
/// automated decline (requested by the customer or producer, decided by a second underwriter) is on the same row.
/// </summary>
internal sealed class DeclineRow
{
    public Guid DeclineId { get; set; }

    public Guid LegalEntityId { get; set; }

    public string DeclineNumber { get; set; } = string.Empty;

    public Guid? JobId { get; set; }

    public Guid? IntakeId { get; set; }

    public string Scope { get; set; } = string.Empty;

    public string ReasonCodes { get; set; } = "[]";

    public string Pairs { get; set; } = "[]";

    public string? Wording { get; set; }

    public bool Automated { get; set; }

    public string Status { get; set; } = "Issued";

    public Guid? EvaluationId { get; set; }

    /// <summary>The data of the refusal letter (number, reasons, wording, automated notice, review route).</summary>
    public string LetterData { get; set; } = "{}";

    public string LetterStatus { get; set; } = "DATA_READY";

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant? ReviewRequestedAt { get; set; }

    public string? ReviewRequestedBy { get; set; }

    public string? ReviewRequester { get; set; }

    public string? ReviewStatement { get; set; }

    public Instant? ReviewDecidedAt { get; set; }

    public string? ReviewDecidedBy { get; set; }

    public string? ReviewOutcome { get; set; }

    public string? ReviewReason { get; set; }

    public int RecordVersion { get; set; } = 1;
}

/// <summary>
/// <c>uw.contingency</c>: a condition on a policy term that must be met by its due date (BO-UW-08, REQ-UW-006). With a job and a blocking
/// point it gates <c>uw.Issue.blockingStatus</c> while it is Open. Overdue is a timestamp, not a status: an overdue contingency is still Open.
/// </summary>
internal sealed class ContingencyRow
{
    public Guid ContingencyId { get; set; }

    public Guid LegalEntityId { get; set; }

    public Guid TermId { get; set; }

    public Guid? JobId { get; set; }

    public string ContingencyType { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string? CustomerText { get; set; }

    public DateOnly DueDate { get; set; }

    public string Owner { get; set; } = string.Empty;

    public string BlockingPoint { get; set; } = "PRE_ISSUE";

    public string ConsequenceCode { get; set; } = "NONE";

    public string? ConsequenceIssueType { get; set; }

    public string Status { get; set; } = "Open";

    public Instant? OverdueAt { get; set; }

    public Instant? ResolvedAt { get; set; }

    public string? ResolvedBy { get; set; }

    public string? ResolutionNote { get; set; }

    public string? EvidenceRef { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public int RecordVersion { get; set; } = 1;
}

/// <summary>The Underwriting module's EF Core context (schema <c>uw</c>): tables and migrations; reads and writes use Dapper on the scope's connection.</summary>
internal sealed class UnderwritingDbContext(DbContextOptions<UnderwritingDbContext> options) : ModuleDbContext(options)
{
    public DbSet<RuleSetVersionRow> RuleSetVersions => Set<RuleSetVersionRow>();

    public DbSet<IssueTypeRow> IssueTypes => Set<IssueTypeRow>();

    public DbSet<EvaluationRow> Evaluations => Set<EvaluationRow>();

    public DbSet<IssueRow> Issues => Set<IssueRow>();

    public DbSet<ReferralStateRow> ReferralStates => Set<ReferralStateRow>();

    public DbSet<DeclineRow> Declines => Set<DeclineRow>();

    public DbSet<ContingencyRow> Contingencies => Set<ContingencyRow>();

    protected override string Schema => UnderwritingModule.Schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RuleSetVersionRow>(entity =>
        {
            entity.ToTable("rule_set_version", table =>
            {
                table.HasCheckConstraint("ck_rule_set_version_status", "status IN ('Draft', 'Submitted', 'Approved', 'Active', 'Superseded', 'Retired')");
                table.HasCheckConstraint("ck_rule_set_version_checkpoint", "checkpoint IN ('PRE_QUOTE', 'PRE_BIND')");
                table.HasCheckConstraint("ck_rule_set_version_data", "data_status IN ('ILLUSTRATIVE_TEST_DATA', 'APPROVED')");
            });
            entity.HasKey(e => new { e.RuleSetCode, e.VersionNo }).HasName("pk_rule_set_version");
            entity.Property(e => e.RuleSetCode).HasColumnName("rule_set_code");
            entity.Property(e => e.VersionNo).HasColumnName("version_no");
            entity.Property(e => e.ProductCode).HasColumnName("product_code");
            entity.Property(e => e.Checkpoint).HasColumnName("checkpoint");
            entity.Property(e => e.ContentHash).HasColumnName("content_hash").HasColumnType("char(64)");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            entity.Property(e => e.DataStatus).HasColumnName("data_status");
            entity.Property(e => e.Definition).HasColumnName("definition").HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").HasDefaultValue(1);
            entity.Property(e => e.SubmittedBy).HasColumnName("submitted_by");
            entity.Property(e => e.SubmittedAt).HasColumnName("submitted_at").HasColumnType("timestamptz");
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.DecidedBy).HasColumnName("decided_by");
            entity.Property(e => e.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            entity.Property(e => e.DecisionReason).HasColumnName("decision_reason");
            entity.HasIndex(e => new { e.ProductCode, e.Checkpoint, e.Status, e.EffectiveFrom }).HasDatabaseName("ix_rule_set_version_product");
        });

        modelBuilder.Entity<IssueTypeRow>(entity =>
        {
            entity.ToTable("issue_type", table =>
            {
                table.HasCheckConstraint("ck_issue_type_severity", "severity IN ('REFER', 'DECLINE')");
                table.HasCheckConstraint("ck_issue_type_lane", "default_lane IN ('STRAIGHT_THROUGH', 'ASSISTED', 'EXPERT')");
                table.HasCheckConstraint("ck_issue_type_blocking", "blocking_point IN ('PRE_QUOTE', 'PRE_BIND', 'PRE_ISSUE', 'NON_BLOCKING')");
            });
            entity.HasKey(e => e.Code).HasName("pk_issue_type");
            entity.Property(e => e.Code).HasColumnName("code");
            entity.Property(e => e.NameEl).HasColumnName("name_el");
            entity.Property(e => e.NameEn).HasColumnName("name_en");
            entity.Property(e => e.Severity).HasColumnName("severity");
            entity.Property(e => e.DefaultLane).HasColumnName("default_lane");
            entity.Property(e => e.BlockingPoint).HasColumnName("blocking_point");
            entity.Property(e => e.Active).HasColumnName("active");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<EvaluationRow>(entity =>
        {
            entity.ToTable("evaluation", table =>
            {
                table.HasCheckConstraint("ck_evaluation_outcome", "outcome IN ('ACCEPT', 'REFER', 'DECLINE')");
                table.HasCheckConstraint("ck_evaluation_checkpoint", "checkpoint IN ('PRE_QUOTE', 'PRE_BIND', 'PRE_ISSUE', 'RENEWAL')");
            });
            entity.HasKey(e => e.EvaluationId).HasName("pk_evaluation");
            entity.Property(e => e.EvaluationId).HasColumnName("evaluation_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.JobId).HasColumnName("job_id");
            entity.Property(e => e.Checkpoint).HasColumnName("checkpoint");
            entity.Property(e => e.RuleSetCode).HasColumnName("rule_set_code");
            entity.Property(e => e.RuleSetVersion).HasColumnName("rule_set_version");
            entity.Property(e => e.RuleSetHash).HasColumnName("rule_set_hash").HasColumnType("char(64)");
            entity.Property(e => e.SnapshotRef).HasColumnName("snapshot_ref");
            entity.Property(e => e.SnapshotHash).HasColumnName("snapshot_hash").HasColumnType("char(64)");
            entity.Property(e => e.Outcome).HasColumnName("outcome");
            entity.Property(e => e.Lane).HasColumnName("lane");
            entity.Property(e => e.Trace).HasColumnName("trace").HasColumnType("jsonb");
            entity.Property(e => e.JobParticipants).HasColumnName("job_participants").HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]");
            entity.Property(e => e.ProducerCode).HasColumnName("producer_code");
            entity.Property(e => e.Facts).HasColumnName("facts").HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => new { e.JobId, e.CreatedAt }).HasDatabaseName("ix_evaluation_job");
            entity.HasOne<RuleSetVersionRow>().WithMany().HasForeignKey(e => new { e.RuleSetCode, e.RuleSetVersion })
                .HasConstraintName("fk_evaluation_rule_set").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IssueRow>(entity =>
        {
            entity.ToTable("issue", table =>
            {
                table.HasCheckConstraint("ck_issue_status", "status IN ('Open', 'Approved', 'ApprovedWithConditions', 'Rejected', 'Invalidated', 'Closed')");
                table.HasCheckConstraint("ck_issue_blocking_point", "blocking_point IN ('PRE_QUOTE', 'PRE_BIND', 'PRE_ISSUE', 'NON_BLOCKING')");
                table.HasCheckConstraint("ck_issue_severity", "severity IN ('REFER', 'DECLINE', 'WARN')");
                table.HasCheckConstraint("ck_issue_decision", "decision IS NULL OR decision IN ('APPROVE', 'REJECT')");
                table.HasCheckConstraint(
                    "ck_issue_decided",
                    "status NOT IN ('Approved', 'ApprovedWithConditions', 'Rejected') OR (decision IS NOT NULL AND decided_by IS NOT NULL AND decided_at IS NOT NULL"
                    + " AND decision_reason IS NOT NULL AND decision_fingerprint IS NOT NULL AND authority_check_id IS NOT NULL)");
            });
            entity.HasKey(e => e.IssueId).HasName("pk_issue");
            entity.Property(e => e.IssueId).HasColumnName("issue_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.JobId).HasColumnName("job_id");
            entity.Property(e => e.IssueType).HasColumnName("issue_type");
            entity.Property(e => e.IssueKey).HasColumnName("issue_key");
            entity.Property(e => e.BlockingPoint).HasColumnName("blocking_point");
            entity.Property(e => e.Severity).HasColumnName("severity");
            entity.Property(e => e.Lane).HasColumnName("lane");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.RuleId).HasColumnName("rule_id");
            entity.Property(e => e.MessageEn).HasColumnName("message_en");
            entity.Property(e => e.MessageEl).HasColumnName("message_el");
            entity.Property(e => e.RaisedEvaluationId).HasColumnName("raised_evaluation_id");
            entity.Property(e => e.ClosedEvaluationId).HasColumnName("closed_evaluation_id");
            entity.Property(e => e.CloseReason).HasColumnName("close_reason");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.ClosedAt).HasColumnName("closed_at").HasColumnType("timestamptz");
            entity.Property(e => e.Fingerprint).HasColumnName("fingerprint").HasColumnType("char(64)");
            entity.Property(e => e.DecisionFingerprint).HasColumnName("decision_fingerprint").HasColumnType("char(64)");
            entity.Property(e => e.Decision).HasColumnName("decision");
            entity.Property(e => e.DecidedBy).HasColumnName("decided_by");
            entity.Property(e => e.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            entity.Property(e => e.DecisionReason).HasColumnName("decision_reason");
            entity.Property(e => e.DecisionMessage).HasColumnName("decision_message");
            entity.Property(e => e.AuthorityCheckId).HasColumnName("authority_check_id");
            // One non-terminal issue per key (PRD-04 §7.1: unique among non-terminal issues).
            entity.HasIndex(e => new { e.JobId, e.IssueKey }).IsUnique()
                .HasFilter("status IN ('Open', 'Approved', 'ApprovedWithConditions', 'Rejected')").HasDatabaseName("ux_issue_open_key");
            entity.HasIndex(e => new { e.Status, e.CreatedAt }).HasDatabaseName("ix_issue_status");
            entity.HasIndex(e => new { e.JobId, e.Status }).HasDatabaseName("ix_issue_job");
            entity.HasOne<EvaluationRow>().WithMany().HasForeignKey(e => e.RaisedEvaluationId)
                .HasConstraintName("fk_issue_raised_evaluation").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReferralStateRow>(entity =>
        {
            entity.ToTable("referral_state", table =>
            {
                table.HasCheckConstraint("ck_referral_state_lifecycle", "lifecycle IN ('Active', 'Withdrawn')");
                table.HasCheckConstraint("ck_referral_state_priority", "priority IN ('NORMAL', 'HIGH')");
                table.HasCheckConstraint("ck_referral_state_lock", "(lock_owner IS NULL) = (lock_expires_at IS NULL)");
            });
            entity.HasKey(e => new { e.LegalEntityId, e.JobId }).HasName("pk_referral_state");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.JobId).HasColumnName("job_id");
            entity.Property(e => e.Lifecycle).HasColumnName("lifecycle");
            entity.Property(e => e.AwaitingInfo).HasColumnName("awaiting_info");
            entity.Property(e => e.InfoComment).HasColumnName("info_comment");
            entity.Property(e => e.SlaPausedAt).HasColumnName("sla_paused_at").HasColumnType("timestamptz");
            entity.Property(e => e.SlaDue).HasColumnName("sla_due").HasColumnType("timestamptz");
            entity.Property(e => e.Priority).HasColumnName("priority");
            entity.Property(e => e.RequiredRole).HasColumnName("required_role");
            entity.Property(e => e.ReferredBy).HasColumnName("referred_by");
            entity.Property(e => e.RequestComment).HasColumnName("request_comment");
            entity.Property(e => e.RequestedBy).HasColumnName("requested_by");
            entity.Property(e => e.RequestedAt).HasColumnName("requested_at").HasColumnType("timestamptz");
            entity.Property(e => e.LockOwner).HasColumnName("lock_owner");
            entity.Property(e => e.LockAcquiredAt).HasColumnName("lock_acquired_at").HasColumnType("timestamptz");
            entity.Property(e => e.LockExpiresAt).HasColumnName("lock_expires_at").HasColumnType("timestamptz");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.HasIndex(e => new { e.LegalEntityId, e.Lifecycle, e.SlaDue }).HasDatabaseName("ix_referral_state_queue");
        });

        modelBuilder.Entity<DeclineRow>(entity =>
        {
            entity.ToTable("decline", table =>
            {
                table.HasCheckConstraint("ck_decline_subject", "(job_id IS NULL) <> (intake_id IS NULL)");
                table.HasCheckConstraint("ck_decline_status", "status IN ('Issued', 'ReviewRequested', 'Upheld', 'Overturned')");
                table.HasCheckConstraint("ck_decline_scope", "scope IN ('FULL', 'PARTIAL')");
            });
            entity.HasKey(e => e.DeclineId).HasName("pk_decline");
            entity.Property(e => e.DeclineId).HasColumnName("decline_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.DeclineNumber).HasColumnName("decline_number");
            entity.Property(e => e.JobId).HasColumnName("job_id");
            entity.Property(e => e.IntakeId).HasColumnName("intake_id");
            entity.Property(e => e.Scope).HasColumnName("scope");
            entity.Property(e => e.ReasonCodes).HasColumnName("reason_codes").HasColumnType("jsonb");
            entity.Property(e => e.Pairs).HasColumnName("pairs").HasColumnType("jsonb");
            entity.Property(e => e.Wording).HasColumnName("wording");
            entity.Property(e => e.Automated).HasColumnName("automated");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.EvaluationId).HasColumnName("evaluation_id");
            entity.Property(e => e.LetterData).HasColumnName("letter_data").HasColumnType("jsonb");
            entity.Property(e => e.LetterStatus).HasColumnName("letter_status");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.ReviewRequestedAt).HasColumnName("review_requested_at").HasColumnType("timestamptz");
            entity.Property(e => e.ReviewRequestedBy).HasColumnName("review_requested_by");
            entity.Property(e => e.ReviewRequester).HasColumnName("review_requester");
            entity.Property(e => e.ReviewStatement).HasColumnName("review_statement");
            entity.Property(e => e.ReviewDecidedAt).HasColumnName("review_decided_at").HasColumnType("timestamptz");
            entity.Property(e => e.ReviewDecidedBy).HasColumnName("review_decided_by");
            entity.Property(e => e.ReviewOutcome).HasColumnName("review_outcome");
            entity.Property(e => e.ReviewReason).HasColumnName("review_reason");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.HasIndex(e => new { e.LegalEntityId, e.DeclineNumber }).IsUnique().HasDatabaseName("ux_decline_number");
            entity.HasIndex(e => e.JobId).IsUnique().HasFilter("job_id IS NOT NULL AND status <> 'Overturned'").HasDatabaseName("ux_decline_job");
            entity.HasIndex(e => e.IntakeId).IsUnique().HasFilter("intake_id IS NOT NULL AND status <> 'Overturned'").HasDatabaseName("ux_decline_intake");
            entity.HasIndex(e => new { e.LegalEntityId, e.CreatedAt, e.DeclineId }).HasDatabaseName("ix_decline_register");
        });
        modelBuilder.Entity<ContingencyRow>(entity =>
        {
            entity.ToTable("contingency", table =>
            {
                table.HasCheckConstraint("ck_contingency_status", "status IN ('Open', 'Satisfied', 'Waived', 'ConsequenceApplied')");
                table.HasCheckConstraint("ck_contingency_blocking_point", "blocking_point IN ('PRE_QUOTE', 'PRE_BIND', 'PRE_ISSUE', 'NON_BLOCKING')");
                table.HasCheckConstraint("ck_contingency_consequence", "consequence_code IN ('NONE', 'CANCEL_QUOTE', 'ADD_ISSUE')");
            });
            entity.HasKey(e => e.ContingencyId).HasName("pk_contingency");
            entity.Property(e => e.ContingencyId).HasColumnName("contingency_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.TermId).HasColumnName("term_id");
            entity.Property(e => e.JobId).HasColumnName("job_id");
            entity.Property(e => e.ContingencyType).HasColumnName("contingency_type");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.CustomerText).HasColumnName("customer_text");
            entity.Property(e => e.DueDate).HasColumnName("due_date").HasColumnType("date");
            entity.Property(e => e.Owner).HasColumnName("owner");
            entity.Property(e => e.BlockingPoint).HasColumnName("blocking_point");
            entity.Property(e => e.ConsequenceCode).HasColumnName("consequence_code");
            entity.Property(e => e.ConsequenceIssueType).HasColumnName("consequence_issue_type");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.OverdueAt).HasColumnName("overdue_at").HasColumnType("timestamptz");
            entity.Property(e => e.ResolvedAt).HasColumnName("resolved_at").HasColumnType("timestamptz");
            entity.Property(e => e.ResolvedBy).HasColumnName("resolved_by");
            entity.Property(e => e.ResolutionNote).HasColumnName("resolution_note");
            entity.Property(e => e.EvidenceRef).HasColumnName("evidence_ref");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.HasIndex(e => new { e.LegalEntityId, e.TermId, e.Status }).HasDatabaseName("ix_contingency_term");
            entity.HasIndex(e => new { e.LegalEntityId, e.JobId, e.Status }).HasDatabaseName("ix_contingency_job");
            entity.HasIndex(e => new { e.Status, e.DueDate }).HasDatabaseName("ix_contingency_due");
        });
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add … --project src/CoreIns.Modules.Underwriting</c> (no connection opened).</summary>
internal sealed class UnderwritingDbContextDesignTimeFactory : IDesignTimeDbContextFactory<UnderwritingDbContext>
{
    public UnderwritingDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<UnderwritingDbContext>("Host=localhost;Database=coreins_design;Username=design", UnderwritingModule.Schema));
}
