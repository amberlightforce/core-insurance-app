using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Persistence;

/// <summary>States of one collected instalment (BO-BIL-06b).</summary>
internal static class DirectDebitStates
{
    /// <summary>In a pain.008 file at the bank; no money yet.</summary>
    public const string Submitted = "SUBMITTED";

    /// <summary>Settled: the receipt exists and is allocated to the invoice.</summary>
    public const string Collected = "COLLECTED";

    /// <summary>The bank refused it before settlement (R-transaction: reject); no receipt, the invoice is collectable again.</summary>
    public const string Rejected = "REJECTED";

    /// <summary>Returned after settlement (R-transaction: return, refund, reversal); its receipt was reversed and the invoice reopened.</summary>
    public const string Returned = "RETURNED";

    /// <summary>The two states in which the instalment counts as collected or about to be (one live item per invoice).</summary>
    public static readonly string[] Live = [Submitted, Collected];

    /// <summary>Receipt method of a collected direct debit.</summary>
    public const string ReceiptMethod = "DIRECT_DEBIT";
}

/// <summary>One pain.008 file sent to the bank (BO-BIL-06b): the file itself is in the bank-file store (IBANs are P2), only its reference and hash are kept here.</summary>
internal sealed class DirectDebitCollectionRow
{
    public Guid CollectionId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    /// <summary>pain.008 message id (<c>MsgId</c>), unique per legal entity.</summary>
    public string MessageId { get; set; } = string.Empty;

    public string FileReference { get; set; } = string.Empty;

    public string FileSha256 { get; set; } = string.Empty;

    public BusinessDate CollectionDate { get; set; }

    public int ItemCount { get; set; }

    public decimal TotalAmount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>One instalment collected by direct debit: the unit the bank answers about (by <see cref="EndToEndId"/>).</summary>
internal sealed class DirectDebitItemRow
{
    public Guid ItemId { get; set; }

    public Guid CollectionId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public BillingAccountId BillingAccountId { get; set; }

    public InvoiceId InvoiceId { get; set; }

    public Guid MandateId { get; set; }

    public Guid InstrumentId { get; set; }

    /// <summary>End-to-end id, at most 35 characters, unique; the bank returns it in the R-transaction and on the statement.</summary>
    public string EndToEndId { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    /// <summary>Sequence type sent for this item (FRST or RCUR).</summary>
    public string SequenceType { get; set; } = string.Empty;

    public BusinessDate CollectionDate { get; set; }

    public string State { get; set; } = DirectDebitStates.Submitted;

    public Guid? ReceiptId { get; set; }

    /// <summary>ISO 20022 reason code of a reject or return (AC04, AM04, MD01, MS03, SL01 and so on).</summary>
    public string? ReasonCode { get; set; }

    public Instant? SettledAt { get; set; }

    public Instant? ClosedAt { get; set; }

    public Instant CreatedAt { get; set; }

    public int RecordVersion { get; set; }
}

internal static class DirectDebitModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<DirectDebitCollectionRow>(e =>
        {
            e.ToTable("dd_collection", t =>
            {
                t.HasCheckConstraint("ck_dd_collection_count", "item_count > 0 AND total_amount > 0");
                t.HasCheckConstraint("ck_dd_collection_currency", "currency ~ '^[A-Z]{3}$'");
            });
            e.HasKey(r => r.CollectionId).HasName("pk_dd_collection");
            SnakeCase(e);
            e.Property(r => r.TotalAmount).HasColumnType("numeric(19,4)");
            e.Property(r => r.Currency).HasColumnType("char(3)");
            e.Property(r => r.CreatedAt).HasColumnType("timestamptz");
            e.HasIndex(r => new { r.LegalEntityId, r.MessageId }).IsUnique().HasDatabaseName("ux_dd_collection_message");
        });

        model.Entity<DirectDebitItemRow>(e =>
        {
            e.ToTable("dd_item", t =>
            {
                t.HasCheckConstraint("ck_dd_item_state", "state IN ('SUBMITTED', 'COLLECTED', 'REJECTED', 'RETURNED')");
                t.HasCheckConstraint("ck_dd_item_sequence", "sequence_type IN ('FRST', 'RCUR')");
                t.HasCheckConstraint("ck_dd_item_amount", "amount > 0");
                t.HasCheckConstraint("ck_dd_item_currency", "currency ~ '^[A-Z]{3}$'");
                t.HasCheckConstraint("ck_dd_item_end_to_end", "char_length(end_to_end_id) BETWEEN 1 AND 35");
                t.HasCheckConstraint("ck_dd_item_record_version", "record_version >= 1");
                t.HasCheckConstraint("ck_dd_item_receipt", "(state IN ('COLLECTED', 'RETURNED')) = (receipt_id IS NOT NULL)");
            });
            e.HasKey(r => r.ItemId).HasName("pk_dd_item");
            SnakeCase(e);
            e.Property(r => r.Amount).HasColumnType("numeric(19,4)");
            e.Property(r => r.Currency).HasColumnType("char(3)");
            e.Property(r => r.SettledAt).HasColumnType("timestamptz");
            e.Property(r => r.ClosedAt).HasColumnType("timestamptz");
            e.Property(r => r.CreatedAt).HasColumnType("timestamptz");
            e.Property(r => r.RecordVersion).IsConcurrencyToken();
            e.HasIndex(r => new { r.LegalEntityId, r.EndToEndId }).IsUnique().HasDatabaseName("ux_dd_item_end_to_end");

            // An instalment is collected once: only one item per invoice may be in flight or settled (a reject or a return frees the invoice).
            e.HasIndex(r => r.InvoiceId).IsUnique().HasFilter("state IN ('SUBMITTED', 'COLLECTED')").HasDatabaseName("ux_dd_item_invoice_live");
            e.HasIndex(r => r.CollectionId).HasDatabaseName("ix_dd_item_collection");
            e.HasIndex(r => new { r.LegalEntityId, r.State, r.CollectionDate }).HasDatabaseName("ix_dd_item_state");
            e.HasOne<DirectDebitCollectionRow>().WithMany().HasForeignKey(r => r.CollectionId).HasConstraintName("fk_dd_item_collection").OnDelete(DeleteBehavior.Restrict);
            e.HasOne<InvoiceRow>().WithMany().HasForeignKey(r => r.InvoiceId).HasConstraintName("fk_dd_item_invoice").OnDelete(DeleteBehavior.Restrict);
            e.HasOne<MandateRow>().WithMany().HasForeignKey(r => r.MandateId).HasConstraintName("fk_dd_item_mandate").OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BillingAccountRow>().WithMany().HasForeignKey(r => r.BillingAccountId).HasConstraintName("fk_dd_item_account").OnDelete(DeleteBehavior.Restrict);
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

/// <summary>
/// BO-BIL-06b database part (data): the ledger rule of a returned receipt (<c>RECEIPT_RETURNED</c>: unapplied cash LA-11 debited, cash at bank
/// LA-10 credited, the mirror of RECEIVED); FIN journalises it with rule set v9.
/// </summary>
internal static class DirectDebitSql
{
    public const string Up = """
        INSERT INTO bil.ledger_rule (rule_id, version, event_type, charge_category, bill_mode, jurisdiction, qualifier,
            debit_account, credit_account, amount_expression, valid_from, valid_to, source) VALUES
            ('BLR-RECEIPT-RETURNED', 1, 'RECEIPT_RETURNED', '*', 'DIRECT_BILL', '*', '*', 'LA-11', 'LA-10', 'AMOUNT', DATE '2000-01-01', NULL, 'BO-BIL-06b (direct debit returned by the bank: the mirror of the RECEIVED rule)');
        """;

    public const string Down = """
        DELETE FROM bil.ledger_rule WHERE rule_id = 'BLR-RECEIPT-RETURNED';
        """;
}
