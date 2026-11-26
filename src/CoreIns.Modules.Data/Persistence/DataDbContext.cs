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
    }
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
