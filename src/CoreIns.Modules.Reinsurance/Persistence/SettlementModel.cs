using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Reinsurance.Persistence;

/// <summary>
/// <c>ri.settlement</c>: the settlement of the net balance of one reinsurer (BO-RI-16, D-CON-13). The net is signed (positive is payable to the
/// reinsurer, negative receivable from it) and sealed at proposal; the status moves PROPOSED, APPROVED, then RELEASED (system path) or RECORDED
/// (off-system movement with evidence). One live (PROPOSED or APPROVED) settlement per reinsurer and currency.
/// </summary>
internal sealed class SettlementRow
{
    public Guid SettlementId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public Guid ReinsurerPartyId { get; set; }

    public string Currency { get; set; } = string.Empty;

    /// <summary>The period (yyyy-MM) the net runs through.</summary>
    public string Period { get; set; } = string.Empty;

    /// <summary>The live statement of that period at proposal, when there was one.</summary>
    public Guid? StatementId { get; set; }

    public decimal? StatementClosing { get; set; }

    /// <summary>Signed balance: positive is payable to the reinsurer, negative receivable from it.</summary>
    public decimal Net { get; set; }

    public decimal Amount { get; set; }

    /// <summary>PAY or RECEIVE.</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>PROPOSED, APPROVED, RELEASED, RECORDED or SUPERSEDED.</summary>
    public string Status { get; set; } = string.Empty;

    public Instant ProposedAt { get; set; }

    public string ProposedBy { get; set; } = string.Empty;

    public Instant? ApprovedAt { get; set; }

    public string? ApprovedBy { get; set; }

    public string? ApprovalComment { get; set; }

    /// <summary>The platform authority check of the approval (RI.SETTLEMENT).</summary>
    public Guid? AuthorityCheckId { get; set; }

    public Instant? SettledAt { get; set; }

    public string? SettledBy { get; set; }

    public BusinessDate? ValueDate { get; set; }

    public string? BankReference { get; set; }

    public string? EvidenceRef { get; set; }

    /// <summary>The account item the movement posted.</summary>
    public Guid? ItemId { get; set; }

    public string? BilReference { get; set; }
}

/// <summary>The EF model of <c>ri.settlement</c>.</summary>
internal static class SettlementModel
{
    public static void Configure(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<SettlementRow>(entity =>
        {
            entity.ToTable("settlement", table =>
            {
                table.HasCheckConstraint("ck_settlement_status", "status IN ('PROPOSED', 'APPROVED', 'RELEASED', 'RECORDED', 'SUPERSEDED')");
                table.HasCheckConstraint("ck_settlement_direction", "direction IN ('PAY', 'RECEIVE')");
                table.HasCheckConstraint("ck_settlement_amount", "amount > 0 AND amount = abs(net)");
            });
            entity.HasKey(e => e.SettlementId).HasName("pk_settlement");
            entity.Property(e => e.SettlementId).HasColumnName("settlement_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.ReinsurerPartyId).HasColumnName("reinsurer_party_id");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.Period).HasColumnName("period");
            entity.Property(e => e.StatementId).HasColumnName("statement_id");
            entity.Property(e => e.StatementClosing).HasColumnName("statement_closing").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Net).HasColumnName("net").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Amount).HasColumnName("amount").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Direction).HasColumnName("direction");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.ProposedAt).HasColumnName("proposed_at").HasColumnType("timestamptz");
            entity.Property(e => e.ProposedBy).HasColumnName("proposed_by");
            entity.Property(e => e.ApprovedAt).HasColumnName("approved_at").HasColumnType("timestamptz");
            entity.Property(e => e.ApprovedBy).HasColumnName("approved_by");
            entity.Property(e => e.ApprovalComment).HasColumnName("approval_comment");
            entity.Property(e => e.AuthorityCheckId).HasColumnName("authority_check_id");
            entity.Property(e => e.SettledAt).HasColumnName("settled_at").HasColumnType("timestamptz");
            entity.Property(e => e.SettledBy).HasColumnName("settled_by");
            entity.Property(e => e.ValueDate).HasColumnName("value_date").HasColumnType("date");
            entity.Property(e => e.BankReference).HasColumnName("bank_reference");
            entity.Property(e => e.EvidenceRef).HasColumnName("evidence_ref");
            entity.Property(e => e.ItemId).HasColumnName("item_id");
            entity.Property(e => e.BilReference).HasColumnName("bil_reference");
            entity.HasIndex(e => new { e.LegalEntityId, e.ReinsurerPartyId, e.Currency }).IsUnique().HasFilter("status IN ('PROPOSED', 'APPROVED')").HasDatabaseName("ux_settlement_live");
            // NX-EC-08: a bank reference is booked once per reinsurer in the legal entity.
            entity.HasIndex(e => new { e.LegalEntityId, e.ReinsurerPartyId, e.BankReference }).IsUnique().HasFilter("bank_reference IS NOT NULL").HasDatabaseName("ux_settlement_reference");
            entity.HasIndex(e => new { e.LegalEntityId, e.Status }).HasDatabaseName("ix_settlement_status");
        });
}

/// <summary>The SQL the settlement migration adds beside the EF model: the net and identity of a settlement are sealed, rows are never deleted.</summary>
internal static class SettlementDatabaseSql
{
    public const string Up = """
        CREATE FUNCTION ri.settlement_guard() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'ri.settlement rows are never deleted (BO-RI-16)' USING ERRCODE = '42501';
            END IF;
            IF NEW.settlement_id <> OLD.settlement_id OR NEW.legal_entity_id <> OLD.legal_entity_id OR NEW.reinsurer_party_id <> OLD.reinsurer_party_id
               OR NEW.currency <> OLD.currency OR NEW.period <> OLD.period OR NEW.net <> OLD.net OR NEW.amount <> OLD.amount OR NEW.direction <> OLD.direction
               OR NEW.proposed_at <> OLD.proposed_at OR NEW.proposed_by <> OLD.proposed_by THEN
                RAISE EXCEPTION 'ri.settlement content is sealed at proposal (BO-RI-16)' USING ERRCODE = '42501';
            END IF;
            IF OLD.status IN ('RELEASED', 'RECORDED') THEN
                RAISE EXCEPTION 'a settled ri.settlement is sealed (BO-RI-16)' USING ERRCODE = '42501';
            END IF;
            RETURN NEW;
        END
        $$;

        CREATE TRIGGER tr_settlement_guard BEFORE UPDATE OR DELETE ON ri.settlement FOR EACH ROW EXECUTE FUNCTION ri.settlement_guard();
        CREATE TRIGGER tr_settlement_no_truncate BEFORE TRUNCATE ON ri.settlement FOR EACH STATEMENT EXECUTE FUNCTION ri.content_append_only();

        """;

    public const string Down = """
        DROP TRIGGER IF EXISTS tr_settlement_no_truncate ON ri.settlement;
        DROP TRIGGER IF EXISTS tr_settlement_guard ON ri.settlement;
        DROP FUNCTION IF EXISTS ri.settlement_guard();

        """;
}
