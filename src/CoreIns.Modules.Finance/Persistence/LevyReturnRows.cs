using CoreIns.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.Persistence;

/// <summary>
/// <c>fin.levy_return</c> (BO-FIN-10): one version of the levy return of a levy type (authority) and period. The figures are computed from the ledger and are
/// frozen once the return leaves DRAFT (a trigger refuses a change); an amendment is a new row with the next version number that
/// <see cref="SupersedesReturnId"/> links to the original. Only the workflow columns change after review.
/// </summary>
internal sealed class LevyReturnRow
{
    public Guid ReturnId { get; set; }

    public Guid LegalEntityId { get; set; }

    public string PeriodCode { get; set; } = string.Empty;

    public string LevyType { get; set; } = string.Empty;

    public int VersionNo { get; set; }

    public Guid? SupersedesReturnId { get; set; }

    public Guid? SupersededByReturnId { get; set; }

    public string Status { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    public decimal LedgerAmount { get; set; }

    public decimal BilAmount { get; set; }

    public decimal AdjustmentsTotal { get; set; }

    public decimal NetPayable { get; set; }

    public int EntryCount { get; set; }

    public string? ReconExplanation { get; set; }

    public string LegalStatus { get; set; } = string.Empty;

    public bool Provisional { get; set; }

    public string? ContentHash { get; set; }

    public string? FileName { get; set; }

    public string? FileHash { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    public string? SubmittedBy { get; set; }

    public Instant? SubmittedAt { get; set; }

    public string? ReviewedBy { get; set; }

    public Instant? ReviewedAt { get; set; }

    public string? FiledBy { get; set; }

    public Instant? FiledAt { get; set; }

    public BusinessDate? FiledOn { get; set; }

    public string? FilingReference { get; set; }

    public Guid? DisbursementId { get; set; }

    public string? PaymentStatus { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public int RecordVersion { get; set; }
}

/// <summary><c>fin.levy_return_adjustment</c>: a manual adjustment of a DRAFT return. Insert-only (audited with its maker, time and reason).</summary>
internal sealed class LevyReturnAdjustmentRow
{
    public Guid AdjustmentId { get; set; }

    public Guid ReturnId { get; set; }

    public decimal Amount { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string? EvidenceRef { get; set; }

    public bool ExplainsReconciliation { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }
}

internal static class LevyReturnModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<LevyReturnRow>(e =>
        {
            e.ToTable("levy_return", t =>
            {
                t.HasCheckConstraint("ck_levy_return_status", "status IN ('DRAFT','REVIEWED','FILED','PAID')");
                t.HasCheckConstraint("ck_levy_return_period", "period_code ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'");
                t.HasCheckConstraint("ck_levy_return_currency", "currency ~ '^[A-Z]{3}$'");
                t.HasCheckConstraint("ck_levy_return_version", "version_no >= 1");
                t.HasCheckConstraint("ck_levy_return_hash", "content_hash IS NULL OR content_hash ~ '^[0-9a-f]{64}$'");
            });
            e.HasKey(r => r.ReturnId).HasName("pk_levy_return");
            e.Property(r => r.ReturnId).HasColumnName("return_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.PeriodCode).HasColumnName("period_code");
            e.Property(r => r.LevyType).HasColumnName("levy_type");
            e.Property(r => r.VersionNo).HasColumnName("version_no");
            e.Property(r => r.SupersedesReturnId).HasColumnName("supersedes_return_id");
            e.Property(r => r.SupersededByReturnId).HasColumnName("superseded_by_return_id");
            e.Property(r => r.Status).HasColumnName("status");
            e.Property(r => r.Currency).HasColumnName("currency").HasColumnType("char(3)");
            e.Property(r => r.LedgerAmount).HasColumnName("ledger_amount").HasColumnType("numeric(19,2)");
            e.Property(r => r.BilAmount).HasColumnName("bil_amount").HasColumnType("numeric(19,2)");
            e.Property(r => r.AdjustmentsTotal).HasColumnName("adjustments_total").HasColumnType("numeric(19,2)");
            e.Property(r => r.NetPayable).HasColumnName("net_payable").HasColumnType("numeric(19,2)");
            e.Property(r => r.EntryCount).HasColumnName("entry_count");
            e.Property(r => r.ReconExplanation).HasColumnName("recon_explanation");
            e.Property(r => r.LegalStatus).HasColumnName("legal_status");
            e.Property(r => r.Provisional).HasColumnName("provisional");
            e.Property(r => r.ContentHash).HasColumnName("content_hash");
            e.Property(r => r.FileName).HasColumnName("file_name");
            e.Property(r => r.FileHash).HasColumnName("file_hash");
            e.Property(r => r.ApprovalRequestId).HasColumnName("approval_request_id");
            e.Property(r => r.SubmittedBy).HasColumnName("submitted_by");
            e.Property(r => r.SubmittedAt).HasColumnName("submitted_at").HasColumnType("timestamptz");
            e.Property(r => r.ReviewedBy).HasColumnName("reviewed_by");
            e.Property(r => r.ReviewedAt).HasColumnName("reviewed_at").HasColumnType("timestamptz");
            e.Property(r => r.FiledBy).HasColumnName("filed_by");
            e.Property(r => r.FiledAt).HasColumnName("filed_at").HasColumnType("timestamptz");
            e.Property(r => r.FiledOn).HasColumnName("filed_on");
            e.Property(r => r.FilingReference).HasColumnName("filing_reference");
            e.Property(r => r.DisbursementId).HasColumnName("disbursement_id");
            e.Property(r => r.PaymentStatus).HasColumnName("payment_status");
            e.Property(r => r.CreatedBy).HasColumnName("created_by");
            e.Property(r => r.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(r => r.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            e.HasIndex(r => new { r.LegalEntityId, r.PeriodCode, r.LevyType, r.VersionNo }).IsUnique().HasDatabaseName("ux_levy_return_version");
            e.HasIndex(r => new { r.LegalEntityId, r.PeriodCode, r.Status }).HasDatabaseName("ix_levy_return_period");
        });

        model.Entity<LevyReturnAdjustmentRow>(e =>
        {
            e.ToTable("levy_return_adjustment", t => t.HasCheckConstraint("ck_levy_return_adjustment_amount", "amount <> 0"));
            e.HasKey(r => r.AdjustmentId).HasName("pk_levy_return_adjustment");
            e.Property(r => r.AdjustmentId).HasColumnName("adjustment_id");
            e.Property(r => r.ReturnId).HasColumnName("return_id");
            e.Property(r => r.Amount).HasColumnName("amount").HasColumnType("numeric(19,2)");
            e.Property(r => r.Reason).HasColumnName("reason");
            e.Property(r => r.EvidenceRef).HasColumnName("evidence_ref");
            e.Property(r => r.ExplainsReconciliation).HasColumnName("explains_reconciliation");
            e.Property(r => r.CreatedBy).HasColumnName("created_by");
            e.Property(r => r.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.HasIndex(r => r.ReturnId).HasDatabaseName("ix_levy_return_adjustment_return");
            e.HasOne<LevyReturnRow>().WithMany().HasForeignKey(r => r.ReturnId).HasConstraintName("fk_levy_return_adjustment_return").OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>Triggers of the return tables: a return that has left DRAFT is frozen, and an adjustment is added only to a DRAFT.</summary>
internal static class LevyReturnSql
{
    public const string Triggers =
        """
        CREATE FUNCTION fin.levy_return_freeze() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'fin.levy_return: a return is never deleted' USING ERRCODE = '55000';
            END IF;
            IF OLD.status <> 'DRAFT' AND (
                NEW.period_code IS DISTINCT FROM OLD.period_code OR NEW.levy_type IS DISTINCT FROM OLD.levy_type OR NEW.version_no IS DISTINCT FROM OLD.version_no
                OR NEW.currency IS DISTINCT FROM OLD.currency OR NEW.ledger_amount IS DISTINCT FROM OLD.ledger_amount OR NEW.bil_amount IS DISTINCT FROM OLD.bil_amount
                OR NEW.adjustments_total IS DISTINCT FROM OLD.adjustments_total OR NEW.net_payable IS DISTINCT FROM OLD.net_payable
                OR NEW.entry_count IS DISTINCT FROM OLD.entry_count OR NEW.recon_explanation IS DISTINCT FROM OLD.recon_explanation
                OR NEW.content_hash IS DISTINCT FROM OLD.content_hash OR NEW.supersedes_return_id IS DISTINCT FROM OLD.supersedes_return_id) THEN
                RAISE EXCEPTION 'fin.levy_return: a return that is no longer DRAFT is frozen; amend it as a new version' USING ERRCODE = '55000';
            END IF;
            IF OLD.superseded_by_return_id IS NOT NULL AND NEW.superseded_by_return_id IS DISTINCT FROM OLD.superseded_by_return_id THEN
                RAISE EXCEPTION 'fin.levy_return: the superseding version is final' USING ERRCODE = '55000';
            END IF;
            RETURN NEW;
        END $$;

        CREATE TRIGGER trg_levy_return_freeze BEFORE UPDATE OR DELETE ON fin.levy_return FOR EACH ROW EXECUTE FUNCTION fin.levy_return_freeze();

        CREATE FUNCTION fin.levy_return_adjustment_guard() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE current_status text;
        BEGIN
            IF TG_OP <> 'INSERT' THEN
                RAISE EXCEPTION 'fin.levy_return_adjustment is insert-only' USING ERRCODE = '55000';
            END IF;
            SELECT status INTO current_status FROM fin.levy_return WHERE return_id = NEW.return_id;
            IF current_status IS NULL OR current_status <> 'DRAFT' THEN
                RAISE EXCEPTION 'fin.levy_return_adjustment: adjustments are added only while the return is DRAFT' USING ERRCODE = '55000';
            END IF;
            RETURN NEW;
        END $$;

        CREATE TRIGGER trg_levy_return_adjustment_guard BEFORE INSERT OR UPDATE OR DELETE ON fin.levy_return_adjustment FOR EACH ROW EXECUTE FUNCTION fin.levy_return_adjustment_guard();
        """;

    public const string DropTriggers =
        """
        DROP TRIGGER IF EXISTS trg_levy_return_adjustment_guard ON fin.levy_return_adjustment;
        DROP FUNCTION IF EXISTS fin.levy_return_adjustment_guard();
        DROP TRIGGER IF EXISTS trg_levy_return_freeze ON fin.levy_return;
        DROP FUNCTION IF EXISTS fin.levy_return_freeze();
        """;
}
