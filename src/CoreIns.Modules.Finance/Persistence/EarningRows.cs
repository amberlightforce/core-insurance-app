using CoreIns.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.Persistence;

/// <summary><c>fin.earning_run</c>: one execution of the earning engine for an accounting period (BO-FIN-06a). Append-only.</summary>
internal sealed class EarningRunRow
{
    public Guid RunId { get; set; }

    public Guid LegalEntityId { get; set; }

    public string PeriodCode { get; set; } = string.Empty;

    public BusinessDate AsOf { get; set; }

    public string State { get; set; } = string.Empty;

    public Instant StartedAt { get; set; }

    public Instant FinishedAt { get; set; }

    public string RunBy { get; set; } = string.Empty;

    public int PoliciesProcessed { get; set; }

    public int TermsSkipped { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal EarnedToDate { get; set; }

    public decimal PostedDelta { get; set; }

    public Guid[] JournalIds { get; set; } = [];
}

/// <summary><c>fin.earned_premium</c>: what one run earned of one policy term in one book; the sum of a term's deltas is its earned premium to date.</summary>
internal sealed class EarnedPremiumRow
{
    public Guid RunId { get; set; }

    public Guid LegalEntityId { get; set; }

    public Guid PolicyTermId { get; set; }

    public string Book { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    public Guid PolicyId { get; set; }

    public string PolicyNumber { get; set; } = string.Empty;

    public BusinessDate TermStart { get; set; }

    public BusinessDate TermEnd { get; set; }

    public decimal WrittenGross { get; set; }

    public decimal WrittenNet { get; set; }

    public int DaysEarned { get; set; }

    public int TermDays { get; set; }

    public decimal EarnedToDate { get; set; }

    public decimal Delta { get; set; }
}

internal static class EarningModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<EarningRunRow>(e =>
        {
            e.ToTable("earning_run", t =>
            {
                t.HasCheckConstraint("ck_earning_run_state", "state IN ('DONE')");
                t.HasCheckConstraint("ck_earning_run_period", "period_code ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'");
            });
            e.HasKey(r => r.RunId).HasName("pk_earning_run");
            e.Property(r => r.RunId).HasColumnName("run_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.PeriodCode).HasColumnName("period_code");
            e.Property(r => r.AsOf).HasColumnName("as_of");
            e.Property(r => r.State).HasColumnName("state");
            e.Property(r => r.StartedAt).HasColumnName("started_at").HasColumnType("timestamptz");
            e.Property(r => r.FinishedAt).HasColumnName("finished_at").HasColumnType("timestamptz");
            e.Property(r => r.RunBy).HasColumnName("run_by");
            e.Property(r => r.PoliciesProcessed).HasColumnName("policies_processed");
            e.Property(r => r.TermsSkipped).HasColumnName("terms_skipped");
            e.Property(r => r.Currency).HasColumnName("currency").HasColumnType("char(3)");
            e.Property(r => r.EarnedToDate).HasColumnName("earned_to_date").HasColumnType("numeric(19,4)");
            e.Property(r => r.PostedDelta).HasColumnName("posted_delta").HasColumnType("numeric(19,4)");
            e.Property(r => r.JournalIds).HasColumnName("journal_ids");
            e.HasIndex(r => new { r.LegalEntityId, r.PeriodCode, r.AsOf }).HasDatabaseName("ix_earning_run_period");
        });

        model.Entity<EarnedPremiumRow>(e =>
        {
            e.ToTable("earned_premium", t =>
            {
                t.HasCheckConstraint("ck_earned_premium_days", "term_days > 0 AND days_earned >= 0 AND days_earned <= term_days");
                t.HasCheckConstraint("ck_earned_premium_currency", "currency ~ '^[A-Z]{3}$'");
            });
            e.HasKey(r => new { r.RunId, r.PolicyTermId, r.Book, r.Currency }).HasName("pk_earned_premium");
            e.Property(r => r.RunId).HasColumnName("run_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.PolicyTermId).HasColumnName("policy_term_id");
            e.Property(r => r.Book).HasColumnName("book");
            e.Property(r => r.Currency).HasColumnName("currency").HasColumnType("char(3)");
            e.Property(r => r.PolicyId).HasColumnName("policy_id");
            e.Property(r => r.PolicyNumber).HasColumnName("policy_number");
            e.Property(r => r.TermStart).HasColumnName("term_start");
            e.Property(r => r.TermEnd).HasColumnName("term_end");
            e.Property(r => r.WrittenGross).HasColumnName("written_gross").HasColumnType("numeric(19,4)");
            e.Property(r => r.WrittenNet).HasColumnName("written_net").HasColumnType("numeric(19,4)");
            e.Property(r => r.DaysEarned).HasColumnName("days_earned");
            e.Property(r => r.TermDays).HasColumnName("term_days");
            e.Property(r => r.EarnedToDate).HasColumnName("earned_to_date").HasColumnType("numeric(19,4)");
            e.Property(r => r.Delta).HasColumnName("delta").HasColumnType("numeric(19,4)");
            e.HasIndex(r => new { r.LegalEntityId, r.PolicyTermId, r.Book }).HasDatabaseName("ix_earned_premium_term");
            e.HasOne<EarningRunRow>().WithMany().HasForeignKey(r => r.RunId).HasConstraintName("fk_earned_premium_run").OnDelete(DeleteBehavior.Restrict);
        });
    }
}
