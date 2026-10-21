using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Time;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreIns.Modules.Reinsurance.Cession;

/// <summary>
/// <c>ri.Tracker.openingAggregate</c>: records the opening aggregate of a treaty year once, idempotent on (treaty, year), for
/// MIG-CLM-RI-IMPORT. A repeat returns the stored row and changes nothing (first value wins); a repeat with a different amount
/// is a data conflict that the caller reports by comparing the stored amount (ReinsuranceMigrationImporter: MIG-ERR-CONFLICT).
/// </summary>
internal sealed class TrackerService(ReinsuranceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
{
    public async Task<TrackerRow> OpeningAggregateAsync(string treatyId, int contractYear, decimal amount, Guid? batchId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(treatyId);
        if (amount < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "An opening aggregate cannot be negative.");
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", [new NpgsqlParameter("k", $"ri:tracker:{legalEntity:D}:{treatyId}:{contractYear}")], cancellationToken).ConfigureAwait(false);
        var existing = await db.Trackers.SingleOrDefaultAsync(t => t.LegalEntityId == legalEntity && t.TreatyId == treatyId && t.ContractYear == contractYear, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var row = new TrackerRow
        {
            TrackerId = Guid.CreateVersion7(), LegalEntityId = legalEntity, TreatyId = treatyId, ContractYear = contractYear, OpeningAggregate = amount, RecordedAt = clock.Now, BatchId = batchId,
        };
        db.Trackers.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }
}
