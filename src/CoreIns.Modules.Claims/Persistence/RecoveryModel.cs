using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoreIns.Modules.Claims.Persistence;

/// <summary>Shared slice-4 schema. Later recovery and clearing packages add commands, not migrations.</summary>
internal static class RecoveryModel
{
    private const string MoneyType = "numeric(19,4)";

    public static void Map(ModelBuilder model)
    {
        model.Entity<RecoveryRow>(e =>
        {
            MapRow(e, "recovery");
            e.HasKey(x => x.RecoveryId).HasName("pk_recovery");
            e.ToTable("recovery", t =>
            {
                t.HasCheckConstraint("ck_recovery_type", "type IN ('SUBROGATION','SALVAGE','FRIENDLY_SETTLEMENT')");
                t.HasCheckConstraint("ck_recovery_status", "status IN ('OPEN','DEMANDED','AGREED','DISPUTED','IN_ARBITRATION','IN_LITIGATION','CLOSED','WRITTEN_OFF')");
                t.HasCheckConstraint("ck_recovery_amount", "expected_amount >= 0 AND (salvage_estimate IS NULL OR salvage_estimate >= 0) AND (sale_price IS NULL OR sale_price >= 0)");
                t.HasCheckConstraint("ck_recovery_allocation", "allocation_rule IN ('PRO_RATA_PAID','SPECIFIED')");
                t.HasCheckConstraint("ck_recovery_currency", "currency = 'EUR'");
            });
            e.Property(x => x.Milestones).HasColumnType("jsonb");
            e.HasOne<ClaimRow>().WithMany().HasForeignKey(x => x.ClaimId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_recovery_claim");
            e.HasOne<ExposureRow>().WithMany().HasForeignKey(x => x.ExposureId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_recovery_exposure");
            e.HasIndex(x => x.ClaimId).HasDatabaseName("ix_recovery_claim");
            e.HasIndex(x => x.ReceivableId).IsUnique().HasFilter("receivable_id IS NOT NULL").HasDatabaseName("ux_recovery_receivable");
        });
        model.Entity<FsCaseRow>(e =>
        {
            MapRow(e, "fs_case");
            e.HasKey(x => x.FsCaseId).HasName("pk_fs_case");
            e.ToTable("fs_case", t =>
            {
                t.HasCheckConstraint("ck_fs_case_role", "role IN ('OWN_INSURER','AT_FAULT_INSURER')");
                t.HasCheckConstraint("ck_fs_case_status", "status IN ('ELIGIBILITY_PENDING','ELIGIBLE','NOT_ELIGIBLE','SUBMITTED','ACCEPTED','DISPUTED','REJECTED','SETTLED','RECONCILED')");
                t.HasCheckConstraint("ck_fs_case_currency", "currency = 'EUR'");
                t.HasCheckConstraint("ck_fs_case_value", "clearing_value IS NULL OR clearing_value >= 0");
                t.HasCheckConstraint("ck_fs_case_rule", "eligibility_result IS NULL OR (eligibility_rule_id IS NOT NULL AND eligibility_rule_version IS NOT NULL AND legal_status IS NOT NULL)");
            });
            e.Property(x => x.EligibilityResult).HasColumnType("jsonb");
            e.HasIndex(x => new { x.ClaimId, x.Role }).IsUnique().HasFilter("status NOT IN ('NOT_ELIGIBLE','REJECTED','RECONCILED')").HasDatabaseName("ux_fs_case_open_claim_role");
            e.HasIndex(x => new { x.LegalEntityId, x.NotificationReference }).IsUnique().HasFilter("notification_reference IS NOT NULL").HasDatabaseName("ux_fs_case_notification");
            e.HasOne<ClaimRow>().WithMany().HasForeignKey(x => x.ClaimId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_fs_case_claim");
            e.HasOne<ExposureRow>().WithMany().HasForeignKey(x => x.ExposureId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_fs_case_exposure");
            e.HasOne<RecoveryRow>().WithMany().HasForeignKey(x => x.RecoveryId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_fs_case_recovery");
        });
        model.Entity<FsStatementRow>(e =>
        {
            MapRow(e, "fs_statement");
            e.HasKey(x => x.FsStatementId).HasName("pk_fs_statement");
            e.ToTable("fs_statement", t =>
            {
                t.HasCheckConstraint("ck_fs_statement_status", "status IN ('PENDING','REQUESTED','SETTLED','REJECTED')");
                t.HasCheckConstraint("ck_fs_statement_direction", "direction IN ('PAYABLE','RECEIVABLE')");
                t.HasCheckConstraint("ck_fs_statement_currency", "currency = 'EUR'");
                t.HasCheckConstraint("ck_fs_statement_net", "net_amount >= 0");
                t.HasCheckConstraint("ck_fs_statement_period", "period ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'");
            });
            e.HasIndex(x => new { x.LegalEntityId, x.Period, x.CounterpartyInsurerPartyId }).IsUnique().HasDatabaseName("ux_fs_statement_period_counterparty");
        });
        model.Entity<FsStatementLineRow>(e =>
        {
            MapRow(e, "fs_statement_line");
            e.HasKey(x => x.FsStatementLineId).HasName("pk_fs_statement_line");
            e.ToTable("fs_statement_line", t =>
            {
                t.HasCheckConstraint("ck_fs_statement_line_direction", "direction IN ('PAYABLE','RECEIVABLE')");
                t.HasCheckConstraint("ck_fs_statement_line_amount", "amount > 0 AND currency = 'EUR'");
                t.HasCheckConstraint("ck_fs_statement_line_match", "match_status IN ('UNMATCHED','MATCHED','EXCEPTION','SETTLED')");
            });
            e.HasIndex(x => new { x.FsStatementId, x.ExternalReference }).IsUnique().HasDatabaseName("ux_fs_statement_line_reference");
            e.HasOne<FsStatementRow>().WithMany().HasForeignKey(x => x.FsStatementId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_fs_statement_line_statement");
            e.HasOne<FsCaseRow>().WithMany().HasForeignKey(x => x.FsCaseId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_fs_statement_line_case");
            e.HasOne<ClaimRow>().WithMany().HasForeignKey(x => x.ClaimId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_fs_statement_line_claim");
            e.HasOne<RecoveryRow>().WithMany().HasForeignKey(x => x.RecoveryId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_fs_statement_line_recovery");
            e.HasOne<ClaimPaymentRow>().WithMany().HasForeignKey(x => x.ClaimPaymentId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_fs_statement_line_payment");
        });
    }

    private static void MapRow<T>(EntityTypeBuilder<T> entity, string table) where T : ClaimsRow
    {
        entity.ToTable(table, t => ClaimsDbContext.CommonChecks(t, table));
        ClaimsDbContext.MapCommon(entity);
        foreach (var property in entity.Metadata.GetProperties())
        {
            property.SetColumnName(Snake(property.Name));
            if (property.ClrType == typeof(decimal) || property.ClrType == typeof(decimal?))
            {
                property.SetColumnType(MoneyType);
            }
        }
    }

    private static string Snake(string name) => string.Concat(name.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
