using CoreIns.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.MigrationImport;

/// <summary>
/// <c>fin.migration_import</c>: an opening-balance journal created by the migration importer (SL6-MIG-BIL-IMPORT). Keeps the batch, the
/// legacy id and the legacy trial-balance totals the journal reconciled to; insert-only.
/// </summary>
internal sealed class MigrationImportRow
{
    public Guid JournalId { get; set; }

    public Guid LegalEntityId { get; set; }

    public Guid BatchId { get; set; }

    public string LegacyId { get; set; } = string.Empty;

    public string Book { get; set; } = string.Empty;

    public DateOnly AccountingDate { get; set; }

    public decimal TrialBalanceDebit { get; set; }

    public decimal TrialBalanceCredit { get; set; }

    public Instant ImportedAt { get; set; }
}

internal static class FinanceMigrationModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<MigrationImportRow>(e =>
        {
            e.ToTable("migration_import", t => t.HasCheckConstraint("ck_migration_import_reconciled", "trial_balance_debit = trial_balance_credit"));
            e.HasKey(r => r.JournalId).HasName("pk_migration_import");
            e.Property(r => r.JournalId).HasColumnName("journal_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.BatchId).HasColumnName("batch_id");
            e.Property(r => r.LegacyId).HasColumnName("legacy_id");
            e.Property(r => r.Book).HasColumnName("book");
            e.Property(r => r.AccountingDate).HasColumnName("accounting_date");
            e.Property(r => r.TrialBalanceDebit).HasColumnName("trial_balance_debit").HasColumnType("numeric(19,4)");
            e.Property(r => r.TrialBalanceCredit).HasColumnName("trial_balance_credit").HasColumnType("numeric(19,4)");
            e.Property(r => r.ImportedAt).HasColumnName("imported_at").HasColumnType("timestamptz");
            e.HasIndex(r => r.BatchId).HasDatabaseName("ix_migration_import_batch");
            e.HasIndex(r => new { r.LegalEntityId, r.LegacyId }).IsUnique().HasDatabaseName("ux_migration_import_legacy");
            e.HasOne<Persistence.JournalEntryRow>().WithMany().HasForeignKey(r => r.JournalId).HasConstraintName("fk_migration_import_journal").OnDelete(DeleteBehavior.Restrict);
        });
    }
}
