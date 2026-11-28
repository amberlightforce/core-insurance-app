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

    public DbSet<PackVersionEventRow> PackVersionEvents => Set<PackVersionEventRow>();

    public DbSet<ConfigStateRow> ConfigStates => Set<ConfigStateRow>();

    public DbSet<PackActivationRow> PackActivations => Set<PackActivationRow>();

    public DbSet<ConfigChangeRequestRow> ConfigChangeRequests => Set<ConfigChangeRequestRow>();

    public DbSet<ConfigChangeItemRow> ConfigChangeItems => Set<ConfigChangeItemRow>();

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

        // Append-only lifecycle of a pack version (BO-MKT-04): pack_version itself never changes, so the current status is the newest event.
        modelBuilder.Entity<PackVersionEventRow>(entity =>
        {
            entity.ToTable("pack_version_event", table =>
            {
                table.HasCheckConstraint("ck_pack_version_event_status", "status IN ('Signed', 'Certified', 'Rejected', 'Published', 'Deprecated', 'Removed')");
            });
            entity.HasKey(e => e.Id).HasName("pk_pack_version_event");
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Seq).HasColumnName("seq").ValueGeneratedOnAdd().UseIdentityAlwaysColumn();
            entity.Property(e => e.PackId).HasColumnName("pack_id").HasMaxLength(32);
            entity.Property(e => e.Version).HasColumnName("version").HasMaxLength(32);
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(16);
            entity.Property(e => e.TargetEnvironment).HasColumnName("target_environment").HasMaxLength(16);
            entity.Property(e => e.ProductionEligible).HasColumnName("production_eligible");
            entity.Property(e => e.Detail).HasColumnName("detail").HasColumnType("jsonb");
            entity.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(128);
            entity.Property(e => e.Actor).HasColumnName("actor").HasMaxLength(128);
            entity.Property(e => e.At).HasColumnName("at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.PackId, e.Version, e.Seq }).HasDatabaseName("ix_pack_version_event_version");
            entity.HasOne<PackVersionRow>().WithMany().HasForeignKey(e => new { e.PackId, e.Version }).HasConstraintName("fk_pack_version_event_pack_version");
        });

        // Append-only hash chain of configuration states (D-SL5-06): a linear history, never rewritten.
        modelBuilder.Entity<ConfigStateRow>(entity =>
        {
            entity.ToTable("config_state", table =>
            {
                table.HasCheckConstraint("ck_config_state_cause", "cause IN ('GENESIS', 'PACK_ACTIVATION', 'PACK_ROLLBACK', 'CONFIG_CHANGE')");
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
                table.HasCheckConstraint("ck_pack_activation_status", "status IN ('REQUESTED', 'PENDING_APPROVAL', 'SCHEDULED', 'ACTIVE', 'SUPERSEDED', 'WITHDRAWN', 'REJECTED')");
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
            entity.Property(e => e.FromVersion).HasColumnName("from_version").HasMaxLength(32);
            entity.Property(e => e.ParentHash).HasColumnName("parent_hash").HasColumnType("char(64)");
            entity.Property(e => e.TargetDigest).HasColumnName("target_digest").HasColumnType("char(64)");
            entity.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(128).HasDefaultValue("");
            entity.Property(e => e.RequestedPrincipal).HasColumnName("requested_principal").HasMaxLength(200);
            entity.Property(e => e.ContentHash).HasColumnName("content_hash").HasColumnType("char(64)");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken().HasDefaultValue(1L);
            entity.Property(e => e.WindowFrom).HasColumnName("window_from").HasColumnType("timestamptz");
            entity.Property(e => e.WindowTo).HasColumnName("window_to").HasColumnType("timestamptz");
            entity.Property(e => e.HashesIssued).HasColumnName("hashes_issued").HasColumnType("text[]").HasDefaultValueSql("ARRAY[]::text[]");
            entity.Property(e => e.DecisionReason).HasColumnName("decision_reason").HasMaxLength(128);
            entity.HasIndex(e => new { e.LegalEntityId, e.PackId, e.ActivatedAt }).HasDatabaseName("ix_pack_activation_entity_pack");
            entity.HasOne<LegalEntityRow>().WithMany().HasForeignKey(e => e.LegalEntityId).HasConstraintName("fk_pack_activation_legal_entity").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PackVersionRow>().WithMany().HasForeignKey(e => new { e.PackId, e.Version }).HasConstraintName("fk_pack_activation_pack_version").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ConfigStateRow>().WithMany().HasForeignKey(e => e.ResultingHash).HasPrincipalKey(e => e.Hash).HasConstraintName("fk_pack_activation_state");
            entity.HasOne<PackActivationRow>().WithMany().HasForeignKey(e => e.SupersedesId).HasConstraintName("fk_pack_activation_supersedes");
        });

        // Configuration change requests (BO-MKT-01): maker-checker over country-layer values; an approved request is applied as an overlay pack version.
        modelBuilder.Entity<ConfigChangeRequestRow>(entity =>
        {
            entity.ToTable("config_change_request", table =>
            {
                table.HasCheckConstraint("ck_config_change_request_status", "status IN ('DRAFT', 'PENDING_APPROVAL', 'SCHEDULED', 'APPLIED', 'REJECTED', 'WITHDRAWN')");
                table.HasCheckConstraint("ck_config_change_request_cause", "cause IN ('MANUAL', 'IMPORT')");
                table.HasCheckConstraint("ck_config_change_request_base_hash", "base_hash ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(e => e.Id).HasName("pk_config_change_request");
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Country).HasColumnName("country").HasColumnType("char(2)");
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(24);
            entity.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(128);
            entity.Property(e => e.BaseHash).HasColumnName("base_hash").HasColumnType("char(64)");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by").HasMaxLength(200);
            entity.Property(e => e.CreatedPrincipal).HasColumnName("created_principal").HasMaxLength(200);
            entity.Property(e => e.DecidedBy).HasColumnName("decided_by").HasMaxLength(200);
            entity.Property(e => e.DecisionReason).HasColumnName("decision_reason").HasMaxLength(128);
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.ContentHash).HasColumnName("content_hash").HasColumnType("char(64)");
            entity.Property(e => e.ResultingHash).HasColumnName("resulting_hash").HasColumnType("char(64)");
            entity.Property(e => e.EffectiveAt).HasColumnName("effective_at").HasColumnType("timestamptz");
            entity.Property(e => e.Cause).HasColumnName("cause").HasMaxLength(16).HasDefaultValue("MANUAL");
            entity.Property(e => e.ImportDigest).HasColumnName("import_digest").HasColumnType("char(64)");
            entity.HasIndex(e => new { e.LegalEntityId, e.ImportDigest }).HasDatabaseName("ix_config_change_request_import");
            entity.HasIndex(e => new { e.Status, e.EffectiveAt }).HasDatabaseName("ix_config_change_request_due");
            entity.Property(e => e.AppliedAt).HasColumnName("applied_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.HasIndex(e => new { e.LegalEntityId, e.Status }).HasDatabaseName("ix_config_change_request_entity_status");
            entity.HasOne<LegalEntityRow>().WithMany().HasForeignKey(e => e.LegalEntityId).HasConstraintName("fk_config_change_request_legal_entity").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ConfigStateRow>().WithMany().HasForeignKey(e => e.ResultingHash).HasPrincipalKey(e => e.Hash).HasConstraintName("fk_config_change_request_state");
            entity.HasMany(e => e.Items).WithOne().HasForeignKey(i => i.RequestId).HasConstraintName("fk_config_change_item_request").OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConfigChangeItemRow>(entity =>
        {
            entity.ToTable("config_change_item");
            entity.HasKey(e => new { e.RequestId, e.LineNo }).HasName("pk_config_change_item");
            entity.Property(e => e.RequestId).HasColumnName("request_id");
            entity.Property(e => e.LineNo).HasColumnName("line_no");
            entity.Property(e => e.Key).HasColumnName("key").HasMaxLength(128);
            entity.Property(e => e.Value).HasColumnName("value");
            entity.Property(e => e.LegalStatus).HasColumnName("legal_status").HasMaxLength(24);
            entity.Property(e => e.SourceRef).HasColumnName("source_ref").HasMaxLength(512);
            entity.Property(e => e.MotorPath).HasColumnName("motor_path");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from").HasColumnType("date");
            entity.Property(e => e.ValidTo).HasColumnName("valid_to").HasColumnType("date");
            entity.Property(e => e.Note).HasColumnName("note").HasMaxLength(512);
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

/// <summary><c>mkt.pack_version_event</c>: one lifecycle step of a pack version (certify, publish, deprecate). Append-only; the newest row is the current status.</summary>
internal sealed class PackVersionEventRow
{
    public Guid Id { get; set; }

    public long Seq { get; set; }

    public string PackId { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    /// <summary>PRODUCTION or NON_PRODUCTION: the target the step was taken for (null never).</summary>
    public string TargetEnvironment { get; set; } = string.Empty;

    /// <summary>True when every value was Settled or NotRegulatory and the pack is not synthetic, at the time of the step.</summary>
    public bool ProductionEligible { get; set; }

    /// <summary>The golden-suite result, the unverified keys and the opt-in, as JSON.</summary>
    public string Detail { get; set; } = "{}";

    public string Reason { get; set; } = string.Empty;

    public string Actor { get; set; } = string.Empty;

    public Instant At { get; set; }
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
    public string? FromVersion { get; set; }

    public string? ParentHash { get; set; }

    public string? TargetDigest { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string? RequestedPrincipal { get; set; }

    public string? ContentHash { get; set; }

    public long RecordVersion { get; set; } = 1;

    public Instant? WindowFrom { get; set; }

    public Instant? WindowTo { get; set; }

    public string[] HashesIssued { get; set; } = [];

    public string? DecisionReason { get; set; }

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

/// <summary><c>mkt.config_change_request</c>: a maker-checker change of country-layer configuration values (BO-MKT-01).</summary>
internal sealed class ConfigChangeRequestRow
{
    public Guid Id { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Country { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    /// <summary>The configuration state the request was written against.</summary>
    public string BaseHash { get; set; } = string.Empty;

    public string CreatedBy { get; set; } = string.Empty;

    public string? CreatedPrincipal { get; set; }

    public string? DecidedBy { get; set; }

    public string? DecisionReason { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    /// <summary>Hash frozen at submit; the PLT approval is bound to it.</summary>
    public string? ContentHash { get; set; }

    public string? ResultingHash { get; set; }

    /// <summary>When a SCHEDULED change takes effect; part of the approved content (BO-MKT-02).</summary>
    public Instant? EffectiveAt { get; set; }

    /// <summary>MANUAL or IMPORT.</summary>
    public string Cause { get; set; } = "MANUAL";

    /// <summary>Digest of the imported content; makes a repeated import return the first request.</summary>
    public string? ImportDigest { get; set; }

    public Instant? AppliedAt { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }

    public int RecordVersion { get; set; } = 1;

    public List<ConfigChangeItemRow> Items { get; set; } = [];
}

/// <summary><c>mkt.config_change_item</c>: one value of a change request.</summary>
internal sealed class ConfigChangeItemRow
{
    public Guid RequestId { get; set; }

    public int LineNo { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string LegalStatus { get; set; } = string.Empty;

    public string SourceRef { get; set; } = string.Empty;

    public bool MotorPath { get; set; }

    public BusinessDate? ValidFrom { get; set; }

    public BusinessDate? ValidTo { get; set; }

    public string? Note { get; set; }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add … --project src/CoreIns.Modules.Market</c> (no connection opened).</summary>
internal sealed class MarketDbContextDesignTimeFactory : IDesignTimeDbContextFactory<MarketDbContext>
{
    public MarketDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<MarketDbContext>("Host=localhost;Database=coreins_design;Username=design", MarketModule.Schema));
}
