using CoreIns.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.Persistence;

/// <summary><c>fin.recon_source_total</c>: the daily control total a source module pushed (fin.Reconciliation.exchange); a later push of the same source, date and currency replaces it.</summary>
internal sealed class ReconSourceTotalRow
{
    public Guid LegalEntityId { get; set; }

    public string Source { get; set; } = string.Empty;

    public BusinessDate AsOf { get; set; }

    public string PeriodCode { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public int? ItemCount { get; set; }

    public Instant ReceivedAt { get; set; }

    public string ReceivedBy { get; set; } = string.Empty;
}

/// <summary><c>fin.recon_run</c>: one run of a reconciliation definition for a period (BO-FIN-08). Insert-only; a rerun adds a run with the next number.</summary>
internal sealed class ReconRunRow
{
    public Guid RunId { get; set; }

    public Guid LegalEntityId { get; set; }

    public string DefinitionCode { get; set; } = string.Empty;

    public string PeriodCode { get; set; } = string.Empty;

    public int RunNo { get; set; }

    public string State { get; set; } = string.Empty;

    public string RanBy { get; set; } = string.Empty;

    /// <summary>Open breaks right after the run (later matches and acceptances do not change it).</summary>
    public int OpenBreaks { get; set; }

    public Instant RanAt { get; set; }

    /// <summary>JSON list of the per-currency source and ledger totals of the run.</summary>
    public string Totals { get; set; } = "[]";
}

/// <summary><c>fin.recon_break</c>: a difference found by a run. Figures are the latest found; only the state and the decision fields change after insert.</summary>
internal sealed class ReconBreakRow
{
    public Guid BreakId { get; set; }

    public Guid LegalEntityId { get; set; }

    public string DefinitionCode { get; set; } = string.Empty;

    public string PeriodCode { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public decimal SourceAmount { get; set; }

    public decimal LedgerAmount { get; set; }

    public int? SourceCount { get; set; }

    public int? LedgerCount { get; set; }

    public Guid RunId { get; set; }

    public Guid LastRunId { get; set; }

    /// <summary>Who ran the latest run that found the break; the person who accepts it must be another one.</summary>
    public string LastRunBy { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string? DecidedBy { get; set; }

    public Instant? DecidedAt { get; set; }

    public string? Reason { get; set; }

    /// <summary>MATCHED (by hand), CLEARED (a later run no longer found it) or SUPERSEDED (its figures changed); null while OPEN or ACCEPTED.</summary>
    public string? Resolution { get; set; }
}

internal static class ReconModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<ReconSourceTotalRow>(e =>
        {
            e.ToTable("recon_source_total", t =>
            {
                t.HasCheckConstraint("ck_recon_source_total_currency", "currency ~ '^[A-Z]{3}$'");
                t.HasCheckConstraint("ck_recon_source_total_count", "item_count IS NULL OR item_count >= 0");
            });
            e.HasKey(r => new { r.LegalEntityId, r.Source, r.AsOf, r.Currency }).HasName("pk_recon_source_total");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.Source).HasColumnName("source");
            e.Property(r => r.AsOf).HasColumnName("as_of");
            e.Property(r => r.PeriodCode).HasColumnName("period_code");
            e.Property(r => r.Currency).HasColumnName("currency").HasColumnType("char(3)");
            e.Property(r => r.Amount).HasColumnName("amount").HasColumnType("numeric(19,2)");
            e.Property(r => r.ItemCount).HasColumnName("item_count");
            e.Property(r => r.ReceivedAt).HasColumnName("received_at").HasColumnType("timestamptz");
            e.Property(r => r.ReceivedBy).HasColumnName("received_by");
            e.HasIndex(r => new { r.LegalEntityId, r.Source, r.PeriodCode }).HasDatabaseName("ix_recon_source_total_period");
        });

        model.Entity<ReconRunRow>(e =>
        {
            e.ToTable("recon_run", t =>
            {
                t.HasCheckConstraint("ck_recon_run_state", "state IN ('CLEAN','BREAKS')");
                t.HasCheckConstraint("ck_recon_run_period", "period_code ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'");
            });
            e.HasKey(r => r.RunId).HasName("pk_recon_run");
            e.Property(r => r.RunId).HasColumnName("run_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.DefinitionCode).HasColumnName("definition_code");
            e.Property(r => r.PeriodCode).HasColumnName("period_code");
            e.Property(r => r.RunNo).HasColumnName("run_no");
            e.Property(r => r.State).HasColumnName("state");
            e.Property(r => r.RanBy).HasColumnName("ran_by");
            e.Property(r => r.OpenBreaks).HasColumnName("open_breaks");
            e.Property(r => r.RanAt).HasColumnName("ran_at").HasColumnType("timestamptz");
            e.Property(r => r.Totals).HasColumnName("totals");
            e.HasIndex(r => new { r.LegalEntityId, r.DefinitionCode, r.PeriodCode, r.RunNo }).IsUnique().HasDatabaseName("ux_recon_run_number");
        });

        model.Entity<ReconBreakRow>(e =>
        {
            e.ToTable("recon_break", t =>
            {
                t.HasCheckConstraint("ck_recon_break_state", "state IN ('OPEN','ACCEPTED','RESOLVED')");
                t.HasCheckConstraint("ck_recon_break_kind", "kind IN ('AMOUNT','COUNT','MISSING_SOURCE','MISSING_LEDGER')");
            });
            e.HasKey(r => r.BreakId).HasName("pk_recon_break");
            e.Property(r => r.BreakId).HasColumnName("break_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.DefinitionCode).HasColumnName("definition_code");
            e.Property(r => r.PeriodCode).HasColumnName("period_code");
            e.Property(r => r.Currency).HasColumnName("currency").HasColumnType("char(3)");
            e.Property(r => r.Kind).HasColumnName("kind");
            e.Property(r => r.State).HasColumnName("state");
            e.Property(r => r.SourceAmount).HasColumnName("source_amount").HasColumnType("numeric(19,2)");
            e.Property(r => r.LedgerAmount).HasColumnName("ledger_amount").HasColumnType("numeric(19,2)");
            e.Property(r => r.SourceCount).HasColumnName("source_count");
            e.Property(r => r.LedgerCount).HasColumnName("ledger_count");
            e.Property(r => r.RunId).HasColumnName("run_id");
            e.Property(r => r.LastRunId).HasColumnName("last_run_id");
            e.Property(r => r.LastRunBy).HasColumnName("last_run_by");
            e.Property(r => r.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(r => r.DecidedBy).HasColumnName("decided_by");
            e.Property(r => r.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            e.Property(r => r.Reason).HasColumnName("reason");
            e.Property(r => r.Resolution).HasColumnName("resolution");
            e.HasIndex(r => new { r.LegalEntityId, r.PeriodCode, r.State }).HasDatabaseName("ix_recon_break_period");
            e.HasIndex(r => new { r.LegalEntityId, r.DefinitionCode, r.PeriodCode, r.Currency, r.Kind }).IsUnique().HasFilter("state IN ('OPEN','ACCEPTED')").HasDatabaseName("ux_recon_break_active");
            e.HasOne<ReconRunRow>().WithMany().HasForeignKey(r => r.RunId).HasConstraintName("fk_recon_break_run").OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ReconRunRow>().WithMany().HasForeignKey(r => r.LastRunId).HasConstraintName("fk_recon_break_last_run").OnDelete(DeleteBehavior.Restrict);
        });
    }
}
