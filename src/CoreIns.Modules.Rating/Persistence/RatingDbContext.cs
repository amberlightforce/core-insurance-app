using CoreIns.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CoreIns.Modules.Rating.Persistence;

/// <summary>
/// The Rating module's EF Core context (schema <c>rat</c>). It defines the tables and their migrations; the module reads
/// and writes them with Dapper on the scope's <see cref="DbSession"/> connection (content-addressed inserts are
/// <c>ON CONFLICT DO NOTHING</c>), so a worksheet commits with the caller's transaction. Internal to the module.
/// </summary>
internal sealed class RatingDbContext(DbContextOptions<RatingDbContext> options) : ModuleDbContext(options)
{
    public DbSet<RateTableVersionRow> RateTableVersions => Set<RateTableVersionRow>();

    public DbSet<RatingArtifactRow> RatingArtifacts => Set<RatingArtifactRow>();

    public DbSet<RateActivationRow> RateActivations => Set<RateActivationRow>();

    public DbSet<WorksheetRow> Worksheets => Set<WorksheetRow>();

    public DbSet<WorksheetIndexRow> WorksheetIndex => Set<WorksheetIndexRow>();

    protected override string Schema => RatingModule.Schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RateTableVersionRow>(entity =>
        {
            entity.ToTable("rate_table_version", table =>
            {
                table.HasCheckConstraint("ck_rate_table_version_hash", "table_hash ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_rate_table_version_status", "data_status IN ('ILLUSTRATIVE_TEST_DATA', 'APPROVED')");
            });
            entity.HasKey(e => e.TableHash).HasName("pk_rate_table_version");
            entity.Property(e => e.TableHash).HasColumnName("table_hash").HasColumnType("char(64)");
            entity.Property(e => e.TableCode).HasColumnName("table_code");
            entity.Property(e => e.VersionNo).HasColumnName("version_no");
            entity.Property(e => e.HitPolicy).HasColumnName("hit_policy");
            entity.Property(e => e.DataStatus).HasColumnName("data_status");
            entity.Property(e => e.Definition).HasColumnName("definition").HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => new { e.TableCode, e.VersionNo }).HasDatabaseName("ix_rate_table_version_code");
        });

        modelBuilder.Entity<RatingArtifactRow>(entity =>
        {
            entity.ToTable("rating_artifact", table =>
            {
                table.HasCheckConstraint("ck_rating_artifact_hash", "artefact_hash ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_rating_artifact_status", "data_status IN ('ILLUSTRATIVE_TEST_DATA', 'APPROVED')");
            });
            entity.HasKey(e => e.ArtefactHash).HasName("pk_rating_artifact");
            entity.Property(e => e.ArtefactHash).HasColumnName("artefact_hash").HasColumnType("char(64)");
            entity.Property(e => e.ArtefactCode).HasColumnName("artefact_code");
            entity.Property(e => e.Label).HasColumnName("label");
            entity.Property(e => e.ProductCode).HasColumnName("product_code");
            entity.Property(e => e.ProductVersion).HasColumnName("product_version");
            entity.Property(e => e.EngineVersion).HasColumnName("engine_version");
            entity.Property(e => e.DataStatus).HasColumnName("data_status");
            entity.Property(e => e.Definition).HasColumnName("definition").HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => new { e.ProductCode, e.ProductVersion }).HasDatabaseName("ix_rating_artifact_product");
        });

        modelBuilder.Entity<RateActivationRow>(entity =>
        {
            entity.ToTable("rate_activation", table =>
            {
                table.HasCheckConstraint("ck_rate_activation_status", "status IN ('Scheduled', 'Active', 'Superseded', 'Cancelled', 'RolledBack')");
                table.HasCheckConstraint("ck_rate_activation_range", "effective_to IS NULL OR effective_to > effective_from");
                table.HasCheckConstraint("ck_rate_activation_fallback", "(fallback_source_version IS NULL) = (source_event_id IS NULL)");
            });
            entity.HasKey(e => e.ActivationId).HasName("pk_rate_activation");
            entity.Property(e => e.ActivationId).HasColumnName("activation_id");
            entity.Property(e => e.FallbackSourceVersion).HasColumnName("fallback_source_version");
            entity.Property(e => e.SourceEventId).HasColumnName("source_event_id");
            entity.HasIndex(e => e.SourceEventId).IsUnique().HasDatabaseName("ux_rate_activation_source_event");
            entity.Property(e => e.ArtefactHash).HasColumnName("artefact_hash").HasColumnType("char(64)");
            entity.Property(e => e.ProductCode).HasColumnName("product_code");
            entity.Property(e => e.ProductVersion).HasColumnName("product_version");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            entity.Property(e => e.EffectiveTo).HasColumnName("effective_to");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => new { e.ProductCode, e.ProductVersion, e.EffectiveFrom }).IsUnique().HasDatabaseName("ux_rate_activation_product_from");
            entity.HasOne<RatingArtifactRow>().WithMany().HasForeignKey(e => e.ArtefactHash)
                .HasConstraintName("fk_rate_activation_artifact").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorksheetRow>(entity =>
        {
            entity.ToTable("worksheet", table =>
            {
                table.HasCheckConstraint("ck_worksheet_id", "worksheet_id ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_worksheet_status", "data_status IN ('ILLUSTRATIVE_TEST_DATA', 'APPROVED')");
            });
            entity.HasKey(e => e.WorksheetId).HasName("pk_worksheet");
            entity.Property(e => e.WorksheetId).HasColumnName("worksheet_id").HasColumnType("char(64)");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)");
            entity.Property(e => e.ArtefactHash).HasColumnName("artefact_hash").HasColumnType("char(64)");
            entity.Property(e => e.ConfigurationHash).HasColumnName("configuration_hash").HasColumnType("char(64)");
            entity.Property(e => e.InputHash).HasColumnName("input_hash").HasColumnType("char(64)");
            entity.Property(e => e.EngineVersion).HasColumnName("engine_version");
            entity.Property(e => e.DataStatus).HasColumnName("data_status");
            entity.Property(e => e.Body).HasColumnName("body").HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => e.ArtefactHash).HasDatabaseName("ix_worksheet_artifact");
            entity.HasOne<RatingArtifactRow>().WithMany().HasForeignKey(e => e.ArtefactHash)
                .HasConstraintName("fk_worksheet_artifact").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorksheetIndexRow>(entity =>
        {
            entity.ToTable("worksheet_index", table =>
                table.HasCheckConstraint("ck_worksheet_index_retention", "retention_state IN ('QUOTE', 'ATTACHED', 'DRY_RUN')"));
            entity.HasKey(e => e.IndexId).HasName("pk_worksheet_index");
            entity.Property(e => e.IndexId).HasColumnName("index_id").UseIdentityAlwaysColumn();
            entity.Property(e => e.WorksheetId).HasColumnName("worksheet_id").HasColumnType("char(64)");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.QuoteId).HasColumnName("quote_id");
            entity.Property(e => e.JobId).HasColumnName("job_id");
            entity.Property(e => e.TransactionId).HasColumnName("transaction_id");
            entity.Property(e => e.Mode).HasColumnName("mode");
            entity.Property(e => e.RetentionState).HasColumnName("retention_state");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.HasIndex(e => e.WorksheetId).HasDatabaseName("ix_worksheet_index_worksheet");
            entity.HasIndex(e => e.QuoteId).HasDatabaseName("ix_worksheet_index_quote");
            entity.HasOne<WorksheetRow>().WithMany().HasForeignKey(e => e.WorksheetId)
                .HasConstraintName("fk_worksheet_index_worksheet").OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add … --project src/CoreIns.Modules.Rating</c> (no connection opened).</summary>
internal sealed class RatingDbContextDesignTimeFactory : IDesignTimeDbContextFactory<RatingDbContext>
{
    public RatingDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<RatingDbContext>("Host=localhost;Database=coreins_design;Username=design", RatingModule.Schema));
}
