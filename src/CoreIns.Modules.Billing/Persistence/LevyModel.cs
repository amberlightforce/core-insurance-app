using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Persistence;

/// <summary>
/// The payable of one levy authority and period (BO-BIL-14): created from the levy lines of the sub-ledger when its payment is requested,
/// never from free text. One per (legal entity, levy authority, period); the payment is the <c>BIL_LEVY</c> disbursement it points to.
/// </summary>
internal sealed class LevyPayableRow
{
    public Guid LevyPayableId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    /// <summary>Levy authority code = levy type = the charge type of the levy lines.</summary>
    public string LevyAuthority { get; set; } = string.Empty;

    /// <summary>Accounting month, yyyy-MM.</summary>
    public string Period { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    /// <summary>Net levy accrued in the period when the payable was created.</summary>
    public decimal CollectedAmount { get; set; }

    /// <summary>The amount requested for payment (the collected amount at creation).</summary>
    public decimal PayableAmount { get; set; }

    /// <summary>Settled, or the weakest legal status behind the lines (PendingOpinion, Unverified).</summary>
    public string LegalStatus { get; set; } = string.Empty;

    public bool Provisional { get; set; }

    public PartyId PayeePartyId { get; set; }

    public Guid PayeeAccountId { get; set; }

    public DisbursementId DisbursementId { get; set; }

    public string SourceId { get; set; } = string.Empty;

    public Instant RequestedAt { get; set; }

    public string RequestedBy { get; set; } = string.Empty;

    public int RecordVersion { get; set; }
}

internal static class LevyModel
{
    public static void Configure(ModelBuilder model) =>
        model.Entity<LevyPayableRow>(e =>
        {
            e.ToTable("levy_payable", t =>
            {
                t.HasCheckConstraint("ck_levy_payable_period", "period ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'");
                t.HasCheckConstraint("ck_levy_payable_amount", "payable_amount > 0 AND collected_amount >= payable_amount");
                t.HasCheckConstraint("ck_levy_payable_currency", "currency ~ '^[A-Z]{3}$'");
                t.HasCheckConstraint("ck_levy_payable_record_version", "record_version >= 1");
            });
            e.HasKey(r => r.LevyPayableId).HasName("pk_levy_payable");
            foreach (var p in e.Metadata.GetProperties())
            {
                p.SetColumnName(System.Text.RegularExpressions.Regex.Replace(p.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
            }

            e.Property(r => r.Period).HasColumnType("char(7)");
            e.Property(r => r.Currency).HasColumnType("char(3)");
            e.Property(r => r.CollectedAmount).HasColumnType("numeric(19,4)");
            e.Property(r => r.PayableAmount).HasColumnType("numeric(19,4)");
            e.Property(r => r.RequestedAt).HasColumnType("timestamptz");
            e.Property(r => r.RecordVersion).IsConcurrencyToken();
            e.HasIndex(r => new { r.LegalEntityId, r.LevyAuthority, r.Period }).IsUnique().HasDatabaseName("ux_levy_payable_authority_period");
            e.HasIndex(r => r.DisbursementId).HasDatabaseName("ix_levy_payable_disbursement");
            e.HasOne<DisbursementRow>().WithMany().HasForeignKey(r => r.DisbursementId).HasConstraintName("fk_levy_payable_disbursement").OnDelete(DeleteBehavior.Restrict);
        });
}
