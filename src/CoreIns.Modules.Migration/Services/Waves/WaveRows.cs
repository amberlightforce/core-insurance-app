using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Migration.Services.Waves;

/// <summary>Wave lifecycle (BO-MIG-07).</summary>
internal static class WaveStatus
{
    public const string Planned = "PLANNED";
    public const string InProgress = "IN_PROGRESS";
    public const string Closed = "CLOSED";
}

/// <summary>State of one policy term in a wave (D-CON-07).</summary>
internal static class TermState
{
    public const string Planned = "PLANNED";
    public const string Offered = "OFFERED";
    public const string Converted = "CONVERTED";
    public const string Excluded = "EXCLUDED";
    public const string Withdrawn = "WITHDRAWN";
    public const string NotConverted = "NOT_CONVERTED";

    public static readonly string[] All = [Planned, Offered, Converted, Excluded, Withdrawn, NotConverted];

    /// <summary>The states in which a policy is claimed by a wave: it cannot be selected again.</summary>
    public static readonly string[] Claiming = [Planned, Offered, Converted];
}

/// <summary><c>mig.wave</c>: one renewal-conversion wave over a staged snapshot and a renewal date window.</summary>
internal sealed class WaveRow
{
    public Guid WaveId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = WaveStatus.Planned;

    public Guid SnapshotId { get; set; }

    public string Source { get; set; } = string.Empty;

    public string Snapshot { get; set; } = string.Empty;

    public string ObjectType { get; set; } = string.Empty;

    public string TermEndField { get; set; } = string.Empty;

    public DateOnly WindowFrom { get; set; }

    public DateOnly WindowTo { get; set; }

    /// <summary>Reconciliation and sign-off scope of the wave.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>Terms selected from staging so far; the count equation compares it with the terms in all states.</summary>
    public int SelectedCount { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string? ClosedBy { get; set; }

    public Instant? ClosedAt { get; set; }

    public int RecordVersion { get; set; }
}

/// <summary><c>mig.wave_term</c>: one legacy policy term of a wave. A policy has at most one claiming term (PLANNED, OFFERED or CONVERTED) across all waves.</summary>
internal sealed class WaveTermRow
{
    public Guid TermId { get; set; }

    public Guid WaveId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string ObjectType { get; set; } = string.Empty;

    public string LegacyKey { get; set; } = string.Empty;

    public DateOnly TermEnd { get; set; }

    public string State { get; set; } = TermState.Planned;

    public string? Reason { get; set; }

    public Guid? BatchId { get; set; }

    public Guid? CoreId { get; set; }

    public Instant UpdatedAt { get; set; }
}

internal static class WaveModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<WaveRow>(e =>
        {
            e.ToTable("wave", t => t.HasCheckConstraint("ck_wave_status", "status IN ('PLANNED','IN_PROGRESS','CLOSED')"));
            e.HasKey(r => r.WaveId).HasName("pk_wave");
            e.Property(r => r.WaveId).HasColumnName("wave_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.Name).HasColumnName("name");
            e.Property(r => r.Status).HasColumnName("status");
            e.Property(r => r.SnapshotId).HasColumnName("snapshot_id");
            e.Property(r => r.Source).HasColumnName("source");
            e.Property(r => r.Snapshot).HasColumnName("snapshot");
            e.Property(r => r.ObjectType).HasColumnName("object_type");
            e.Property(r => r.TermEndField).HasColumnName("term_end_field");
            e.Property(r => r.WindowFrom).HasColumnName("window_from").HasColumnType("date");
            e.Property(r => r.WindowTo).HasColumnName("window_to").HasColumnType("date");
            e.Property(r => r.Scope).HasColumnName("scope");
            e.Property(r => r.SelectedCount).HasColumnName("selected_count");
            e.Property(r => r.CreatedBy).HasColumnName("created_by");
            e.Property(r => r.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(r => r.ClosedBy).HasColumnName("closed_by");
            e.Property(r => r.ClosedAt).HasColumnName("closed_at").HasColumnType("timestamptz");
            e.Property(r => r.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            e.HasIndex(r => new { r.LegalEntityId, r.Name }).IsUnique().HasDatabaseName("ux_wave_name");
        });

        model.Entity<WaveTermRow>(e =>
        {
            e.ToTable("wave_term", t => t.HasCheckConstraint("ck_wave_term_state", "state IN ('PLANNED','OFFERED','CONVERTED','EXCLUDED','WITHDRAWN','NOT_CONVERTED')"));
            e.HasKey(r => r.TermId).HasName("pk_wave_term");
            e.Property(r => r.TermId).HasColumnName("term_id");
            e.Property(r => r.WaveId).HasColumnName("wave_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.ObjectType).HasColumnName("object_type");
            e.Property(r => r.LegacyKey).HasColumnName("legacy_key");
            e.Property(r => r.TermEnd).HasColumnName("term_end").HasColumnType("date");
            e.Property(r => r.State).HasColumnName("state");
            e.Property(r => r.Reason).HasColumnName("reason");
            e.Property(r => r.BatchId).HasColumnName("batch_id");
            e.Property(r => r.CoreId).HasColumnName("core_id");
            e.Property(r => r.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.HasIndex(r => new { r.WaveId, r.LegacyKey }).IsUnique().HasDatabaseName("ux_wave_term_wave_key");
            e.HasIndex(r => new { r.LegalEntityId, r.ObjectType, r.LegacyKey }).IsUnique().HasDatabaseName("ux_wave_term_claim")
                .HasFilter("state IN ('PLANNED','OFFERED','CONVERTED')");
            e.HasIndex(r => r.BatchId).HasDatabaseName("ix_wave_term_batch");
            e.HasOne<WaveRow>().WithMany().HasForeignKey(r => r.WaveId).HasConstraintName("fk_wave_term_wave");
        });
    }
}
