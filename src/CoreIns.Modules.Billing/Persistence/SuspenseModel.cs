using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Persistence;

internal static class SuspenseModel
{
    public static void Configure(ModelBuilder model) =>
        model.Entity<SuspenseItemRow>(e =>
        {
            e.ToTable("suspense_item", t =>
            {
                t.HasCheckConstraint("ck_suspense_item_state", "state IN ('OPEN', 'REFUND_PENDING', 'ALLOCATED', 'REFUNDED', 'RETURNED')");
                t.HasCheckConstraint("ck_suspense_item_amount", "amount > 0");
                t.HasCheckConstraint("ck_suspense_item_record_version", "record_version >= 1");
            });
            e.HasKey(r => r.SuspenseItemId).HasName("pk_suspense_item");
            foreach (var p in e.Metadata.GetProperties())
            {
                p.SetColumnName(System.Text.RegularExpressions.Regex.Replace(p.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
            }

            e.Property(r => r.Amount).HasColumnType("numeric(19,4)");
            e.Property(r => r.Currency).HasColumnType("char(3)");
            e.Property(r => r.CreatedAt).HasColumnType("timestamptz");
            e.Property(r => r.ResolvedAt).HasColumnType("timestamptz");
            e.Property(r => r.RecordVersion).IsConcurrencyToken();
            e.HasIndex(r => r.ReceiptId).IsUnique().HasDatabaseName("ux_suspense_item_receipt");
            e.HasIndex(r => new { r.LegalEntityId, r.State, r.SuspenseItemId }).HasDatabaseName("ix_suspense_item_state");
            e.HasIndex(r => r.BillingAccountId).HasDatabaseName("ix_suspense_item_account");
            e.HasIndex(r => r.RefundId).HasDatabaseName("ix_suspense_item_refund");
            e.HasOne<ReceiptRow>().WithMany().HasForeignKey(r => r.ReceiptId).HasConstraintName("fk_suspense_item_receipt").OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BillingAccountRow>().WithMany().HasForeignKey(r => r.BillingAccountId).HasConstraintName("fk_suspense_item_account").OnDelete(DeleteBehavior.Restrict);
        });
}
