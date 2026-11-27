using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Migration.Services.Quality;

/// <summary><c>mig.dq_rule</c>: one rule of one rule-set version, recorded when a run first uses that version. Insert-only.</summary>
internal sealed class DqRuleRow
{
    public Guid RuleId { get; set; }

    public int RuleSetVersion { get; set; }

    public string Code { get; set; } = string.Empty;

    public string ObjectType { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Severity { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
}

/// <summary><c>mig.dq_run</c>: one run of one rule-set version over one snapshot, with its profile. Insert-only; a re-run is a new row.</summary>
internal sealed class DqRunRow
{
    public Guid RunId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public Guid SnapshotId { get; set; }

    public int RuleSetVersion { get; set; }

    public int RecordCount { get; set; }

    public int ErrorCount { get; set; }

    public int WarningCount { get; set; }

    /// <summary>Findings per rule code, as a JSON object.</summary>
    public string FindingsByRule { get; set; } = "{}";

    /// <summary>The per object type profile, as JSON.</summary>
    public string Profile { get; set; } = "[]";

    public string RunBy { get; set; } = string.Empty;

    public Instant RunAt { get; set; }
}

/// <summary><c>mig.dq_finding</c>: one rule violation of one staged record in one run. Insert-only.</summary>
internal sealed class DqFindingRow
{
    public Guid FindingId { get; set; }

    public Guid RunId { get; set; }

    public Guid SnapshotId { get; set; }

    public Guid RecordId { get; set; }

    public string ObjectType { get; set; } = string.Empty;

    public string LegacyKey { get; set; } = string.Empty;

    public string RuleCode { get; set; } = string.Empty;

    public string Severity { get; set; } = string.Empty;

    public string? Field { get; set; }

    public string Message { get; set; } = string.Empty;

    public int Seq { get; set; }
}

/// <summary>
/// <c>mig.dq_waiver</c>: a data steward's waiver of one rule on one legacy record of a source, until <see cref="ExpiresOn"/> (inclusive).
/// Insert-only: a waiver is never edited or deleted; after its expiry the finding is open again.
/// </summary>
internal sealed class DqWaiverRow
{
    public Guid WaiverId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Source { get; set; } = string.Empty;

    public string ObjectType { get; set; } = string.Empty;

    public string LegacyKey { get; set; } = string.Empty;

    public string RuleCode { get; set; } = string.Empty;

    public Guid FindingId { get; set; }

    public string Reason { get; set; } = string.Empty;

    public DateOnly ExpiresOn { get; set; }

    public string WaivedBy { get; set; } = string.Empty;

    public Instant WaivedAt { get; set; }
}

internal static class DqModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<DqRuleRow>(e =>
        {
            e.ToTable("dq_rule");
            e.HasKey(r => r.RuleId).HasName("pk_dq_rule");
            e.Property(r => r.RuleId).HasColumnName("rule_id");
            e.Property(r => r.RuleSetVersion).HasColumnName("rule_set_version");
            e.Property(r => r.Code).HasColumnName("code");
            e.Property(r => r.ObjectType).HasColumnName("object_type");
            e.Property(r => r.Kind).HasColumnName("kind");
            e.Property(r => r.Severity).HasColumnName("severity");
            e.Property(r => r.Description).HasColumnName("description");
            e.HasIndex(r => new { r.RuleSetVersion, r.Code, r.ObjectType }).IsUnique().HasDatabaseName("ux_dq_rule_version_code");
        });

        model.Entity<DqRunRow>(e =>
        {
            e.ToTable("dq_run");
            e.HasKey(r => r.RunId).HasName("pk_dq_run");
            e.Property(r => r.RunId).HasColumnName("run_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.SnapshotId).HasColumnName("snapshot_id");
            e.Property(r => r.RuleSetVersion).HasColumnName("rule_set_version");
            e.Property(r => r.RecordCount).HasColumnName("record_count");
            e.Property(r => r.ErrorCount).HasColumnName("error_count");
            e.Property(r => r.WarningCount).HasColumnName("warning_count");
            e.Property(r => r.FindingsByRule).HasColumnName("findings_by_rule").HasColumnType("jsonb");
            e.Property(r => r.Profile).HasColumnName("profile").HasColumnType("jsonb");
            e.Property(r => r.RunBy).HasColumnName("run_by");
            e.Property(r => r.RunAt).HasColumnName("run_at").HasColumnType("timestamptz");
            e.HasIndex(r => new { r.SnapshotId, r.RunAt }).HasDatabaseName("ix_dq_run_snapshot");
            e.HasOne<Staging.StagingSnapshotRow>().WithMany().HasForeignKey(r => r.SnapshotId).HasConstraintName("fk_dq_run_snapshot");
        });

        model.Entity<DqFindingRow>(e =>
        {
            e.ToTable("dq_finding", t => t.HasCheckConstraint("ck_dq_finding_severity", "severity IN ('ERROR','WARNING')"));
            e.HasKey(r => r.FindingId).HasName("pk_dq_finding");
            e.Property(r => r.FindingId).HasColumnName("finding_id");
            e.Property(r => r.RunId).HasColumnName("run_id");
            e.Property(r => r.SnapshotId).HasColumnName("snapshot_id");
            e.Property(r => r.RecordId).HasColumnName("record_id");
            e.Property(r => r.ObjectType).HasColumnName("object_type");
            e.Property(r => r.LegacyKey).HasColumnName("legacy_key");
            e.Property(r => r.RuleCode).HasColumnName("rule_code");
            e.Property(r => r.Severity).HasColumnName("severity");
            e.Property(r => r.Field).HasColumnName("field");
            e.Property(r => r.Message).HasColumnName("message");
            e.Property(r => r.Seq).HasColumnName("seq");
            e.HasIndex(r => new { r.RunId, r.Seq }).IsUnique().HasDatabaseName("ux_dq_finding_run_seq");
            e.HasIndex(r => new { r.RunId, r.RuleCode }).HasDatabaseName("ix_dq_finding_rule");
            e.HasOne<DqRunRow>().WithMany().HasForeignKey(r => r.RunId).HasConstraintName("fk_dq_finding_run");
        });

        model.Entity<DqWaiverRow>(e =>
        {
            e.ToTable("dq_waiver");
            e.HasKey(r => r.WaiverId).HasName("pk_dq_waiver");
            e.Property(r => r.WaiverId).HasColumnName("waiver_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.Source).HasColumnName("source");
            e.Property(r => r.ObjectType).HasColumnName("object_type");
            e.Property(r => r.LegacyKey).HasColumnName("legacy_key");
            e.Property(r => r.RuleCode).HasColumnName("rule_code");
            e.Property(r => r.FindingId).HasColumnName("finding_id");
            e.Property(r => r.Reason).HasColumnName("reason");
            e.Property(r => r.ExpiresOn).HasColumnName("expires_on").HasColumnType("date");
            e.Property(r => r.WaivedBy).HasColumnName("waived_by");
            e.Property(r => r.WaivedAt).HasColumnName("waived_at").HasColumnType("timestamptz");
            e.HasIndex(r => new { r.LegalEntityId, r.Source, r.RuleCode, r.ObjectType, r.LegacyKey }).HasDatabaseName("ix_dq_waiver_key");
            e.HasOne<DqFindingRow>().WithMany().HasForeignKey(r => r.FindingId).HasConstraintName("fk_dq_waiver_finding");
        });
    }
}
