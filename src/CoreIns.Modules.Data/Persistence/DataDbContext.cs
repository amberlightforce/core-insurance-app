using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CoreIns.Modules.Data.Persistence;

/// <summary>
/// The Data module's EF Core context (schema <c>dat</c>). SL6-DAT-CORE builds only the subject-key / crypto-shred tables,
/// the erasure idempotency ledger, the minimal model registry and bias reports. No lakehouse, marts or analytics.
/// </summary>
internal sealed class DataDbContext(DbContextOptions<DataDbContext> options) : ModuleDbContext(options)
{
    public DbSet<SubjectKeyRow> SubjectKeys => Set<SubjectKeyRow>();

    public DbSet<SubjectFactRow> SubjectFacts => Set<SubjectFactRow>();

    public DbSet<ErasureRequestRow> ErasureRequests => Set<ErasureRequestRow>();

    public DbSet<ModelRow> Models => Set<ModelRow>();

    public DbSet<BiasReportRow> BiasReports => Set<BiasReportRow>();

    public DbSet<DataContractRow> DataContracts => Set<DataContractRow>();

    public DbSet<EventLedgerRow> EventLedger => Set<EventLedgerRow>();

    public DbSet<QuarantineRow> Quarantine => Set<QuarantineRow>();

    protected override string Schema => DataModule.Schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SubjectKeyRow>(e =>
        {
            e.ToTable("subject_key", t => t.HasCheckConstraint("ck_subject_key_destroyed", "destroyed_at IS NULL OR key_material IS NULL"));
            e.HasKey(x => x.SubjectToken).HasName("pk_subject_key");
            e.Property(x => x.SubjectToken).HasColumnName("subject_token");
            e.Property(x => x.KeyId).HasColumnName("key_id");
            // Wrapped under the platform KEK (SubjectKeyProtector); a legacy raw 32-byte value stays readable and is re-wrapped on the next fact write.
            e.Property(x => x.KeyMaterial).HasColumnName("key_material");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.DestroyedAt).HasColumnName("destroyed_at").HasColumnType("timestamptz");
            e.Property(x => x.RestrictedAt).HasColumnName("restricted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<SubjectFactRow>(e =>
        {
            e.ToTable("subject_fact");
            e.HasKey(x => x.FactId).HasName("pk_subject_fact");
            e.Property(x => x.FactId).HasColumnName("fact_id");
            e.Property(x => x.SubjectToken).HasColumnName("subject_token");
            e.Property(x => x.Category).HasColumnName("category");
            e.Property(x => x.FactKey).HasColumnName("fact_key");
            e.Property(x => x.Cipher).HasColumnName("cipher");
            e.Property(x => x.Blanked).HasColumnName("blanked");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.HasIndex(x => x.SubjectToken).HasDatabaseName("ix_subject_fact_subject");
        });

        modelBuilder.Entity<ErasureRequestRow>(e =>
        {
            e.ToTable("erasure_request");
            e.HasKey(x => x.RequestId).HasName("pk_erasure_request");
            e.Property(x => x.RequestId).HasColumnName("request_id");
            e.Property(x => x.SubjectToken).HasColumnName("subject_token");
            e.Property(x => x.DsarRef).HasColumnName("dsar_ref");
            e.Property(x => x.Result).HasColumnName("result").HasColumnType("jsonb");
            e.Property(x => x.ExecutedAt).HasColumnName("executed_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<ModelRow>(e =>
        {
            e.ToTable("model", t => t.HasCheckConstraint("ck_model_status", "status IN ('ACTIVE','SUSPENDED')"));
            e.HasKey(x => x.ModelId).HasName("pk_model");
            e.Property(x => x.ModelId).HasColumnName("model_id");
            e.Property(x => x.FeatureKey).HasColumnName("feature_key");
            e.Property(x => x.Version).HasColumnName("version");
            e.Property(x => x.Card).HasColumnName("card").HasColumnType("jsonb");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.SuspendedReason).HasColumnName("suspended_reason");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.HasIndex(x => new { x.FeatureKey, x.Version }).IsUnique().HasDatabaseName("ux_model_feature_version");
        });

        modelBuilder.Entity<BiasReportRow>(e =>
        {
            e.ToTable("bias_report");
            e.HasKey(x => x.BiasReportId).HasName("pk_bias_report");
            e.Property(x => x.BiasReportId).HasColumnName("bias_report_id");
            e.Property(x => x.ModelId).HasColumnName("model_id");
            e.Property(x => x.FeatureKey).HasColumnName("feature_key");
            e.Property(x => x.Metric).HasColumnName("metric");
            e.Property(x => x.ProxyGroup).HasColumnName("proxy_group");
            e.Property(x => x.Ratio).HasColumnName("ratio").HasColumnType("numeric(12,6)");
            e.Property(x => x.SampleSize).HasColumnName("sample_size");
            e.Property(x => x.Breached).HasColumnName("breached");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<DataContractRow>(e =>
        {
            e.ToTable("data_contract");
            e.HasKey(x => x.ContractId).HasName("pk_data_contract");
            e.Property(x => x.ContractId).HasColumnName("contract_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Major).HasColumnName("major");
            e.Property(x => x.Minor).HasColumnName("minor");
            e.Property(x => x.Producer).HasColumnName("producer");
            e.Property(x => x.Document).HasColumnName("document").HasColumnType("jsonb");
            e.Property(x => x.Compatibility).HasColumnName("compatibility").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.HasIndex(x => new { x.Name, x.Major, x.Minor }).IsUnique().HasDatabaseName("ux_data_contract_version");
        });

        // rpt_raw (bronze) layer: the append-only ledger of ingested events and the quarantine of refused ones.
        modelBuilder.Entity<EventLedgerRow>(e =>
        {
            e.ToTable("event_ledger", DataModule.ReportingRawSchema);
            e.HasKey(x => x.EventId).HasName("pk_event_ledger");
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.RegistryName).HasColumnName("registry_name");
            e.Property(x => x.EventType).HasColumnName("event_type");
            e.Property(x => x.Producer).HasColumnName("producer");
            e.Property(x => x.SchemaVersion).HasColumnName("schema_version");
            e.Property(x => x.AggregateType).HasColumnName("aggregate_type");
            e.Property(x => x.AggregateId).HasColumnName("aggregate_id");
            e.Property(x => x.AggregateSequence).HasColumnName("aggregate_sequence");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz");
            e.Property(x => x.LegalEntity).HasColumnName("legal_entity");
            e.Property(x => x.Origin).HasColumnName("origin");
            e.Property(x => x.Envelope).HasColumnName("envelope").HasColumnType("jsonb");
            e.Property(x => x.ReceivedAt).HasColumnName("received_at").HasColumnType("timestamptz");
            e.HasIndex(x => new { x.RegistryName, x.OccurredAt }).HasDatabaseName("ix_event_ledger_registry_occurred");
        });

        modelBuilder.Entity<QuarantineRow>(e =>
        {
            e.ToTable("quarantine", DataModule.ReportingRawSchema, t => t.HasCheckConstraint("ck_quarantine_status", "status IN ('QUARANTINED','RELEASED')"));
            e.HasKey(x => x.QuarantineId).HasName("pk_quarantine");
            e.Property(x => x.QuarantineId).HasColumnName("quarantine_id");
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.RegistryName).HasColumnName("registry_name");
            e.Property(x => x.Reasons).HasColumnName("reasons").HasColumnType("jsonb");
            e.Property(x => x.Envelope).HasColumnName("envelope").HasColumnType("jsonb");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.ReceivedAt).HasColumnName("received_at").HasColumnType("timestamptz");
            e.Property(x => x.ReleasedAt).HasColumnName("released_at").HasColumnType("timestamptz");
            e.HasIndex(x => x.EventId).IsUnique().HasDatabaseName("ux_quarantine_event");
            e.HasIndex(x => new { x.Status, x.RegistryName }).HasDatabaseName("ix_quarantine_status");
        });
    }
}

/// <summary>One registered version of a data contract (the payload shape DAT expects of an event type).</summary>
internal sealed class DataContractRow
{
    public Guid ContractId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Major { get; set; }

    public int Minor { get; set; }

    public string Producer { get; set; } = string.Empty;

    public string Document { get; set; } = "{}";

    public string Compatibility { get; set; } = "{}";

    public Instant CreatedAt { get; set; }
}

/// <summary>An event accepted into the raw layer; <c>event_id</c> is unique so redelivery is a no-op.</summary>
internal sealed class EventLedgerRow
{
    public Guid EventId { get; set; }

    public string RegistryName { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public string Producer { get; set; } = string.Empty;

    public string SchemaVersion { get; set; } = string.Empty;

    public string AggregateType { get; set; } = string.Empty;

    public string AggregateId { get; set; } = string.Empty;

    public long AggregateSequence { get; set; }

    public Instant OccurredAt { get; set; }

    public string LegalEntity { get; set; } = string.Empty;

    public string Origin { get; set; } = string.Empty;

    public string Envelope { get; set; } = "{}";

    public Instant ReceivedAt { get; set; }
}

/// <summary>An event refused by validation, kept with its reasons until released.</summary>
internal sealed class QuarantineRow
{
    public Guid QuarantineId { get; set; }

    public Guid EventId { get; set; }

    public string RegistryName { get; set; } = string.Empty;

    public string Reasons { get; set; } = "[]";

    public string Envelope { get; set; } = "{}";

    public string Status { get; set; } = "QUARANTINED";

    public Instant ReceivedAt { get; set; }

    public Instant? ReleasedAt { get; set; }
}

/// <summary>Per-subject data key (crypto-shred): destroying the key makes the encrypted facts unreadable.</summary>
internal sealed class SubjectKeyRow
{
    public string SubjectToken { get; set; } = string.Empty;

    public Guid KeyId { get; set; }

    public byte[]? KeyMaterial { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant? DestroyedAt { get; set; }

    public Instant? RestrictedAt { get; set; }
}

/// <summary>A fact held about a subject, encrypted under the subject key; blanked on erasure.</summary>
internal sealed class SubjectFactRow
{
    public Guid FactId { get; set; }

    public string SubjectToken { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string FactKey { get; set; } = string.Empty;

    public byte[]? Cipher { get; set; }

    public bool Blanked { get; set; }

    public Instant CreatedAt { get; set; }
}

/// <summary>Erasure idempotency ledger: the stored result is returned on every repeat of a request id.</summary>
internal sealed class ErasureRequestRow
{
    public string RequestId { get; set; } = string.Empty;

    public string SubjectToken { get; set; } = string.Empty;

    public Guid? DsarRef { get; set; }

    public string Result { get; set; } = "{}";

    public Instant ExecutedAt { get; set; }
}

/// <summary>Minimal model registry row.</summary>
internal sealed class ModelRow
{
    public Guid ModelId { get; set; }

    public string FeatureKey { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Card { get; set; } = "{}";

    public string Status { get; set; } = "ACTIVE";

    public string? SuspendedReason { get; set; }

    public Instant CreatedAt { get; set; }
}

/// <summary>The outcome of one bias test run.</summary>
internal sealed class BiasReportRow
{
    public Guid BiasReportId { get; set; }

    public Guid? ModelId { get; set; }

    public string FeatureKey { get; set; } = string.Empty;

    public string Metric { get; set; } = string.Empty;

    public string ProxyGroup { get; set; } = string.Empty;

    public decimal Ratio { get; set; }

    public int SampleSize { get; set; }

    public bool Breached { get; set; }

    public Instant CreatedAt { get; set; }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> on this project.</summary>
internal sealed class DataDbContextDesignTimeFactory : IDesignTimeDbContextFactory<DataDbContext>
{
    public DataDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<DataDbContext>("Host=localhost;Database=coreins_design;Username=design", DataModule.Schema));
}
