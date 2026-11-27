using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Reinsurance.Persistence;

/// <summary>The EF model of the premium accounting tables (BO-RI-10); all four are append-only (<see cref="AccountingDatabaseSql"/>).</summary>
internal static class AccountingModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DepositScheduleRow>(entity =>
        {
            entity.ToTable("deposit_schedule", table =>
            {
                table.HasCheckConstraint("ck_deposit_schedule_revision", "revision >= 1");
                table.HasCheckConstraint("ck_deposit_schedule_layer", "layer_no >= 1");
                table.HasCheckConstraint("ck_deposit_schedule_amounts", "minimum_premium >= 0 AND deposit_premium >= minimum_premium");
            });
            entity.HasKey(e => e.ScheduleId).HasName("pk_deposit_schedule");
            entity.Property(e => e.ScheduleId).HasColumnName("schedule_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.ContractId).HasColumnName("contract_id");
            entity.Property(e => e.LayerNo).HasColumnName("layer_no");
            entity.Property(e => e.ContractYear).HasColumnName("contract_year");
            entity.Property(e => e.Revision).HasColumnName("revision");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.MinimumPremium).HasColumnName("minimum_premium").HasColumnType("numeric(19,4)");
            entity.Property(e => e.DepositPremium).HasColumnName("deposit_premium").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Instalments).HasColumnName("instalments").HasColumnType("jsonb");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => new { e.ContractId, e.LayerNo, e.Revision }).IsUnique().HasDatabaseName("ux_deposit_schedule_revision");
            entity.HasOne<ContractRow>().WithMany().HasForeignKey(e => e.ContractId).HasConstraintName("fk_deposit_schedule_contract").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AdjustmentRow>(entity =>
        {
            entity.ToTable("adjustment", table =>
            {
                table.HasCheckConstraint("ck_adjustment_amounts", "subject_premium >= 0 AND calculated_premium >= 0 AND minimum_premium >= 0 AND deposit_premium >= 0");
            });
            entity.HasKey(e => e.AdjustmentId).HasName("pk_adjustment");
            entity.Property(e => e.AdjustmentId).HasColumnName("adjustment_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.ContractId).HasColumnName("contract_id");
            entity.Property(e => e.LayerNo).HasColumnName("layer_no");
            entity.Property(e => e.ContractYear).HasColumnName("contract_year");
            entity.Property(e => e.ScheduleId).HasColumnName("schedule_id");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.SubjectPremium).HasColumnName("subject_premium").HasColumnType("numeric(19,4)");
            entity.Property(e => e.RatePct).HasColumnName("rate_pct").HasColumnType("numeric(9,6)");
            entity.Property(e => e.CalculatedPremium).HasColumnName("calculated_premium").HasColumnType("numeric(19,4)");
            entity.Property(e => e.MinimumPremium).HasColumnName("minimum_premium").HasColumnType("numeric(19,4)");
            entity.Property(e => e.DepositPremium).HasColumnName("deposit_premium").HasColumnType("numeric(19,4)");
            entity.Property(e => e.AdjustmentAmount).HasColumnName("adjustment_amount").HasColumnType("numeric(19,4)");
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => new { e.ContractId, e.LayerNo, e.ContractYear }).HasDatabaseName("ix_adjustment_layer");
            entity.HasIndex(e => e.ScheduleId).HasDatabaseName("ix_adjustment_schedule");
            entity.HasOne<ContractRow>().WithMany().HasForeignKey(e => e.ContractId).HasConstraintName("fk_adjustment_contract").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DepositScheduleRow>().WithMany().HasForeignKey(e => e.ScheduleId).HasConstraintName("fk_adjustment_schedule").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CommissionRow>(entity =>
        {
            entity.ToTable("commission", table =>
            {
                table.HasCheckConstraint("ck_commission_amounts", "ceded_premium >= 0 AND incurred_loss >= 0 AND provisional_commission >= 0 AND earned_commission >= 0");
            });
            entity.HasKey(e => e.CommissionId).HasName("pk_commission");
            entity.Property(e => e.CommissionId).HasColumnName("commission_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.ContractId).HasColumnName("contract_id");
            entity.Property(e => e.ContractYear).HasColumnName("contract_year");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.CededPremium).HasColumnName("ceded_premium").HasColumnType("numeric(19,4)");
            entity.Property(e => e.IncurredLoss).HasColumnName("incurred_loss").HasColumnType("numeric(19,4)");
            entity.Property(e => e.LossRatioPct).HasColumnName("loss_ratio_pct").HasColumnType("numeric(12,6)");
            entity.Property(e => e.ProvisionalRatePct).HasColumnName("provisional_rate_pct").HasColumnType("numeric(9,6)");
            entity.Property(e => e.EarnedRatePct).HasColumnName("earned_rate_pct").HasColumnType("numeric(9,6)");
            entity.Property(e => e.ProvisionalCommission).HasColumnName("provisional_commission").HasColumnType("numeric(19,4)");
            entity.Property(e => e.EarnedCommission).HasColumnName("earned_commission").HasColumnType("numeric(19,4)");
            entity.Property(e => e.AdjustmentAmount).HasColumnName("adjustment_amount").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Scale).HasColumnName("scale").HasColumnType("jsonb");
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => new { e.ContractId, e.ContractYear, e.CreatedAt }).HasDatabaseName("ix_commission_contract");
            entity.HasOne<ContractRow>().WithMany().HasForeignKey(e => e.ContractId).HasConstraintName("fk_commission_contract").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AccountingDecisionRow>(entity =>
        {
            entity.ToTable("accounting_decision", table =>
            {
                table.HasCheckConstraint("ck_accounting_decision_kind", "kind IN ('ADJUSTMENT', 'COMMISSION')");
                table.HasCheckConstraint("ck_accounting_decision_decision", "decision IN ('APPROVED', 'REJECTED')");
            });
            entity.HasKey(e => new { e.Kind, e.SubjectId }).HasName("pk_accounting_decision");
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.SubjectId).HasColumnName("subject_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Decision).HasColumnName("decision");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            entity.Property(e => e.DecidedBy).HasColumnName("decided_by");
        });
    }
}

/// <summary>The SQL the accounting migration adds beside the EF model: the seal triggers (PITFALLS 8, 17). Nothing is updated or deleted.</summary>
internal static class AccountingDatabaseSql
{
    private static readonly string[] SealedTables = ["deposit_schedule", "adjustment", "commission", "accounting_decision"];

    /// <summary>Statements run by the migration's Up.</summary>
    public static string Up { get; } = string.Concat(SealedTables.Select(t =>
        $"CREATE TRIGGER tr_{t}_seal BEFORE UPDATE OR DELETE ON ri.{t} FOR EACH ROW EXECUTE FUNCTION ri.content_append_only();\n" +
        $"CREATE TRIGGER tr_{t}_no_truncate BEFORE TRUNCATE ON ri.{t} FOR EACH STATEMENT EXECUTE FUNCTION ri.content_append_only();\n"));

    /// <summary>Statements run by the migration's Down.</summary>
    public static string Down { get; } = string.Concat(SealedTables.Select(t =>
        $"DROP TRIGGER IF EXISTS tr_{t}_seal ON ri.{t};\nDROP TRIGGER IF EXISTS tr_{t}_no_truncate ON ri.{t};\n"));
}
