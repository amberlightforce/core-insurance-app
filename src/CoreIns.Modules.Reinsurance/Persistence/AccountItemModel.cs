using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Reinsurance.Persistence;

/// <summary>
/// <c>ri.account_item</c>: one line of the technical account of a reinsurer on a contract (BO-RI-11). Append-only for every writer;
/// a correction is a reversal row (<see cref="ReversalOf"/>), never an edit. Positive is payable by the cedant to the reinsurer.
/// Idempotent on (kind, source id, reinsurer): a source produces its item once.
/// </summary>
internal sealed class AccountItemRow
{
    public Guid ItemId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public RiContractId ContractId { get; set; }

    public int ContractYear { get; set; }

    public int? LayerNo { get; set; }

    public Guid ReinsurerPartyId { get; set; }

    /// <summary>CEDED_PREMIUM, DEPOSIT_INSTALMENT, PREMIUM_ADJUSTMENT, COMMISSION or RECOVERY.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The kind of row the item was produced from (cession, deposit schedule, adjustment, commission, recovery, reversal).</summary>
    public string SourceKind { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    /// <summary>Items that replace one another's target share a group (a deposit instalment of a layer across schedule revisions).</summary>
    public string SourceGroup { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    /// <summary>Accounting period, yyyy-MM.</summary>
    public string Period { get; set; } = string.Empty;

    public BusinessDate ItemDate { get; set; }

    public Guid? ReversalOf { get; set; }

    public string? Reason { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>The EF model of <c>ri.account_item</c>.</summary>
internal static class AccountItemModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountItemRow>(entity =>
        {
            entity.ToTable("account_item", table =>
            {
                table.HasCheckConstraint("ck_account_item_kind", "kind IN ('CEDED_PREMIUM', 'DEPOSIT_INSTALMENT', 'PREMIUM_ADJUSTMENT', 'COMMISSION', 'RECOVERY')");
                table.HasCheckConstraint("ck_account_item_period", "period ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'");
            });
            entity.HasKey(e => e.ItemId).HasName("pk_account_item");
            entity.Property(e => e.ItemId).HasColumnName("item_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.ContractId).HasColumnName("contract_id");
            entity.Property(e => e.ContractYear).HasColumnName("contract_year");
            entity.Property(e => e.LayerNo).HasColumnName("layer_no");
            entity.Property(e => e.ReinsurerPartyId).HasColumnName("reinsurer_party_id");
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.SourceKind).HasColumnName("source_kind");
            entity.Property(e => e.SourceId).HasColumnName("source_id");
            entity.Property(e => e.SourceGroup).HasColumnName("source_group");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.Amount).HasColumnName("amount").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Period).HasColumnName("period");
            entity.Property(e => e.ItemDate).HasColumnName("item_date").HasColumnType("date");
            entity.Property(e => e.ReversalOf).HasColumnName("reversal_of");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => new { e.Kind, e.SourceId, e.ReinsurerPartyId }).IsUnique().HasDatabaseName("ux_account_item_source");
            entity.HasIndex(e => e.ReversalOf).IsUnique().HasFilter("reversal_of IS NOT NULL").HasDatabaseName("ux_account_item_reversal");
            entity.HasIndex(e => new { e.ContractId, e.Period }).HasDatabaseName("ix_account_item_contract");
            entity.HasIndex(e => new { e.LegalEntityId, e.ReinsurerPartyId, e.Period }).HasDatabaseName("ix_account_item_reinsurer");
            entity.HasOne<ContractRow>().WithMany().HasForeignKey(e => e.ContractId).HasConstraintName("fk_account_item_contract").OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>The SQL the account item migration adds beside the EF model: the seal triggers (PITFALLS 8, 17). Nothing is updated or deleted.</summary>
internal static class AccountItemDatabaseSql
{
    public const string Up =
        "CREATE TRIGGER tr_account_item_seal BEFORE UPDATE OR DELETE ON ri.account_item FOR EACH ROW EXECUTE FUNCTION ri.content_append_only();\n" +
        "CREATE TRIGGER tr_account_item_no_truncate BEFORE TRUNCATE ON ri.account_item FOR EACH STATEMENT EXECUTE FUNCTION ri.content_append_only();\n";

    public const string Down =
        "DROP TRIGGER IF EXISTS tr_account_item_seal ON ri.account_item;\nDROP TRIGGER IF EXISTS tr_account_item_no_truncate ON ri.account_item;\n";
}
