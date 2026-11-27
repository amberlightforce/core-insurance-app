using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Reinsurance.Persistence;

/// <summary>
/// <c>ri.cash_call</c>: a call on the reinsurers for the paid recoveries of one claim that exceed the threshold (BO-RI-15). The amounts are in the
/// lines; the status moves RAISED, SENT, PARTIALLY_SETTLED, SETTLED, or CANCELLED (only before any cash arrived). One live call per contract and claim.
/// </summary>
internal sealed class CashCallRow
{
    public Guid CashCallId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public RiContractId ContractId { get; set; }

    /// <summary>The claim id (text) the recoveries belong to; with the contract the idempotency key of the call.</summary>
    public string SourceKey { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    public decimal Total { get; set; }

    public decimal Threshold { get; set; }

    public BusinessDate DueDate { get; set; }

    /// <summary>RAISED, SENT, PARTIALLY_SETTLED, SETTLED or CANCELLED.</summary>
    public string Status { get; set; } = string.Empty;

    public Instant RaisedAt { get; set; }

    public string RaisedBy { get; set; } = string.Empty;

    public Instant? SentAt { get; set; }

    public string? SentBy { get; set; }

    public Instant? CancelledAt { get; set; }

    public string? CancelledBy { get; set; }

    public string? CancelReason { get; set; }

    public Instant? SettledAt { get; set; }
}

/// <summary><c>ri.cash_call_line</c>: the share of one reinsurer in a cash call and what it has paid so far.</summary>
internal sealed class CashCallLineRow
{
    public Guid CashCallId { get; set; }

    public Guid ReinsurerPartyId { get; set; }

    public decimal Amount { get; set; }

    public decimal Received { get; set; }
}

/// <summary><c>ri.cash_call_receipt</c>: cash received on a cash call, append-only; unique on (reinsurer, bank reference) so a replay books nothing twice.</summary>
internal sealed class CashCallReceiptRow
{
    public Guid ReceiptId { get; set; }

    public Guid CashCallId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public Guid ReinsurerPartyId { get; set; }

    public decimal Amount { get; set; }

    public string BankReference { get; set; } = string.Empty;

    public BusinessDate ReceivedOn { get; set; }

    /// <summary>The account item the receipt posted (positive RECOVERY item, source kind CASH_CALL_RECEIPT).</summary>
    public Guid ItemId { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>The EF model of the cash call tables.</summary>
internal static class CashCallModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CashCallRow>(entity =>
        {
            entity.ToTable("cash_call", table =>
            {
                table.HasCheckConstraint("ck_cash_call_status", "status IN ('RAISED', 'SENT', 'PARTIALLY_SETTLED', 'SETTLED', 'CANCELLED')");
                table.HasCheckConstraint("ck_cash_call_total", "total > 0");
            });
            entity.HasKey(e => e.CashCallId).HasName("pk_cash_call");
            entity.Property(e => e.CashCallId).HasColumnName("cash_call_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.ContractId).HasColumnName("contract_id");
            entity.Property(e => e.SourceKey).HasColumnName("source_key");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.Total).HasColumnName("total").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Threshold).HasColumnName("threshold").HasColumnType("numeric(19,4)");
            entity.Property(e => e.DueDate).HasColumnName("due_date").HasColumnType("date");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.RaisedAt).HasColumnName("raised_at").HasColumnType("timestamptz");
            entity.Property(e => e.RaisedBy).HasColumnName("raised_by");
            entity.Property(e => e.SentAt).HasColumnName("sent_at").HasColumnType("timestamptz");
            entity.Property(e => e.SentBy).HasColumnName("sent_by");
            entity.Property(e => e.CancelledAt).HasColumnName("cancelled_at").HasColumnType("timestamptz");
            entity.Property(e => e.CancelledBy).HasColumnName("cancelled_by");
            entity.Property(e => e.CancelReason).HasColumnName("cancel_reason");
            entity.Property(e => e.SettledAt).HasColumnName("settled_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.ContractId, e.SourceKey }).IsUnique().HasFilter("status <> 'CANCELLED'").HasDatabaseName("ux_cash_call_live");
            entity.HasIndex(e => new { e.LegalEntityId, e.Status }).HasDatabaseName("ix_cash_call_status");
            entity.HasOne<ContractRow>().WithMany().HasForeignKey(e => e.ContractId).HasConstraintName("fk_cash_call_contract").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CashCallLineRow>(entity =>
        {
            entity.ToTable("cash_call_line", table => table.HasCheckConstraint("ck_cash_call_line_received", "received >= 0 AND received <= amount"));
            entity.HasKey(e => new { e.CashCallId, e.ReinsurerPartyId }).HasName("pk_cash_call_line");
            entity.Property(e => e.CashCallId).HasColumnName("cash_call_id");
            entity.Property(e => e.ReinsurerPartyId).HasColumnName("reinsurer_party_id");
            entity.Property(e => e.Amount).HasColumnName("amount").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Received).HasColumnName("received").HasColumnType("numeric(19,4)");
            entity.HasOne<CashCallRow>().WithMany().HasForeignKey(e => e.CashCallId).HasConstraintName("fk_cash_call_line_call").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CashCallReceiptRow>(entity =>
        {
            entity.ToTable("cash_call_receipt", table => table.HasCheckConstraint("ck_cash_call_receipt_amount", "amount > 0"));
            entity.HasKey(e => e.ReceiptId).HasName("pk_cash_call_receipt");
            entity.Property(e => e.ReceiptId).HasColumnName("receipt_id");
            entity.Property(e => e.CashCallId).HasColumnName("cash_call_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.ReinsurerPartyId).HasColumnName("reinsurer_party_id");
            entity.Property(e => e.Amount).HasColumnName("amount").HasColumnType("numeric(19,4)");
            entity.Property(e => e.BankReference).HasColumnName("bank_reference");
            entity.Property(e => e.ReceivedOn).HasColumnName("received_on").HasColumnType("date");
            entity.Property(e => e.ItemId).HasColumnName("item_id");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            // NX-EC-08: a bank reference is booked once per reinsurer in the legal entity.
            entity.HasIndex(e => new { e.LegalEntityId, e.ReinsurerPartyId, e.BankReference }).IsUnique().HasDatabaseName("ux_cash_call_receipt_reference");
            entity.HasIndex(e => e.CashCallId).HasDatabaseName("ix_cash_call_receipt_call");
            entity.HasOne<CashCallRow>().WithMany().HasForeignKey(e => e.CashCallId).HasConstraintName("fk_cash_call_receipt_call").OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>The SQL the cash call migration adds beside the EF model: receipts and the identity of a call are sealed (PITFALLS 8, 17).</summary>
internal static class CashCallDatabaseSql
{
    public const string Up = """
        CREATE FUNCTION ri.cash_call_guard() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'ri.cash_call rows are never deleted (BO-RI-15)' USING ERRCODE = '42501';
            END IF;
            IF NEW.cash_call_id <> OLD.cash_call_id OR NEW.legal_entity_id <> OLD.legal_entity_id OR NEW.contract_id <> OLD.contract_id
               OR NEW.source_key <> OLD.source_key OR NEW.currency <> OLD.currency OR NEW.total <> OLD.total OR NEW.threshold <> OLD.threshold
               OR NEW.due_date <> OLD.due_date OR NEW.raised_at <> OLD.raised_at OR NEW.raised_by <> OLD.raised_by THEN
                RAISE EXCEPTION 'ri.cash_call content is sealed at raising (BO-RI-15)' USING ERRCODE = '42501';
            END IF;
            RETURN NEW;
        END
        $$;

        CREATE TRIGGER tr_cash_call_guard BEFORE UPDATE OR DELETE ON ri.cash_call FOR EACH ROW EXECUTE FUNCTION ri.cash_call_guard();
        CREATE TRIGGER tr_cash_call_no_truncate BEFORE TRUNCATE ON ri.cash_call FOR EACH STATEMENT EXECUTE FUNCTION ri.content_append_only();
        CREATE TRIGGER tr_cash_call_receipt_seal BEFORE UPDATE OR DELETE ON ri.cash_call_receipt FOR EACH ROW EXECUTE FUNCTION ri.content_append_only();
        CREATE TRIGGER tr_cash_call_receipt_no_truncate BEFORE TRUNCATE ON ri.cash_call_receipt FOR EACH STATEMENT EXECUTE FUNCTION ri.content_append_only();

        """;

    public const string Down = """
        DROP TRIGGER IF EXISTS tr_cash_call_receipt_no_truncate ON ri.cash_call_receipt;
        DROP TRIGGER IF EXISTS tr_cash_call_receipt_seal ON ri.cash_call_receipt;
        DROP TRIGGER IF EXISTS tr_cash_call_no_truncate ON ri.cash_call;
        DROP TRIGGER IF EXISTS tr_cash_call_guard ON ri.cash_call;
        DROP FUNCTION IF EXISTS ri.cash_call_guard();

        """;
}
