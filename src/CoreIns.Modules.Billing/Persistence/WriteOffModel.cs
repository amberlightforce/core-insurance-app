using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Persistence;

/// <summary>States, kinds and the authority of write-offs (BO-BIL-13).</summary>
internal static class WriteOffStates
{
    public const string Proposed = "PROPOSED";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Posted = "POSTED";

    /// <summary>Written off by a second person's decision.</summary>
    public const string KindManual = "MANUAL";

    /// <summary>A small residual within the MKT tolerance, written off at once without a second person.</summary>
    public const string KindTolerance = "TOLERANCE";

    /// <summary>Ledger rule qualifiers of the WRITTEN_OFF entry.</summary>
    public const string QualifierWriteOff = "WRITE_OFF";

    public const string QualifierTolerance = "TOLERANCE";
}

/// <summary>
/// A write-off of part or all of the open balance of one invoice (BO-BIL-13): proposed by one person, decided by another (maker-checker,
/// BIL.WRITE_OFF authority over the amount), then posted. The row changes state; what it did is the append-only <see cref="WriteOffLineRow"/>s
/// and the ledger entry it points at.
/// </summary>
internal sealed class WriteOffRow
{
    public Guid WriteOffId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public BillingAccountId BillingAccountId { get; set; }

    public InvoiceId InvoiceId { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string ReasonCode { get; set; } = string.Empty;

    public string? Comment { get; set; }

    public string ProposedBy { get; set; } = string.Empty;

    public Instant ProposedAt { get; set; }

    public string? DecidedBy { get; set; }

    public Instant? DecidedAt { get; set; }

    public string? DecisionComment { get; set; }

    /// <summary>The authority check of the decision (plt.AuthorityCheck).</summary>
    public Guid? AuthorityCheckId { get; set; }

    /// <summary>The tolerance in force when a TOLERANCE write-off was made (audit of the MKT value used).</summary>
    public decimal? ToleranceLimit { get; set; }

    public string? PostedBy { get; set; }

    public Instant? PostedAt { get; set; }

    public Guid? LedgerEntryId { get; set; }

    public int RecordVersion { get; set; }
}

/// <summary>The part of a posted write-off that falls on one invoice item. Append-only; open amounts of an item are net of these rows.</summary>
internal sealed class WriteOffLineRow
{
    public Guid WriteOffLineId { get; set; }

    public Guid WriteOffId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public InvoiceId InvoiceId { get; set; }

    public Guid InvoiceItemId { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;
}

internal static class WriteOffModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<WriteOffRow>(e =>
        {
            e.ToTable("write_off", t =>
            {
                t.HasCheckConstraint("ck_write_off_state", "state IN ('PROPOSED', 'APPROVED', 'REJECTED', 'POSTED')");
                t.HasCheckConstraint("ck_write_off_kind", "kind IN ('MANUAL', 'TOLERANCE')");
                t.HasCheckConstraint("ck_write_off_amount", "amount > 0");
                t.HasCheckConstraint("ck_write_off_currency", "currency ~ '^[A-Z]{3}$'");
                t.HasCheckConstraint("ck_write_off_record_version", "record_version >= 1");
                t.HasCheckConstraint("ck_write_off_posted", "(state = 'POSTED') = (ledger_entry_id IS NOT NULL)");
                t.HasCheckConstraint("ck_write_off_decided", "state = 'PROPOSED' OR decided_by IS NOT NULL");
            });
            e.HasKey(r => r.WriteOffId).HasName("pk_write_off");
            SnakeCase(e);
            e.Property(r => r.Amount).HasColumnType("numeric(19,4)");
            e.Property(r => r.ToleranceLimit).HasColumnType("numeric(19,4)");
            e.Property(r => r.Currency).HasColumnType("char(3)");
            e.Property(r => r.ProposedAt).HasColumnType("timestamptz");
            e.Property(r => r.DecidedAt).HasColumnType("timestamptz");
            e.Property(r => r.PostedAt).HasColumnType("timestamptz");
            e.Property(r => r.RecordVersion).IsConcurrencyToken();
            e.HasIndex(r => new { r.LegalEntityId, r.State, r.ProposedAt }).HasDatabaseName("ix_write_off_state");
            e.HasIndex(r => r.InvoiceId).HasDatabaseName("ix_write_off_invoice");
            e.HasIndex(r => r.BillingAccountId).HasDatabaseName("ix_write_off_account");
            e.HasOne<InvoiceRow>().WithMany().HasForeignKey(r => r.InvoiceId).HasConstraintName("fk_write_off_invoice").OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BillingAccountRow>().WithMany().HasForeignKey(r => r.BillingAccountId).HasConstraintName("fk_write_off_account").OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<WriteOffLineRow>(e =>
        {
            e.ToTable("write_off_line", t =>
            {
                t.HasCheckConstraint("ck_write_off_line_amount", "amount > 0");
                t.HasCheckConstraint("ck_write_off_line_currency", "currency ~ '^[A-Z]{3}$'");
            });
            e.HasKey(r => r.WriteOffLineId).HasName("pk_write_off_line");
            SnakeCase(e);
            e.Property(r => r.Amount).HasColumnType("numeric(19,4)");
            e.Property(r => r.Currency).HasColumnType("char(3)");
            e.HasIndex(r => r.InvoiceItemId).HasDatabaseName("ix_write_off_line_item");
            e.HasIndex(r => new { r.WriteOffId, r.InvoiceItemId }).IsUnique().HasDatabaseName("ux_write_off_line_item");
            e.HasOne<WriteOffRow>().WithMany().HasForeignKey(r => r.WriteOffId).HasConstraintName("fk_write_off_line_write_off").OnDelete(DeleteBehavior.Restrict);
            e.HasOne<InvoiceRow>().WithMany().HasForeignKey(r => r.InvoiceId).HasConstraintName("fk_write_off_line_invoice").OnDelete(DeleteBehavior.Restrict);
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
/// BO-BIL-13 database part (data and guards): the ledger rules of a write-off (<c>WRITTEN_OFF</c>: billed receivable LA-02 against the
/// write-off clearing LA-16, or the tolerance clearing LA-19 for a residual within tolerance) and of a money transfer (<c>MONEY_TRANSFERRED</c>:
/// unapplied cash LA-11 of the source through the inter-account clearing LA-20 to LA-11 of the target); the write-off lines are append-only
/// and may not push the cash, credit and write-off on an item above the item.
/// </summary>
internal static class WriteOffSql
{
    public const string Up = """
        INSERT INTO bil.ledger_rule (rule_id, version, event_type, charge_category, bill_mode, jurisdiction, qualifier,
            debit_account, credit_account, amount_expression, valid_from, valid_to, source) VALUES
            ('BLR-WRITTEN-OFF', 1, 'WRITTEN_OFF', '*', 'DIRECT_BILL', '*', 'WRITE_OFF', 'LA-16', 'LA-02', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-217..220, BO-BIL-13 (write-off: billed receivable to write-off clearing)'),
            ('BLR-WRITTEN-OFF-TOLERANCE', 1, 'WRITTEN_OFF', '*', 'DIRECT_BILL', '*', 'TOLERANCE', 'LA-19', 'LA-02', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-217..220, BO-BIL-13 (residual within tolerance: billed receivable to tolerance clearing)'),
            ('BLR-MONEY-XFER-OUT', 1, 'MONEY_TRANSFERRED', '*', 'DIRECT_BILL', '*', 'OUT', 'LA-11', 'LA-20', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-225..227, BO-BIL-13 (unapplied cash leaves the source account)'),
            ('BLR-MONEY-XFER-IN', 1, 'MONEY_TRANSFERRED', '*', 'DIRECT_BILL', '*', 'IN', 'LA-20', 'LA-11', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-225..227, BO-BIL-13 (unapplied cash arrives on the target account)');

        CREATE TRIGGER tr_write_off_line_append_only BEFORE UPDATE OR DELETE ON bil.write_off_line FOR EACH ROW EXECUTE FUNCTION bil.reject_change();

        -- Cash, credit and write-off on one item never exceed the item (the allocation guard checks the same sum from its side).
        CREATE FUNCTION bil.guard_write_off_line() RETURNS trigger LANGUAGE plpgsql AS $fn$
        DECLARE
            item_amount numeric;
            covered numeric;
        BEGIN
            SELECT amount INTO item_amount FROM bil.invoice_item WHERE invoice_item_id = NEW.invoice_item_id;
            IF item_amount IS NULL THEN
                RAISE EXCEPTION 'a write-off line must name an invoice item (BO-BIL-13)' USING ERRCODE = 'BL001';
            END IF;
            SELECT coalesce((SELECT sum(amount) FROM bil.allocation WHERE invoice_item_id = NEW.invoice_item_id), 0)
                 + coalesce((SELECT sum(amount) FROM bil.credit_application WHERE target_invoice_item_id = NEW.invoice_item_id), 0)
                 + coalesce((SELECT sum(amount) FROM bil.write_off_line WHERE invoice_item_id = NEW.invoice_item_id), 0)
              INTO covered;
            IF covered + NEW.amount > item_amount THEN
                RAISE EXCEPTION 'a write-off may not exceed the open amount of the invoice item (BO-BIL-13)' USING ERRCODE = 'BL001';
            END IF;
            RETURN NEW;
        END
        $fn$;
        CREATE TRIGGER tr_write_off_line_guard BEFORE INSERT ON bil.write_off_line FOR EACH ROW EXECUTE FUNCTION bil.guard_write_off_line();
        """;

    public const string Down = """
        DROP TRIGGER IF EXISTS tr_write_off_line_guard ON bil.write_off_line;
        DROP FUNCTION IF EXISTS bil.guard_write_off_line();
        DROP TRIGGER IF EXISTS tr_write_off_line_append_only ON bil.write_off_line;
        DELETE FROM bil.ledger_rule WHERE rule_id IN ('BLR-WRITTEN-OFF', 'BLR-WRITTEN-OFF-TOLERANCE', 'BLR-MONEY-XFER-OUT', 'BLR-MONEY-XFER-IN');
        """;
}
