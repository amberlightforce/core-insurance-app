using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.MigrationImport;

/// <summary>
/// <c>clm.migration_import</c>: one row per imported claim (SL6-MIG-CLM-RI-IMPORT). Carries origin MIGRATION, the legacy number
/// (unique per legal entity, so a double import is one claim), the original notified date, the opening reserve as given and the
/// running clock. The opening reserve is a statement of the legacy position, not a ledger transaction: any later ledger activity is
/// post-migration activity (the reverser refuses).
/// </summary>
internal sealed class ClaimMigrationImportRow
{
    public ClaimId ClaimId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public Guid BatchId { get; set; }

    public string LegacyNumber { get; set; } = string.Empty;

    public string Origin { get; set; } = "MIGRATION";

    public BusinessDate OriginalNotifiedOn { get; set; }

    public decimal OpeningReserve { get; set; }

    public string Currency { get; set; } = string.Empty;

    public Guid? ClockId { get; set; }

    public Instant ImportedAt { get; set; }
}

internal static class ClaimMigrationModel
{
    public static void Configure(ModelBuilder model) =>
        model.Entity<ClaimMigrationImportRow>(e =>
        {
            e.ToTable("migration_import", t =>
            {
                t.HasCheckConstraint("ck_migration_import_origin", "origin = 'MIGRATION'");
                t.HasCheckConstraint("ck_migration_import_reserve", "opening_reserve >= 0");
            });
            e.HasKey(r => r.ClaimId).HasName("pk_migration_import");
            e.Property(r => r.ClaimId).HasColumnName("claim_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.BatchId).HasColumnName("batch_id");
            e.Property(r => r.LegacyNumber).HasColumnName("legacy_number");
            e.Property(r => r.Origin).HasColumnName("origin");
            e.Property(r => r.OriginalNotifiedOn).HasColumnName("original_notified_on");
            e.Property(r => r.OpeningReserve).HasColumnName("opening_reserve").HasColumnType("numeric(19,4)");
            e.Property(r => r.Currency).HasColumnName("currency");
            e.Property(r => r.ClockId).HasColumnName("clock_id");
            e.Property(r => r.ImportedAt).HasColumnName("imported_at").HasColumnType("timestamptz");
            e.HasIndex(r => new { r.LegalEntityId, r.LegacyNumber }).IsUnique().HasDatabaseName("ux_migration_import_legacy");
            e.HasIndex(r => r.BatchId).HasDatabaseName("ix_migration_import_batch");
            e.HasOne<Persistence.ClaimRow>().WithOne().HasForeignKey<ClaimMigrationImportRow>(r => r.ClaimId).OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_migration_import_claim");
        });
}
