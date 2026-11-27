using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Migration.Services.SignOff;

/// <summary><c>mig.signoff</c>: one sign-off request of a scope, bound to a reconciliation run and the hash of its result rows.</summary>
internal sealed class SignOffRow
{
    public Guid SignOffId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Scope { get; set; } = string.Empty;

    public Guid RunId { get; set; }

    public string ResultsHash { get; set; } = string.Empty;

    public string RequestedBy { get; set; } = string.Empty;

    public Instant RequestedAt { get; set; }
}

/// <summary><c>mig.signoff_signatory</c>: one named signatory of a sign-off, backed by one platform approval request (<c>MIG.WAVE_SIGNOFF</c>).</summary>
internal sealed class SignOffSignatoryRow
{
    public Guid SignatoryId { get; set; }

    public Guid SignOffId { get; set; }

    public string Signatory { get; set; } = string.Empty;

    public Guid ApprovalRequestId { get; set; }

    public string Status { get; set; } = SignatoryStatus.Pending;

    public string? DecidedBy { get; set; }

    public Instant? DecidedAt { get; set; }

    public string? Comment { get; set; }
}

/// <summary><c>mig.signoff_waiver</c>: a waived variance key of one reconciliation run. Insert-only.</summary>
internal sealed class SignOffWaiverRow
{
    public Guid WaiverId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public Guid RunId { get; set; }

    public string VarianceKey { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string WaivedBy { get; set; } = string.Empty;

    public Instant WaivedAt { get; set; }
}

internal static class SignatoryStatus
{
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
}

internal static class SignOffModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<SignOffRow>(e =>
        {
            e.ToTable("signoff");
            e.HasKey(r => r.SignOffId).HasName("pk_signoff");
            e.Property(r => r.SignOffId).HasColumnName("signoff_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.Scope).HasColumnName("scope");
            e.Property(r => r.RunId).HasColumnName("run_id");
            e.Property(r => r.ResultsHash).HasColumnName("results_hash");
            e.Property(r => r.RequestedBy).HasColumnName("requested_by");
            e.Property(r => r.RequestedAt).HasColumnName("requested_at").HasColumnType("timestamptz");
            e.HasIndex(r => new { r.LegalEntityId, r.Scope, r.RequestedAt }).HasDatabaseName("ix_signoff_scope");
        });

        model.Entity<SignOffSignatoryRow>(e =>
        {
            e.ToTable("signoff_signatory");
            e.HasKey(r => r.SignatoryId).HasName("pk_signoff_signatory");
            e.Property(r => r.SignatoryId).HasColumnName("signatory_id");
            e.Property(r => r.SignOffId).HasColumnName("signoff_id");
            e.Property(r => r.Signatory).HasColumnName("signatory");
            e.Property(r => r.ApprovalRequestId).HasColumnName("approval_request_id");
            e.Property(r => r.Status).HasColumnName("status");
            e.Property(r => r.DecidedBy).HasColumnName("decided_by");
            e.Property(r => r.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            e.Property(r => r.Comment).HasColumnName("comment");
            e.HasIndex(r => new { r.SignOffId, r.Signatory }).IsUnique().HasDatabaseName("ux_signoff_signatory");
            e.HasOne<SignOffRow>().WithMany().HasForeignKey(r => r.SignOffId).HasConstraintName("fk_signoff_signatory_signoff");
            e.ToTable(t => t.HasCheckConstraint("ck_signoff_signatory_status", "status IN ('PENDING','APPROVED','REJECTED')"));
        });

        model.Entity<SignOffWaiverRow>(e =>
        {
            e.ToTable("signoff_waiver");
            e.HasKey(r => r.WaiverId).HasName("pk_signoff_waiver");
            e.Property(r => r.WaiverId).HasColumnName("waiver_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.RunId).HasColumnName("run_id");
            e.Property(r => r.VarianceKey).HasColumnName("variance_key");
            e.Property(r => r.Reason).HasColumnName("reason");
            e.Property(r => r.WaivedBy).HasColumnName("waived_by");
            e.Property(r => r.WaivedAt).HasColumnName("waived_at").HasColumnType("timestamptz");
            e.HasIndex(r => new { r.LegalEntityId, r.RunId, r.VarianceKey }).IsUnique().HasDatabaseName("ux_signoff_waiver_key");
        });
    }
}
