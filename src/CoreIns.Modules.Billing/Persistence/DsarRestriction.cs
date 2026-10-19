using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Persistence;

/// <summary><c>bil.dsar_restriction</c>: one row per (subject, record class) restricted for a data-subject request (D-SL6-07). Honoured by reads feeding marketing and notification.</summary>
internal sealed class DsarRestrictionRow
{
    public Guid RestrictionId { get; set; }

    public PartyId SubjectPartyId { get; set; }

    public string RecordClass { get; set; } = string.Empty;

    public string LegalBasis { get; set; } = string.Empty;

    /// <summary>ISO date or UNSET_F404.</summary>
    public string RetentionEnd { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public int RecordCount { get; set; }

    public string RestrictedBy { get; set; } = string.Empty;

    public Instant RestrictedAt { get; set; }
}

internal static class DsarRestrictionModel
{
    public static void Configure(ModelBuilder model) =>
        model.Entity<DsarRestrictionRow>(e =>
        {
            e.ToTable("dsar_restriction");
            e.HasKey(r => r.RestrictionId).HasName("pk_dsar_restriction");
            e.Property(r => r.RestrictionId).HasColumnName("restriction_id");
            e.Property(r => r.SubjectPartyId).HasColumnName("subject_party_id");
            e.Property(r => r.RecordClass).HasColumnName("record_class");
            e.Property(r => r.LegalBasis).HasColumnName("legal_basis");
            e.Property(r => r.RetentionEnd).HasColumnName("retention_end");
            e.Property(r => r.Reason).HasColumnName("reason");
            e.Property(r => r.RecordCount).HasColumnName("record_count");
            e.Property(r => r.RestrictedBy).HasColumnName("restricted_by");
            e.Property(r => r.RestrictedAt).HasColumnName("restricted_at").HasColumnType("timestamptz");
            e.HasIndex(r => new { r.SubjectPartyId, r.RecordClass }).IsUnique().HasDatabaseName("ux_dsar_restriction_subject_class");
        });
}
