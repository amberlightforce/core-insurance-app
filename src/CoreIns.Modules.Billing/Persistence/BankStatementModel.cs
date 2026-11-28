using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Persistence;

/// <summary>Statement line states and directions (BO-BIL-11).</summary>
internal static class BankLineStates
{
    /// <summary>Credit matched to a receipt of the right amount, or debit matched to a disbursement.</summary>
    public const string Matched = "MATCHED";

    /// <summary>The reference identified the receipt or invoice, but the amount differs; the cash waits in suspense or the difference is a break.</summary>
    public const string Partial = "PARTIAL";

    /// <summary>A credit that could not be identified was recorded as unapplied cash in suspense.</summary>
    public const string Suspense = "SUSPENSE";

    /// <summary>Nothing could be matched or recorded; a person must look at the line.</summary>
    public const string Unmatched = "UNMATCHED";

    public const string Credit = "CREDIT";
    public const string Debit = "DEBIT";
}

/// <summary>
/// An imported bank statement of an own bank account (BO-BIL-11). Append-only: the same content (hash) is imported once per account, and the
/// same statement reference never twice with different content. The file (third-party IBANs and names) is kept in the bank-file store, not here.
/// </summary>
internal sealed class BankStatementRow
{
    public Guid StatementId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    /// <summary>The own bank account (the insurer's IBAN, not personal data).</summary>
    public string BankAccount { get; set; } = string.Empty;

    public string StatementRef { get; set; } = string.Empty;

    public string Format { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public string FileRef { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    public BusinessDate FromDate { get; set; }

    public BusinessDate ToDate { get; set; }

    public decimal? OpeningBalance { get; set; }

    public decimal? ClosingBalance { get; set; }

    public int LineCount { get; set; }

    public Instant ImportedAt { get; set; }

    public string ImportedBy { get; set; } = string.Empty;
}

/// <summary>A line of a statement with the outcome of its match. The amount is positive; <see cref="Direction"/> says which way the money moved.</summary>
internal sealed class BankStatementLineRow
{
    public Guid LineId { get; set; }

    public Guid StatementId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public int LineNo { get; set; }

    public string Direction { get; set; } = string.Empty;

    public BusinessDate BookingDate { get; set; }

    public BusinessDate? ValueDate { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    /// <summary>Remittance text (may name the payer's invoice).</summary>
    public string? Reference { get; set; }

    public string? EndToEndId { get; set; }

    public string Status { get; set; } = string.Empty;

    public decimal MatchedAmount { get; set; }

    public PaymentId? ReceiptId { get; set; }

    public DisbursementId? DisbursementId { get; set; }

    public Guid? SuspenseItemId { get; set; }

    public string? Note { get; set; }
}

internal static class BankStatementModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<BankStatementRow>(e =>
        {
            e.ToTable("bank_statement", t =>
            {
                t.HasCheckConstraint("ck_bank_statement_format", "format IN ('CAMT053', 'CSV')");
                t.HasCheckConstraint("ck_bank_statement_hash", "content_hash ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("ck_bank_statement_currency", "currency ~ '^[A-Z]{3}$'");
                t.HasCheckConstraint("ck_bank_statement_period", "to_date >= from_date");
                t.HasCheckConstraint("ck_bank_statement_lines", "line_count >= 0");
            });
            e.HasKey(r => r.StatementId).HasName("pk_bank_statement");
            SnakeCase(e);
            e.Property(r => r.Currency).HasColumnType("char(3)");
            e.Property(r => r.ContentHash).HasColumnType("char(64)");
            e.Property(r => r.OpeningBalance).HasColumnType("numeric(19,4)");
            e.Property(r => r.ClosingBalance).HasColumnType("numeric(19,4)");
            e.Property(r => r.ImportedAt).HasColumnType("timestamptz");
            // Re-importing the same file is one statement; the same reference with other content is refused by the handler (and here, as a backstop).
            e.HasIndex(r => new { r.LegalEntityId, r.BankAccount, r.ContentHash }).IsUnique().HasDatabaseName("ux_bank_statement_content");
            e.HasIndex(r => new { r.LegalEntityId, r.BankAccount, r.StatementRef }).IsUnique().HasDatabaseName("ux_bank_statement_ref");
        });

        model.Entity<BankStatementLineRow>(e =>
        {
            e.ToTable("bank_statement_line", t =>
            {
                t.HasCheckConstraint("ck_bank_statement_line_direction", "direction IN ('CREDIT', 'DEBIT')");
                t.HasCheckConstraint("ck_bank_statement_line_status", "status IN ('MATCHED', 'PARTIAL', 'SUSPENSE', 'UNMATCHED')");
                t.HasCheckConstraint("ck_bank_statement_line_amount", "amount > 0 AND matched_amount >= 0");
                t.HasCheckConstraint("ck_bank_statement_line_currency", "currency ~ '^[A-Z]{3}$'");
            });
            e.HasKey(r => r.LineId).HasName("pk_bank_statement_line");
            SnakeCase(e);
            e.Property(r => r.Amount).HasColumnType("numeric(19,4)");
            e.Property(r => r.MatchedAmount).HasColumnType("numeric(19,4)");
            e.Property(r => r.Currency).HasColumnType("char(3)");
            e.HasIndex(r => new { r.StatementId, r.LineNo }).IsUnique().HasDatabaseName("ux_bank_statement_line_no");
            e.HasIndex(r => r.ReceiptId).HasDatabaseName("ix_bank_statement_line_receipt");
            e.HasIndex(r => r.DisbursementId).HasDatabaseName("ix_bank_statement_line_disbursement");
            e.HasOne<BankStatementRow>().WithMany().HasForeignKey(r => r.StatementId).HasConstraintName("fk_bank_statement_line_statement").OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ReceiptRow>().WithMany().HasForeignKey(r => r.ReceiptId).HasConstraintName("fk_bank_statement_line_receipt").OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DisbursementRow>().WithMany().HasForeignKey(r => r.DisbursementId).HasConstraintName("fk_bank_statement_line_disbursement").OnDelete(DeleteBehavior.Restrict);
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
