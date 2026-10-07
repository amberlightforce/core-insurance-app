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

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary><c>uw.issue</c>: a UW issue, Open until the rule stops hitting (Closed). Decide/approve belongs to the referral workbench work package.</summary>
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
}

/// <summary>The Underwriting module's EF Core context (schema <c>uw</c>): tables and migrations; reads and writes use Dapper on the scope's connection.</summary>
internal sealed class UnderwritingDbContext(DbContextOptions<UnderwritingDbContext> options) : ModuleDbContext(options)
{
    public DbSet<RuleSetVersionRow> RuleSetVersions => Set<RuleSetVersionRow>();

    public DbSet<EvaluationRow> Evaluations => Set<EvaluationRow>();

    public DbSet<IssueRow> Issues => Set<IssueRow>();

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
            entity.HasIndex(e => new { e.ProductCode, e.Checkpoint, e.Status, e.EffectiveFrom }).HasDatabaseName("ix_rule_set_version_product");
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
            entity.HasIndex(e => new { e.JobId, e.IssueKey }).IsUnique().HasFilter("status = 'Open'").HasDatabaseName("ux_issue_open_key");
            entity.HasIndex(e => new { e.JobId, e.Status }).HasDatabaseName("ix_issue_job");
            entity.HasOne<EvaluationRow>().WithMany().HasForeignKey(e => e.RaisedEvaluationId)
                .HasConstraintName("fk_issue_raised_evaluation").OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add … --project src/CoreIns.Modules.Underwriting</c> (no connection opened).</summary>
internal sealed class UnderwritingDbContextDesignTimeFactory : IDesignTimeDbContextFactory<UnderwritingDbContext>
{
    public UnderwritingDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<UnderwritingDbContext>("Host=localhost;Database=coreins_design;Username=design", UnderwritingModule.Schema));
}
