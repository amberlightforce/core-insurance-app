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

            // REQ-PFC-032: a version number identifies one version of a product.
            entity.HasIndex(e => new { e.ProductId, e.Major, e.Minor }).IsUnique().HasDatabaseName("ux_product_version_number");
            entity.HasIndex(e => new { e.LegalEntityId, e.Status }).HasDatabaseName("ix_product_version_resolution");
            entity.HasOne<ProductRow>().WithMany().HasForeignKey(e => e.ProductId).HasConstraintName("fk_product_version_product").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ArtifactRow>().WithMany().HasForeignKey(e => e.ArtefactHash).HasConstraintName("fk_product_version_artifact").OnDelete(DeleteBehavior.Restrict);
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
