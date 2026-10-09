using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Persistence;

internal static class ReceivableModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<ReceivableRow>(e =>
        {
            e.ToTable("receivable", t =>
            {
                t.HasCheckConstraint("ck_receivable_amount", "amount > 0 AND amount = round(amount, 2)");
                t.HasCheckConstraint("ck_receivable_currency", "currency = 'EUR'");
                t.HasCheckConstraint("ck_receivable_source", "(source_type = 'CLM_CLAIM_PAYMENT' AND purpose IN ('SALVAGE','SUBROGATION') AND claim_id IS NOT NULL AND recovery_id IS NOT NULL AND statement_ref IS NULL) OR (source_type = 'FS_CLEARING' AND purpose = 'FS_NET' AND statement_ref IS NOT NULL AND claim_id IS NULL AND recovery_id IS NULL)");
            });
            e.HasKey(r => r.ReceivableId);
            foreach (var p in e.Metadata.GetProperties())
            {
                p.SetColumnName(System.Text.RegularExpressions.Regex.Replace(p.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
            }
            e.Property(r => r.Amount).HasColumnType("numeric(19,4)");
            e.Property(r => r.Currency).HasColumnType("char(3)");
            e.Property(r => r.RegisteredAt).HasColumnType("timestamptz");
            e.HasIndex(r => new { r.LegalEntityId, r.SourceType, r.SourceId, r.Purpose, r.CounterpartyPartyId, r.Amount }).IsUnique();
            e.HasIndex(r => r.PaymentReference).IsUnique();
            e.HasOne<BillingAccountRow>().WithMany().HasForeignKey(r => r.BillingAccountId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ReceivableAllocationRow>(e =>
        {
            e.ToTable("receivable_allocation", t => t.HasCheckConstraint("ck_receivable_allocation_amount", "amount > 0 AND amount = round(amount, 2)"));
            e.HasKey(r => r.AllocationId);
            foreach (var p in e.Metadata.GetProperties())
            {
                p.SetColumnName(System.Text.RegularExpressions.Regex.Replace(p.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
            }
            e.Property(r => r.Amount).HasColumnType("numeric(19,4)");
            e.Property(r => r.Currency).HasColumnType("char(3)");
            e.Property(r => r.AllocatedAt).HasColumnType("timestamptz");
            e.HasOne<ReceivableRow>().WithMany().HasForeignKey(r => r.ReceivableId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ReceiptRow>().WithMany().HasForeignKey(r => r.ReceiptId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(r => new { r.ReceiptId, r.ReceivableId }).IsUnique();
        });
    }
}
