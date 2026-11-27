using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Persistence;

/// <summary>States of a disbursement batch (BO-BIL-10): Prepared, then ReleaseRequested by the first person, then Released by the second.</summary>
internal static class BatchStates
{
    public const string Prepared = "PREPARED";
    public const string ReleaseRequested = "RELEASE_REQUESTED";
    public const string Released = "RELEASED";
}

/// <summary>
/// A bank-file batch of Approved non-claim disbursements (BO-BIL-10): one SEPA pain.001 file paid from one own bank account on one value date.
/// The file itself (IBANs and payee names) is never stored here: only its content hash, which the second person confirms, and its reference.
/// </summary>
internal sealed class DisbursementBatchRow
{
    public Guid BatchId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string State { get; set; } = string.Empty;

    /// <summary>The own bank account to debit (the insurer's IBAN, not personal data).</summary>
    public string BankAccount { get; set; } = string.Empty;

    public BusinessDate ValueDate { get; set; }

    public string Currency { get; set; } = string.Empty;

    public int ItemCount { get; set; }

    public decimal TotalAmount { get; set; }

    public string FileHash { get; set; } = string.Empty;

    public string FileRef { get; set; } = string.Empty;

    public Instant PreparedAt { get; set; }

    public string PreparedBy { get; set; } = string.Empty;

    public Instant? ReleaseRequestedAt { get; set; }

    public string? ReleaseRequestedBy { get; set; }

    public Instant? ReleasedAt { get; set; }

    public string? ReleasedBy { get; set; }

    public int RecordVersion { get; set; }
}

/// <summary>A disbursement in a batch. The disbursement id is the key: a disbursement is in at most one batch, ever (EC-08 business key against a double release).</summary>
internal sealed class DisbursementBatchItemRow
{
    public DisbursementId DisbursementId { get; set; }

    public Guid BatchId { get; set; }

    public decimal Amount { get; set; }

    /// <summary>True when the disbursement was stopped (or otherwise left Approved) before the release and the file was rebuilt without it.</summary>
    public bool Dropped { get; set; }
}

internal static class DisbursementBatchModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<DisbursementBatchRow>(e =>
        {
            e.ToTable("disbursement_batch", t =>
            {
                t.HasCheckConstraint("ck_disbursement_batch_state", "state IN ('PREPARED', 'RELEASE_REQUESTED', 'RELEASED')");
                t.HasCheckConstraint("ck_disbursement_batch_total", "total_amount > 0 AND item_count > 0");
                t.HasCheckConstraint("ck_disbursement_batch_currency", "currency ~ '^[A-Z]{3}$'");
                t.HasCheckConstraint("ck_disbursement_batch_hash", "file_hash ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("ck_disbursement_batch_record_version", "record_version >= 1");
            });
            e.HasKey(r => r.BatchId).HasName("pk_disbursement_batch");
            SnakeCase(e);
            e.Property(r => r.Currency).HasColumnType("char(3)");
            e.Property(r => r.TotalAmount).HasColumnType("numeric(19,4)");
            e.Property(r => r.FileHash).HasColumnType("char(64)");
            e.Property(r => r.PreparedAt).HasColumnType("timestamptz");
            e.Property(r => r.ReleaseRequestedAt).HasColumnType("timestamptz");
            e.Property(r => r.ReleasedAt).HasColumnType("timestamptz");
            e.Property(r => r.RecordVersion).IsConcurrencyToken();
            // The same file content (same items, account, date) is one batch: a second batch with it is refused (EC-08).
            e.HasIndex(r => new { r.LegalEntityId, r.FileHash }).IsUnique().HasDatabaseName("ux_disbursement_batch_file");
        });

        model.Entity<DisbursementBatchItemRow>(e =>
        {
            e.ToTable("disbursement_batch_item", t => t.HasCheckConstraint("ck_disbursement_batch_item_amount", "amount > 0"));
            e.HasKey(r => r.DisbursementId).HasName("pk_disbursement_batch_item");
            SnakeCase(e);
            e.Property(r => r.Amount).HasColumnType("numeric(19,4)");
            e.HasIndex(r => r.BatchId).HasDatabaseName("ix_disbursement_batch_item_batch");
            e.HasOne<DisbursementBatchRow>().WithMany().HasForeignKey(r => r.BatchId).HasConstraintName("fk_disbursement_batch_item_batch").OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DisbursementRow>().WithMany().HasForeignKey(r => r.DisbursementId).HasConstraintName("fk_disbursement_batch_item_disbursement").OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void SnakeCase<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e)
        where T : class
    {
        foreach (var p in e.Metadata.GetProperties())
        {
            p.SetColumnName(System.Text.RegularExpressions.Regex.Replace(p.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
        }
    }
}
