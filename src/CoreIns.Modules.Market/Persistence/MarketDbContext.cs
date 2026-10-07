using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CoreIns.Modules.Market.Persistence;

/// <summary>
/// The Market module's EF Core context (schema <c>mkt</c>). The slice holds the legal-entity registry (REQ-MKT-151);
/// configuration values and pack data are code-declared pack data in this wave (REQ-MKT-126 data-only packs), the
/// persisted configuration store arrives with W1-MKT-01. Internal: no other module may use it.
/// </summary>
internal sealed class MarketDbContext(DbContextOptions<MarketDbContext> options) : ModuleDbContext(options)
{
    public DbSet<LegalEntityRow> LegalEntities => Set<LegalEntityRow>();

    protected override string Schema => MarketModule.Schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<LegalEntityRow>(entity =>
        {
            entity.ToTable("legal_entity", table =>
            {
                table.HasCheckConstraint("ck_legal_entity_status", "status IN ('PLANNED', 'ONBOARDING', 'ACTIVE', 'RUN_OFF', 'CLOSED')");
                table.HasCheckConstraint("ck_legal_entity_home_jurisdiction", "home_jurisdiction ~ '^[A-Z]{2}$'");
                table.HasCheckConstraint("ck_legal_entity_functional_currency", "functional_currency ~ '^[A-Z]{3}$'");
            });
            entity.HasKey(e => e.LegalEntityId).HasName("pk_legal_entity");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Code).HasColumnName("code").HasMaxLength(32);
            entity.Property(e => e.NativeName).HasColumnName("native_name").HasMaxLength(200);
            entity.Property(e => e.LatinName).HasColumnName("latin_name").HasMaxLength(200);
            entity.Property(e => e.HomeJurisdiction).HasColumnName("home_jurisdiction").HasColumnType("char(2)");
            entity.Property(e => e.PackId).HasColumnName("pack_id").HasMaxLength(32);
            entity.Property(e => e.FunctionalCurrency).HasColumnName("functional_currency").HasColumnType("char(3)");
            entity.Property(e => e.TimeZone).HasColumnName("timezone").HasMaxLength(64);
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(16);
            entity.Property(e => e.IsTestEntity).HasColumnName("is_test_entity");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.HasIndex(e => e.Code).IsUnique().HasDatabaseName("ux_legal_entity_code");
        });
    }
}

/// <summary><c>mkt.legal_entity</c>: the legal-entity registry (PRD-17 section 7.1 LegalEntity, D-CON-33).</summary>
internal sealed class LegalEntityRow
{
    public LegalEntityId LegalEntityId { get; set; }

    /// <summary>Envelope and configuration form, for example <c>GR-TEST</c>.</summary>
    public string Code { get; set; } = string.Empty;

    public string NativeName { get; set; } = string.Empty;

    public string LatinName { get; set; } = string.Empty;

    public string HomeJurisdiction { get; set; } = string.Empty;

    public string PackId { get; set; } = string.Empty;

    public string FunctionalCurrency { get; set; } = string.Empty;

    public string TimeZone { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    /// <summary>A synthetic entity for tests and the local stack; never a licensed insurer.</summary>
    public bool IsTestEntity { get; set; }

    public int RecordVersion { get; set; }

    public Instant CreatedAt { get; set; }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add … --project src/CoreIns.Modules.Market</c> (no connection opened).</summary>
internal sealed class MarketDbContextDesignTimeFactory : IDesignTimeDbContextFactory<MarketDbContext>
{
    public MarketDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<MarketDbContext>("Host=localhost;Database=coreins_design;Username=design", MarketModule.Schema));
}
