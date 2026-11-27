using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CoreIns.Modules.Migration.Persistence;

/// <summary>The Migration module's EF Core context (schema mig): xref registry, batches, batch items, coexistence routing.</summary>
internal sealed class MigrationDbContext(DbContextOptions<MigrationDbContext> options) : ModuleDbContext(options)
{
    public DbSet<BatchRow> Batches => Set<BatchRow>();

    public DbSet<BatchItemRow> Items => Set<BatchItemRow>();

    public DbSet<XrefRow> Xrefs => Set<XrefRow>();

    public DbSet<RoutingRow> Routes => Set<RoutingRow>();

    public DbSet<Services.Reconciliation.ControlTotalRow> ControlTotals => Set<Services.Reconciliation.ControlTotalRow>();

    public DbSet<Services.Reconciliation.ReconResultRow> ReconResults => Set<Services.Reconciliation.ReconResultRow>();

    public DbSet<Services.SignOff.SignOffRow> SignOffs => Set<Services.SignOff.SignOffRow>();

    public DbSet<Services.SignOff.SignOffSignatoryRow> SignOffSignatories => Set<Services.SignOff.SignOffSignatoryRow>();

    public DbSet<Services.SignOff.SignOffWaiverRow> SignOffWaivers => Set<Services.SignOff.SignOffWaiverRow>();

    public DbSet<Services.Staging.StagingSnapshotRow> StagingSnapshots => Set<Services.Staging.StagingSnapshotRow>();

    public DbSet<Services.Staging.StagingRecordRow> StagingRecords => Set<Services.Staging.StagingRecordRow>();

    protected override string Schema => MigrationModule.Schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.MapDsarRestrictions();
        Services.Reconciliation.ReconciliationModel.Configure(modelBuilder);
        Services.SignOff.SignOffModel.Configure(modelBuilder);
        Services.Staging.StagingModel.Configure(modelBuilder);

        modelBuilder.Entity<BatchRow>(entity =>
        {
            entity.ToTable("batch");
            entity.HasKey(e => e.BatchId).HasName("pk_batch");
            entity.Property(e => e.BatchId).HasColumnName("batch_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.PausedFrom).HasColumnName("paused_from");
            entity.Property(e => e.BaselineHash).HasColumnName("baseline_hash");
            entity.Property(e => e.ContentHash).HasColumnName("content_hash");
            entity.Property(e => e.DryRunHash).HasColumnName("dry_run_hash");
            entity.Property(e => e.DryRunAt).HasColumnName("dry_run_at").HasColumnType("timestamptz");
            entity.Property(e => e.StartedBy).HasColumnName("started_by");
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.Cursor).HasColumnName("cursor").HasDefaultValue(0);
            entity.Property(e => e.ProcessedCount).HasColumnName("processed_count").HasDefaultValue(0);
            entity.Property(e => e.FailedCount).HasColumnName("failed_count").HasDefaultValue(0);
            entity.Property(e => e.LeaseUntil).HasColumnName("lease_until").HasColumnType("timestamptz");
            entity.HasIndex(e => e.Status).HasDatabaseName("ix_batch_status");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
        });

        modelBuilder.Entity<BatchItemRow>(entity =>
        {
            entity.ToTable("batch_item");
            entity.HasKey(e => e.ItemId).HasName("pk_batch_item");
            entity.Property(e => e.ItemId).HasColumnName("item_id");
            entity.Property(e => e.BatchId).HasColumnName("batch_id");
            entity.Property(e => e.Seq).HasColumnName("seq");
            entity.Property(e => e.ModuleName).HasColumnName("module");
            entity.Property(e => e.ObjectType).HasColumnName("object_type");
            entity.Property(e => e.LegacyId).HasColumnName("legacy_id");
            entity.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb");
            entity.Property(e => e.Outcome).HasColumnName("outcome");
            entity.Property(e => e.CoreId).HasColumnName("core_id");
            entity.Property(e => e.ErrorCode).HasColumnName("error_code");
            entity.Property(e => e.Message).HasColumnName("message");
            entity.HasIndex(e => new { e.BatchId, e.Seq }).IsUnique().HasDatabaseName("ux_batch_item_seq");
            entity.HasIndex(e => new { e.BatchId, e.ModuleName, e.ObjectType, e.LegacyId }).IsUnique().HasDatabaseName("ux_batch_item_legacy");
        });

        modelBuilder.Entity<XrefRow>(entity =>
        {
            entity.ToTable("xref", table => table.HasCheckConstraint("ck_xref_status", "status IN ('ACTIVE','REVERSED')"));
            entity.HasKey(e => e.XrefId).HasName("pk_xref");
            entity.Property(e => e.XrefId).HasColumnName("xref_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Source).HasColumnName("source");
            entity.Property(e => e.ObjectType).HasColumnName("object_type");
            entity.Property(e => e.LegacyKey).HasColumnName("legacy_key");
            entity.Property(e => e.CoreId).HasColumnName("core_id");
            entity.Property(e => e.BatchId).HasColumnName("batch_id");
            entity.HasIndex(e => e.BatchId).HasDatabaseName("ix_xref_batch");
            entity.Property(e => e.Origin).HasColumnName("origin");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.LegalEntityId, e.Source, e.ObjectType, e.LegacyKey }).IsUnique().HasDatabaseName("ux_xref_legacy");
            entity.HasIndex(e => new { e.LegalEntityId, e.ObjectType, e.CoreId }).HasDatabaseName("ix_xref_core");
        });

        modelBuilder.Entity<RoutingRow>(entity =>
        {
            entity.ToTable("routing", table =>
            {
                table.HasCheckConstraint("ck_routing_state", "master_state IN ('NONE','TRANSITIONING','CORE')");
                table.HasCheckConstraint("ck_routing_period", "valid_to IS NULL OR valid_to > valid_from");
            });
            entity.HasKey(e => e.RouteId).HasName("pk_routing");
            entity.Property(e => e.RouteId).HasColumnName("route_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.ObjectType).HasColumnName("object_type");
            entity.Property(e => e.Key).HasColumnName("key");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.ValidTo).HasColumnName("valid_to");
            entity.Property(e => e.MasterState).HasColumnName("master_state");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.LegalEntityId, e.ObjectType, e.Key, e.ValidFrom }).IsUnique().HasDatabaseName("ux_routing_from");
        });
    }
}

/// <summary>A migration batch.</summary>
internal sealed class BatchRow
{
    public Guid BatchId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? PausedFrom { get; set; }

    public string? BaselineHash { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    public string? DryRunHash { get; set; }

    public Instant? DryRunAt { get; set; }

    public string? StartedBy { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    /// <summary>Seq of the last item the worker processed (BO-MIG-01).</summary>
    public int Cursor { get; set; }

    /// <summary>Items with an outcome other than PENDING.</summary>
    public int ProcessedCount { get; set; }

    /// <summary>Items with outcome FAILED.</summary>
    public int FailedCount { get; set; }

    /// <summary>Until when a worker owns the run; an expired lease is taken over (crash recovery).</summary>
    public Instant? LeaseUntil { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }

    public int RecordVersion { get; set; }
}

/// <summary>One legacy object of a batch.</summary>
internal sealed class BatchItemRow
{
    public Guid ItemId { get; set; }

    public Guid BatchId { get; set; }

    public int Seq { get; set; }

    public string ModuleName { get; set; } = string.Empty;

    public string ObjectType { get; set; } = string.Empty;

    public string LegacyId { get; set; } = string.Empty;

    public string Payload { get; set; } = "null";

    public string Outcome { get; set; } = string.Empty;

    public Guid? CoreId { get; set; }

    public string? ErrorCode { get; set; }

    public string? Message { get; set; }
}

/// <summary>A legacy key to core id link (REQ-MIG-002).</summary>
internal sealed class XrefRow
{
    public Guid XrefId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Source { get; set; } = string.Empty;

    public string ObjectType { get; set; } = string.Empty;

    public string LegacyKey { get; set; } = string.Empty;

    public Guid CoreId { get; set; }

    public Guid? BatchId { get; set; }

    public string Origin { get; set; } = "MIGRATION";

    public string Status { get; set; } = "ACTIVE";

    public Instant CreatedAt { get; set; }
}

/// <summary>A coexistence period: who is master of one object between two dates (valid_to exclusive, null = open).</summary>
internal sealed class RoutingRow
{
    public Guid RouteId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string ObjectType { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public BusinessDate ValidFrom { get; set; }

    public BusinessDate? ValidTo { get; set; }

    public string MasterState { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string CreatedBy { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }
}

/// <summary>Design-time factory for dotnet ef migrations add (project src/CoreIns.Modules.Migration).</summary>
internal sealed class MigrationDbContextDesignTimeFactory : IDesignTimeDbContextFactory<MigrationDbContext>
{
    public MigrationDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<MigrationDbContext>("Host=localhost;Database=coreins_design;Username=design", MigrationModule.Schema));
}
