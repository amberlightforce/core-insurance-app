using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Dsar;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Platform.Persistence;

/// <summary>
/// A DSAR restriction in a module's own schema: one row per (subject party, record class) (SL6-DSAR-PART-C, D-SL6-07).
/// Reads that feed marketing or notification must call <see cref="DsarRestrictions.IsRestrictedAsync"/>.
/// </summary>
public sealed class DsarRestrictionRow
{
    /// <summary>Row id.</summary>
    public Guid RestrictionId { get; set; }

    /// <summary>The data subject.</summary>
    public Guid PartyId { get; set; }

    /// <summary>The restricted record class.</summary>
    public string RecordClass { get; set; } = string.Empty;

    /// <summary>Why the data is restricted.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>The legal basis for keeping the data.</summary>
    public string LegalBasis { get; set; } = string.Empty;

    /// <summary>Retention end as text; <see cref="DsarRetention.UnsetF404"/> until F-404 is answered.</summary>
    public string RetentionEnd { get; set; } = DsarRetention.UnsetF404;

    /// <summary>When the restriction was recorded.</summary>
    public Instant RestrictedAt { get; set; }
}

/// <summary>Mapping and helpers for <see cref="DsarRestrictionRow"/>, shared by the module DbContexts.</summary>
public static class DsarRestrictions
{
    /// <summary>Maps <see cref="DsarRestrictionRow"/> to <c>dsar_restriction</c> in the context's default schema.</summary>
    public static void MapDsarRestrictions(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<DsarRestrictionRow>(entity =>
        {
            entity.ToTable("dsar_restriction");
            entity.HasKey(e => e.RestrictionId).HasName("pk_dsar_restriction");
            entity.Property(e => e.RestrictionId).HasColumnName("restriction_id");
            entity.Property(e => e.PartyId).HasColumnName("party_id");
            entity.Property(e => e.RecordClass).HasColumnName("record_class");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.LegalBasis).HasColumnName("legal_basis");
            entity.Property(e => e.RetentionEnd).HasColumnName("retention_end");
            entity.Property(e => e.RestrictedAt).HasColumnName("restricted_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.PartyId, e.RecordClass }).IsUnique().HasDatabaseName("ux_dsar_restriction_subject_class");
        });
    }

    /// <summary>True when the party's record class is restricted in this module (for marketing/notification reads).</summary>
    public static Task<bool> IsRestrictedAsync(DbContext db, PartyId party, string recordClass, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var id = party.Value;
        return db.Set<DsarRestrictionRow>().AsNoTracking().AnyAsync(r => r.PartyId == id && r.RecordClass == recordClass, ct);
    }

    /// <summary>
    /// Records the restriction of a record class and returns its outcome: NOT_HELD when <paramref name="count"/> is zero,
    /// otherwise RESTRICTED (idempotent: an existing row is kept).
    /// </summary>
    public static async Task<DsarRecordOutcome> RestrictAsync(
        DbContext db, IClock clock, PartyId party, string recordClass, int count, string reason, string legalBasis, string? retentionEnd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (count == 0)
        {
            return DsarRecordOutcome.NotHeld(recordClass);
        }

        var id = party.Value;
        var set = db.Set<DsarRestrictionRow>();
        if (!await set.AnyAsync(r => r.PartyId == id && r.RecordClass == recordClass, ct).ConfigureAwait(false))
        {
            set.Add(new DsarRestrictionRow
            {
                RestrictionId = Guid.CreateVersion7(),
                PartyId = id,
                RecordClass = recordClass,
                Reason = reason,
                LegalBasis = legalBasis,
                RetentionEnd = retentionEnd ?? DsarRetention.UnsetF404,
                RestrictedAt = clock.Now,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return DsarRecordOutcome.Restricted(recordClass, legalBasis, count, retentionEnd);
    }
}
