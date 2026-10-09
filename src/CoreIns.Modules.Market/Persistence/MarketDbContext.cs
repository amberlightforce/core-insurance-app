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

    public DbSet<PackVersionRow> PackVersions => Set<PackVersionRow>();

    public DbSet<ConfigStateRow> ConfigStates => Set<ConfigStateRow>();

    public DbSet<PackActivationRow> PackActivations => Set<PackActivationRow>();

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

        // Append-only registry of pack versions (D-SL5-06/07): the database refuses UPDATE, DELETE and TRUNCATE for every role (MarketStateSql).
        modelBuilder.Entity<PackVersionRow>(entity =>
        {
            entity.ToTable("pack_version", table =>
            {
                table.HasCheckConstraint("ck_pack_version_status", "status IN ('Built', 'Signed', 'Certified', 'Rejected', 'Published', 'Deprecated', 'Removed')");
                table.HasCheckConstraint("ck_pack_version_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_pack_version_semver", "version ~ '^[0-9]+[.][0-9]+[.][0-9]+$'");
            });
            entity.HasKey(e => new { e.PackId, e.Version }).HasName("pk_pack_version");
            entity.Property(e => e.PackId).HasColumnName("pack_id").HasMaxLength(32);
            entity.Property(e => e.Version).HasColumnName("version").HasMaxLength(32);
            entity.Property(e => e.Country).HasColumnName("country").HasColumnType("char(2)");
            entity.Property(e => e.ContentDigest).HasColumnName("content_digest").HasColumnType("char(64)");
            entity.Property(e => e.Values).HasColumnName("values").HasColumnType("jsonb");
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(16);
            entity.Property(e => e.RegisteredAt).HasColumnName("registered_at").HasColumnType("timestamptz");
        });

        // Append-only hash chain of configuration states (D-SL5-06): a linear history, never rewritten.
        modelBuilder.Entity<ConfigStateRow>(entity =>
        {
            entity.ToTable("config_state", table =>
            {
                table.HasCheckConstraint("ck_config_state_cause", "cause IN ('GENESIS', 'PACK_ACTIVATION', 'PACK_ROLLBACK')");
                table.HasCheckConstraint("ck_config_state_hash", "hash ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_config_state_parent", "(cause = 'GENESIS') = (parent_hash IS NULL)");
            });
            entity.HasKey(e => e.Hash).HasName("pk_config_state");
            entity.Property(e => e.Hash).HasColumnName("hash").HasColumnType("char(64)");
            entity.Property(e => e.Seq).HasColumnName("seq").ValueGeneratedOnAdd().UseIdentityAlwaysColumn();
            entity.Property(e => e.ParentHash).HasColumnName("parent_hash").HasColumnType("char(64)");
            entity.Property(e => e.Manifest).HasColumnName("manifest").HasColumnType("jsonb");
            entity.Property(e => e.ActivatedAt).HasColumnName("activated_at").HasColumnType("timestamptz");
            entity.Property(e => e.Cause).HasColumnName("cause").HasMaxLength(16);
            entity.Property(e => e.CauseRef).HasColumnName("cause_ref");
            entity.HasIndex(e => e.Seq).IsUnique().HasDatabaseName("ux_config_state_seq");
            entity.HasIndex(e => e.ParentHash).IsUnique().HasDatabaseName("ux_config_state_parent");
            entity.HasOne<ConfigStateRow>().WithMany().HasForeignKey(e => e.ParentHash).HasPrincipalKey(e => e.Hash).HasConstraintName("fk_config_state_parent");
        });

        // Activation requests and decisions (PRD-17 section 7.1 PackActivation). SL5-MKT-ROLLBACK writes the ROLLBACK rows and moves the status.
        modelBuilder.Entity<PackActivationRow>(entity =>
        {
            entity.ToTable("pack_activation", table =>
            {
                table.HasCheckConstraint("ck_pack_activation_kind", "kind IN ('ACTIVATE', 'ROLLBACK')");
                table.HasCheckConstraint("ck_pack_activation_status", "status IN ('PENDING_APPROVAL', 'APPROVED', 'REJECTED', 'WITHDRAWN', 'ACTIVE', 'SUPERSEDED')");
            });
            entity.HasKey(e => e.Id).HasName("pk_pack_activation");
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.PackId).HasColumnName("pack_id").HasMaxLength(32);
            entity.Property(e => e.Version).HasColumnName("version").HasMaxLength(32);
            entity.Property(e => e.Kind).HasColumnName("kind").HasMaxLength(16);
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(24);
            entity.Property(e => e.RequestedBy).HasColumnName("requested_by").HasMaxLength(200);
            entity.Property(e => e.DecidedBy).HasColumnName("decided_by").HasMaxLength(200);
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.ActivatedAt).HasColumnName("activated_at").HasColumnType("timestamptz");
            entity.Property(e => e.ResultingHash).HasColumnName("resulting_hash").HasColumnType("char(64)");
            entity.Property(e => e.SupersedesId).HasColumnName("supersedes_id");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.LegalEntityId, e.PackId, e.ActivatedAt }).HasDatabaseName("ix_pack_activation_entity_pack");
            entity.HasOne<LegalEntityRow>().WithMany().HasForeignKey(e => e.LegalEntityId).HasConstraintName("fk_pack_activation_legal_entity").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PackVersionRow>().WithMany().HasForeignKey(e => new { e.PackId, e.Version }).HasConstraintName("fk_pack_activation_pack_version").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ConfigStateRow>().WithMany().HasForeignKey(e => e.ResultingHash).HasPrincipalKey(e => e.Hash).HasConstraintName("fk_pack_activation_state");
            entity.HasOne<PackActivationRow>().WithMany().HasForeignKey(e => e.SupersedesId).HasConstraintName("fk_pack_activation_supersedes");
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

/// <summary><c>mkt.pack_version</c>: one shipped version of a pack's data, registered once and never changed (D-SL5-07). The core defaults are registered as pack <c>core</c>.</summary>
internal sealed class PackVersionRow
{
    public string PackId { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <summary>Country whose L3 node the values populate; null for the core defaults.</summary>
    public string? Country { get; set; }

    /// <summary>SHA-256 over the canonical JSON of the values.</summary>
    public string ContentDigest { get; set; } = string.Empty;

    /// <summary>The values as JSON (jsonb).</summary>
    public string Values { get; set; } = "[]";

    public string Status { get; set; } = string.Empty;

    public Instant RegisteredAt { get; set; }
}

/// <summary><c>mkt.config_state</c>: one configuration state, keyed by the hash of its manifest (REQ-MKT-046). Append-only.</summary>
internal sealed class ConfigStateRow
{
    public string Hash { get; set; } = string.Empty;

    /// <summary>Insertion order of the chain (identity); the current state is the highest.</summary>
    public long Seq { get; set; }

    public string? ParentHash { get; set; }

    /// <summary>The manifest as JSON (jsonb): {packVersions, coreDigest, parentHash, cause}.</summary>
    public string Manifest { get; set; } = "{}";

    public Instant ActivatedAt { get; set; }

    public string Cause { get; set; } = string.Empty;

    /// <summary>The activation that caused the state; null for genesis.</summary>
    public Guid? CauseRef { get; set; }
}

/// <summary><c>mkt.pack_activation</c>: a request to activate or roll back a pack version for a legal entity, and its decision.</summary>
internal sealed class PackActivationRow
{
    public Guid Id { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string PackId { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string RequestedBy { get; set; } = string.Empty;

    public string? DecidedBy { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    public Instant? ActivatedAt { get; set; }

    public string? ResultingHash { get; set; }

    public Guid? SupersedesId { get; set; }

    public Instant CreatedAt { get; set; }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add … --project src/CoreIns.Modules.Market</c> (no connection opened).</summary>
internal sealed class MarketDbContextDesignTimeFactory : IDesignTimeDbContextFactory<MarketDbContext>
{
    public MarketDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<MarketDbContext>("Host=localhost;Database=coreins_design;Username=design", MarketModule.Schema));
}
