using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Product.Domain;
using CoreIns.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CoreIns.Modules.Product.Persistence;

/// <summary>
/// The Product module's EF Core context (schema <c>pfc</c>) on the scope's connection and transaction. Writes go through
/// this context; reads use Dapper on the same connection (<see cref="Queries.ProductReader"/>). Internal by architecture rule.
/// </summary>
internal sealed class ProductDbContext(DbContextOptions<ProductDbContext> options) : ModuleDbContext(options)
{
    public DbSet<ProductRow> Products => Set<ProductRow>();

    public DbSet<ProductVersionRow> Versions => Set<ProductVersionRow>();

    public DbSet<ArtifactRow> Artifacts => Set<ArtifactRow>();

    public DbSet<FallbackRequestRow> Fallbacks => Set<FallbackRequestRow>();

    protected override string Schema => ProductModule.Schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ProductRow>(entity =>
        {
            entity.ToTable("product", table =>
            {
                table.HasCheckConstraint("ck_product_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                table.HasCheckConstraint("ck_product_record_version", "record_version >= 1");
            });
            entity.HasKey(e => e.ProductId).HasName("pk_product");
            entity.Property(e => e.ProductId).HasColumnName("product_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)");
            entity.Property(e => e.Code).HasColumnName("code");
            entity.Property(e => e.LineCode).HasColumnName("line_code");
            entity.Property(e => e.LineFamily).HasColumnName("line_family");
            entity.Property(e => e.NameEl).HasColumnName("name_el");
            entity.Property(e => e.NameEn).HasColumnName("name_en");
            entity.Property(e => e.ProductType).HasColumnName("product_type");
            entity.Property(e => e.CustomerType).HasColumnName("customer_type");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");

            // REQ-PFC-031: the product code is unique per legal entity.
            entity.HasIndex(e => new { e.LegalEntityId, e.Code }).IsUnique().HasDatabaseName("ux_product_code");
        });

        modelBuilder.Entity<ProductVersionRow>(entity =>
        {
            entity.ToTable("product_version", table =>
            {
                table.HasCheckConstraint("ck_product_version_status", Codes.CheckSql<ProductVersionState>("status"));
                table.HasCheckConstraint("ck_product_version_substate", Codes.CheckSql<ProductVersionLockedSubstate>("lifecycle_substate"));
                table.HasCheckConstraint("ck_product_version_substate_locked", "(status = 'LOCKED') = (lifecycle_substate IS NOT NULL)");
                table.HasCheckConstraint("ck_product_version_nb_window", "new_business_to IS NULL OR new_business_to > new_business_from");
                table.HasCheckConstraint("ck_product_version_renewal_window", "renewal_to IS NULL OR renewal_to > renewal_from");
                table.HasCheckConstraint("ck_product_version_number", "major >= 0 AND minor >= 0");
                table.HasCheckConstraint("ck_product_version_hash", "artefact_hash ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_product_version_channels", "cardinality(channels) >= 1");
                table.HasCheckConstraint("ck_product_version_fallback_pair", "(fallback_of_version_id IS NULL) = (replaces_version_id IS NULL)");
            });
            entity.HasKey(e => e.ProductVersionId).HasName("pk_product_version");
            entity.Property(e => e.ProductVersionId).HasColumnName("product_version_id");
            entity.Property(e => e.ProductId).HasColumnName("product_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)");
            entity.Property(e => e.Major).HasColumnName("major");
            entity.Property(e => e.Minor).HasColumnName("minor");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.LifecycleSubstate).HasColumnName("lifecycle_substate");
            entity.Property(e => e.IsAbstract).HasColumnName("is_abstract");
            entity.Property(e => e.Channels).HasColumnName("channels");
            entity.Property(e => e.ContractCurrency).HasColumnName("contract_currency").HasColumnType("char(3)");
            entity.Property(e => e.NewBusinessFrom).HasColumnName("new_business_from");
            entity.Property(e => e.NewBusinessTo).HasColumnName("new_business_to");
            entity.Property(e => e.RenewalFrom).HasColumnName("renewal_from");
            entity.Property(e => e.RenewalTo).HasColumnName("renewal_to");
            entity.Property(e => e.ArtefactHash).HasColumnName("artefact_hash").HasColumnType("char(64)");
            entity.Property(e => e.SchemaVersion).HasColumnName("schema_version");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.LockedAt).HasColumnName("locked_at").HasColumnType("timestamptz");
            entity.Property(e => e.FallbackOfVersionId).HasColumnName("fallback_of_version_id");
            entity.Property(e => e.ReplacesVersionId).HasColumnName("replaces_version_id");
            entity.HasOne<ProductVersionRow>().WithMany().HasForeignKey(e => e.FallbackOfVersionId).HasConstraintName("fk_product_version_fallback_of").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProductVersionRow>().WithMany().HasForeignKey(e => e.ReplacesVersionId).HasConstraintName("fk_product_version_replaces").OnDelete(DeleteBehavior.Restrict);

            // REQ-PFC-032: a version number identifies one version of a product.
            entity.HasIndex(e => new { e.ProductId, e.Major, e.Minor }).IsUnique().HasDatabaseName("ux_product_version_number");
            entity.HasIndex(e => new { e.LegalEntityId, e.Status }).HasDatabaseName("ix_product_version_resolution");
            entity.HasOne<ProductRow>().WithMany().HasForeignKey(e => e.ProductId).HasConstraintName("fk_product_version_product").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ArtifactRow>().WithMany().HasForeignKey(e => e.ArtefactHash).HasConstraintName("fk_product_version_artifact").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FallbackRequestRow>(entity =>
        {
            entity.ToTable("fallback_request", table =>
            {
                table.HasCheckConstraint("ck_fallback_status", "status IN ('PENDING_APPROVAL', 'APPLIED', 'REJECTED')");
                table.HasCheckConstraint("ck_fallback_reason", "char_length(reason) BETWEEN 20 AND 128");
                table.HasCheckConstraint("ck_fallback_hash", "payload_hash ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_fallback_new_number", "new_major >= 0 AND new_minor >= 0");
                table.HasCheckConstraint("ck_fallback_decision", "(status = 'PENDING_APPROVAL') = (decided_at IS NULL AND decided_by IS NULL)");
                table.HasCheckConstraint("ck_fallback_applied", "(status = 'APPLIED') = (new_version_id IS NOT NULL)");
                table.HasCheckConstraint("ck_fallback_sod", "decided_by IS NULL OR decided_by <> requested_by");
                table.HasCheckConstraint("ck_fallback_record_version", "record_version >= 1");
            });
            entity.HasKey(e => e.FallbackId).HasName("pk_fallback_request");
            entity.Property(e => e.FallbackId).HasColumnName("fallback_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)");
            entity.Property(e => e.ProductId).HasColumnName("product_id");
            entity.Property(e => e.DefectiveVersionId).HasColumnName("defective_version_id");
            entity.Property(e => e.SourceVersionId).HasColumnName("source_version_id");
            entity.Property(e => e.NewMajor).HasColumnName("new_major");
            entity.Property(e => e.NewMinor).HasColumnName("new_minor");
            entity.Property(e => e.FallbackDate).HasColumnName("fallback_date");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.PayloadHash).HasColumnName("payload_hash").HasColumnType("char(64)");
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.RequestedBy).HasColumnName("requested_by");
            entity.Property(e => e.RequestedByPrincipal).HasColumnName("requested_by_principal");
            entity.Property(e => e.RequestedAt).HasColumnName("requested_at").HasColumnType("timestamptz");
            entity.Property(e => e.DecidedBy).HasColumnName("decided_by");
            entity.Property(e => e.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            entity.Property(e => e.DecisionReason).HasColumnName("decision_reason");
            entity.Property(e => e.NewVersionId).HasColumnName("new_version_id");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();

            // At most one live fall-back per defective version: a pending or applied one blocks another (PFC-ERR-FALLBACK-STATE).
            entity.HasIndex(e => e.DefectiveVersionId).IsUnique().HasFilter("status <> 'REJECTED'").HasDatabaseName("ux_fallback_live_per_defective");
            entity.HasIndex(e => e.ApprovalRequestId).IsUnique().HasDatabaseName("ux_fallback_approval");
            entity.HasOne<ProductRow>().WithMany().HasForeignKey(e => e.ProductId).HasConstraintName("fk_fallback_product").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProductVersionRow>().WithMany().HasForeignKey(e => e.DefectiveVersionId).HasConstraintName("fk_fallback_defective").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProductVersionRow>().WithMany().HasForeignKey(e => e.SourceVersionId).HasConstraintName("fk_fallback_source").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProductVersionRow>().WithMany().HasForeignKey(e => e.NewVersionId).HasConstraintName("fk_fallback_new_version").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ArtifactRow>(entity =>
        {
            entity.ToTable("artifact", table =>
            {
                table.HasCheckConstraint("ck_artifact_hash", "artefact_hash ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_artifact_size", "size_bytes > 0");
            });
            entity.HasKey(e => e.ArtefactHash).HasName("pk_artifact");
            entity.Property(e => e.ArtefactHash).HasColumnName("artefact_hash").HasColumnType("char(64)");
            entity.Property(e => e.CanonicalJson).HasColumnName("canonical_json");
            entity.Property(e => e.SizeBytes).HasColumnName("size_bytes");
            entity.Property(e => e.StoredAt).HasColumnName("stored_at").HasColumnType("timestamptz");
        });
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add … --project src/CoreIns.Modules.Product</c> (no connection opened).</summary>
internal sealed class ProductDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ProductDbContext>
{
    public ProductDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<ProductDbContext>("Host=localhost;Database=coreins_design;Username=design", ProductModule.Schema));
}
