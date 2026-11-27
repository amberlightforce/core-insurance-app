using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Migration.Services.Reconciliation;

/// <summary><c>mig.control_total</c>: one legacy control total of a scope. Reloading a scope updates the rows (no deletes).</summary>
internal sealed class ControlTotalRow
{
    public Guid ControlTotalId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Scope { get; set; } = string.Empty;

    public string ModuleName { get; set; } = string.Empty;

    public string ObjectType { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    public long Count { get; set; }

    public decimal Amount { get; set; }

    /// <summary>False once a reload no longer lists the figure: it stays as history but is not compared.</summary>
    public bool Active { get; set; } = true;

    public string LoadedBy { get; set; } = string.Empty;

    public Instant LoadedAt { get; set; }
}

/// <summary><c>mig.recon_result</c>: one compared figure of one run. Insert-only.</summary>
internal sealed class ReconResultRow
{
    public Guid ResultId { get; set; }

    public Guid RunId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Scope { get; set; } = string.Empty;

    public Guid? BatchId { get; set; }

    public string ModuleName { get; set; } = string.Empty;

    public string ObjectType { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    public long LegacyCount { get; set; }

    public long CoreCount { get; set; }

    public decimal LegacyAmount { get; set; }

    public decimal CoreAmount { get; set; }

    public string Status { get; set; } = string.Empty;

    public string RunBy { get; set; } = string.Empty;

    public Instant RunAt { get; set; }
}

internal static class ReconciliationModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<ControlTotalRow>(e =>
        {
            e.ToTable("control_total");
            e.HasKey(r => r.ControlTotalId).HasName("pk_control_total");
            e.Property(r => r.ControlTotalId).HasColumnName("control_total_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.Scope).HasColumnName("scope");
            e.Property(r => r.ModuleName).HasColumnName("module");
            e.Property(r => r.ObjectType).HasColumnName("object_type");
            e.Property(r => r.Key).HasColumnName("key");
            e.Property(r => r.Currency).HasColumnName("currency");
            e.Property(r => r.Count).HasColumnName("count");
            e.Property(r => r.Amount).HasColumnName("amount").HasColumnType("numeric(19,4)");
            e.Property(r => r.Active).HasColumnName("active");
            e.Property(r => r.LoadedBy).HasColumnName("loaded_by");
            e.Property(r => r.LoadedAt).HasColumnName("loaded_at").HasColumnType("timestamptz");
            e.HasIndex(r => new { r.LegalEntityId, r.Scope, r.ModuleName, r.ObjectType, r.Key, r.Currency }).IsUnique().HasDatabaseName("ux_control_total_key");
        });

        model.Entity<ReconResultRow>(e =>
        {
            e.ToTable("recon_result");
            e.HasKey(r => r.ResultId).HasName("pk_recon_result");
            e.Property(r => r.ResultId).HasColumnName("result_id");
            e.Property(r => r.RunId).HasColumnName("run_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.Scope).HasColumnName("scope");
            e.Property(r => r.BatchId).HasColumnName("batch_id");
            e.Property(r => r.ModuleName).HasColumnName("module");
            e.Property(r => r.ObjectType).HasColumnName("object_type");
            e.Property(r => r.Key).HasColumnName("key");
            e.Property(r => r.Currency).HasColumnName("currency");
            e.Property(r => r.LegacyCount).HasColumnName("legacy_count");
            e.Property(r => r.CoreCount).HasColumnName("core_count");
            e.Property(r => r.LegacyAmount).HasColumnName("legacy_amount").HasColumnType("numeric(19,4)");
            e.Property(r => r.CoreAmount).HasColumnName("core_amount").HasColumnType("numeric(19,4)");
            e.Property(r => r.Status).HasColumnName("status");
            e.Property(r => r.RunBy).HasColumnName("run_by");
            e.Property(r => r.RunAt).HasColumnName("run_at").HasColumnType("timestamptz");
            e.HasIndex(r => new { r.LegalEntityId, r.RunId }).HasDatabaseName("ix_recon_result_run");
            e.HasIndex(r => new { r.LegalEntityId, r.Scope, r.RunAt }).HasDatabaseName("ix_recon_result_scope");
            e.ToTable(t => t.HasCheckConstraint("ck_recon_result_status", "status IN ('MATCH','VARIANCE','MISSING_CORE','MISSING_LEGACY')"));
        });
    }
}
