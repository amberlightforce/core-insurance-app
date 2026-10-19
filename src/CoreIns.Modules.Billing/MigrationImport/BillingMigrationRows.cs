using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.MigrationImport;

/// <summary>
/// <c>bil.migration_receivable</c>: an opening receivable imported from a legacy invoice (SL6-MIG-BIL-IMPORT), origin=MIGRATION.
/// Carries the legacy outstanding to the cent and what has been collected against it since (the reverser refuses when that is not zero).
/// </summary>
internal sealed class MigrationReceivableRow
{
    public Guid ReceivableId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public Guid BatchId { get; set; }

    public string LegacyInvoiceNumber { get; set; } = string.Empty;

    public BillingAccountId BillingAccountId { get; set; }

    public PartyId PayerPartyId { get; set; }

    public string? PolicyLegacyNumber { get; set; }

    public decimal OriginalAmount { get; set; }

    public decimal OutstandingAmount { get; set; }

    /// <summary>Cash collected against the opening receivable since the migration; non-zero blocks the reversal.</summary>
    public decimal CollectedAmount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public DateOnly DueDate { get; set; }

    public string Origin { get; set; } = "MIGRATION";

    public Instant ImportedAt { get; set; }

    public string ImportedBy { get; set; } = string.Empty;
}

/// <summary><c>bil.migration_mandate</c>: a SEPA mandate imported with its original reference and signature date; the IBAN itself is not carried (P2).</summary>
internal sealed class MigrationMandateRow
{
    public Guid MandateId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public Guid BatchId { get; set; }

    public BillingAccountId BillingAccountId { get; set; }

    public PartyId PayerPartyId { get; set; }

    /// <summary>The unique mandate reference (UMR) as issued by the legacy system.</summary>
    public string MandateReference { get; set; } = string.Empty;

    public DateOnly SignatureDate { get; set; }

    public string? IbanMasked { get; set; }

    public string Status { get; set; } = "ACTIVE";

    public string Origin { get; set; } = "MIGRATION";

    /// <summary>Set when a collection first used the mandate; blocks the reversal.</summary>
    public Instant? FirstUsedAt { get; set; }

    public Instant ImportedAt { get; set; }

    public string ImportedBy { get; set; } = string.Empty;
}

internal static class BillingMigrationModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<MigrationReceivableRow>(e =>
        {
            e.ToTable("migration_receivable", t =>
            {
                t.HasCheckConstraint("ck_migration_receivable_amounts", "original_amount >= outstanding_amount AND outstanding_amount > 0 AND collected_amount >= 0 AND outstanding_amount = round(outstanding_amount, 2) AND original_amount = round(original_amount, 2)");
                t.HasCheckConstraint("ck_migration_receivable_origin", "origin = 'MIGRATION'");
            });
            e.HasKey(r => r.ReceivableId).HasName("pk_migration_receivable");
            e.Property(r => r.ReceivableId).HasColumnName("receivable_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.BatchId).HasColumnName("batch_id");
            e.Property(r => r.LegacyInvoiceNumber).HasColumnName("legacy_invoice_number");
            e.Property(r => r.BillingAccountId).HasColumnName("billing_account_id");
            e.Property(r => r.PayerPartyId).HasColumnName("payer_party_id");
            e.Property(r => r.PolicyLegacyNumber).HasColumnName("policy_legacy_number");
            e.Property(r => r.OriginalAmount).HasColumnName("original_amount").HasColumnType("numeric(19,4)");
            e.Property(r => r.OutstandingAmount).HasColumnName("outstanding_amount").HasColumnType("numeric(19,4)");
            e.Property(r => r.CollectedAmount).HasColumnName("collected_amount").HasColumnType("numeric(19,4)").HasDefaultValue(0m);
            e.Property(r => r.Currency).HasColumnName("currency").HasColumnType("char(3)");
            e.Property(r => r.DueDate).HasColumnName("due_date");
            e.Property(r => r.Origin).HasColumnName("origin").HasDefaultValue("MIGRATION");
            e.Property(r => r.ImportedAt).HasColumnName("imported_at").HasColumnType("timestamptz");
            e.Property(r => r.ImportedBy).HasColumnName("imported_by");
            e.HasIndex(r => r.BatchId).HasDatabaseName("ix_migration_receivable_batch");
            e.HasIndex(r => new { r.LegalEntityId, r.LegacyInvoiceNumber }).IsUnique().HasDatabaseName("ux_migration_receivable_legacy");
            e.HasOne<Persistence.BillingAccountRow>().WithMany().HasForeignKey(r => r.BillingAccountId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<MigrationMandateRow>(e =>
        {
            e.ToTable("migration_mandate", t =>
            {
                t.HasCheckConstraint("ck_migration_mandate_origin", "origin = 'MIGRATION'");
                t.HasCheckConstraint("ck_migration_mandate_status", "status IN ('ACTIVE', 'CANCELLED')");
            });
            e.HasKey(r => r.MandateId).HasName("pk_migration_mandate");
            e.Property(r => r.MandateId).HasColumnName("mandate_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.BatchId).HasColumnName("batch_id");
            e.Property(r => r.BillingAccountId).HasColumnName("billing_account_id");
            e.Property(r => r.PayerPartyId).HasColumnName("payer_party_id");
            e.Property(r => r.MandateReference).HasColumnName("mandate_reference");
            e.Property(r => r.SignatureDate).HasColumnName("signature_date");
            e.Property(r => r.IbanMasked).HasColumnName("iban_masked");
            e.Property(r => r.Status).HasColumnName("status").HasDefaultValue("ACTIVE");
            e.Property(r => r.Origin).HasColumnName("origin").HasDefaultValue("MIGRATION");
            e.Property(r => r.FirstUsedAt).HasColumnName("first_used_at").HasColumnType("timestamptz");
            e.Property(r => r.ImportedAt).HasColumnName("imported_at").HasColumnType("timestamptz");
            e.Property(r => r.ImportedBy).HasColumnName("imported_by");
            e.HasIndex(r => r.BatchId).HasDatabaseName("ix_migration_mandate_batch");
            e.HasIndex(r => new { r.LegalEntityId, r.MandateReference }).IsUnique().HasDatabaseName("ux_migration_mandate_reference");
            e.HasOne<Persistence.BillingAccountRow>().WithMany().HasForeignKey(r => r.BillingAccountId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
