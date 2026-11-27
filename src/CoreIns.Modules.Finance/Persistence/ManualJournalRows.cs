using CoreIns.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.Persistence;

/// <summary>
/// <c>fin.manual_journal</c>: a manual journal proposal (BO-FIN-01a). It is a workflow record, not the ledger: it is
/// mutable only along the status graph enforced by a trigger. Posting it (BO-FIN-01b) writes an ordinary sealed journal
/// through the journal writer and records the posted journal id here; the posted journal itself is never touched (D-ARC-34).
/// </summary>
internal sealed class ManualJournalRow
{
    public Guid ManualJournalId { get; set; }

    public Guid LegalEntityId { get; set; }

    /// <summary>Database-assigned sequence; the business number is <c>MJ</c> + this value padded to six digits.</summary>
    public long NumberSeq { get; set; }

    public string Status { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Book { get; set; } = string.Empty;

    public BusinessDate AccountingDate { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string EvidenceRef { get; set; } = string.Empty;

    public string CreatedBy { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string? SubmittedBy { get; set; }

    public Instant? SubmittedAt { get; set; }

    public string? DecidedBy { get; set; }

    public Instant? DecidedAt { get; set; }

    public string? DecisionReason { get; set; }

    public Guid[] PostedJournalIds { get; set; } = [];

    public int RecordVersion { get; set; }

    public string Number => ManualJournalRowNumber.Format(NumberSeq);
}

internal static class ManualJournalRowNumber
{
    public static string Format(long seq) => "MJ" + seq.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary><c>fin.manual_journal_line</c>: one line of a manual journal; insert-only.</summary>
internal sealed class ManualJournalLineRow
{
    public Guid LineId { get; set; }

    public Guid ManualJournalId { get; set; }

    public int LineNo { get; set; }

    public string AccountCode { get; set; } = string.Empty;

    public string Side { get; set; } = string.Empty;

    public decimal Amount { get; set; }
}

internal static class ManualJournalModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<ManualJournalRow>(e =>
        {
            e.ToTable("manual_journal", t =>
            {
                t.HasCheckConstraint("ck_manual_journal_status", "status IN ('DRAFT', 'SUBMITTED', 'APPROVED', 'REJECTED', 'WITHDRAWN', 'POSTED')");
                t.HasCheckConstraint("ck_manual_journal_currency", "currency ~ '^[A-Z]{3}$'");
                t.HasCheckConstraint("ck_manual_journal_record_version", "record_version >= 1");
                t.HasCheckConstraint("ck_manual_journal_evidence", "length(btrim(evidence_ref)) > 0");
                t.HasCheckConstraint("ck_manual_journal_posted", "status <> 'POSTED' OR cardinality(posted_journal_ids) >= 1");
            });
            e.HasKey(r => r.ManualJournalId).HasName("pk_manual_journal");
            e.Property(r => r.ManualJournalId).HasColumnName("manual_journal_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.NumberSeq).HasColumnName("number_seq").ValueGeneratedOnAdd().UseIdentityAlwaysColumn();
            e.Property(r => r.Status).HasColumnName("status");
            e.Property(r => r.Description).HasColumnName("description");
            e.Property(r => r.Book).HasColumnName("book");
            e.Property(r => r.AccountingDate).HasColumnName("accounting_date");
            e.Property(r => r.Currency).HasColumnName("currency").HasColumnType("char(3)");
            e.Property(r => r.EvidenceRef).HasColumnName("evidence_ref");
            e.Property(r => r.CreatedBy).HasColumnName("created_by");
            e.Property(r => r.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(r => r.SubmittedBy).HasColumnName("submitted_by");
            e.Property(r => r.SubmittedAt).HasColumnName("submitted_at").HasColumnType("timestamptz");
            e.Property(r => r.DecidedBy).HasColumnName("decided_by");
            e.Property(r => r.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            e.Property(r => r.DecisionReason).HasColumnName("decision_reason");
            e.Property(r => r.PostedJournalIds).HasColumnName("posted_journal_ids");
            e.Property(r => r.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            e.Ignore(r => r.Number);
            e.HasIndex(r => new { r.LegalEntityId, r.NumberSeq }).IsUnique().HasDatabaseName("ux_manual_journal_number");
        });

        model.Entity<ManualJournalLineRow>(e =>
        {
            e.ToTable("manual_journal_line", t =>
            {
                t.HasCheckConstraint("ck_manual_journal_line_side", "side IN ('DEBIT', 'CREDIT')");
                t.HasCheckConstraint("ck_manual_journal_line_amount", "amount > 0");
                t.HasCheckConstraint("ck_manual_journal_line_no", "line_no >= 1");
            });
            e.HasKey(r => r.LineId).HasName("pk_manual_journal_line");
            e.Property(r => r.LineId).HasColumnName("line_id");
            e.Property(r => r.ManualJournalId).HasColumnName("manual_journal_id");
            e.Property(r => r.LineNo).HasColumnName("line_no");
            e.Property(r => r.AccountCode).HasColumnName("account_code");
            e.Property(r => r.Side).HasColumnName("side");
            e.Property(r => r.Amount).HasColumnName("amount").HasColumnType("numeric(19,4)");
            e.HasIndex(r => new { r.ManualJournalId, r.LineNo }).IsUnique().HasDatabaseName("ux_manual_journal_line_no");
            e.HasOne<ManualJournalRow>().WithMany().HasForeignKey(r => r.ManualJournalId).HasConstraintName("fk_manual_journal_line_journal").OnDelete(DeleteBehavior.Restrict);
        });
    }
}
