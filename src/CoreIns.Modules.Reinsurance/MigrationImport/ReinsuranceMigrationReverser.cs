using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Reinsurance.MigrationImport;

/// <summary>
/// <c>IMigrationReverser</c> for module RI (D-SL6-05). A treaty-year tracker opened by the batch is removed again. Nothing reads or
/// consumes the opening aggregate yet, so there is no post-migration activity that could block the reversal; when a consumer is added
/// it must be checked in <c>ri.reverse_migrated_tracker</c> (which repeats the batch check under the row lock). The tracker table is
/// append-only: the delete runs in that SECURITY DEFINER function, the only caller the seal trigger lets through.
/// </summary>
internal sealed class ReinsuranceMigrationReverser(ReinsuranceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities) : IMigrationReverser
{
    public string ModuleName => "RI";

    public async Task<IReadOnlyList<ReversalOutcome>> ReverseAsync(Guid batchId, bool dryRun, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
        var trackers = await db.Trackers.AsNoTracking().Where(t => t.BatchId == batchId && t.LegalEntityId == legalEntity)
            .OrderBy(t => t.RecordedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var outcomes = new List<ReversalOutcome>(trackers.Count);
        foreach (var tracker in trackers)
        {
            if (dryRun)
            {
                outcomes.Add(new ReversalOutcome(ReinsuranceMigrationImporter.ObjectType, tracker.TrackerId, true));
                continue;
            }

            var removed = (await db.Database.SqlQuery<bool>($"SELECT ri.reverse_migrated_tracker({batchId}, {tracker.TrackerId}) AS \"Value\"")
                .ToListAsync(cancellationToken).ConfigureAwait(false))[0];
            outcomes.Add(removed
                ? new ReversalOutcome(ReinsuranceMigrationImporter.ObjectType, tracker.TrackerId, true)
                : new ReversalOutcome(ReinsuranceMigrationImporter.ObjectType, tracker.TrackerId, false, MigrationReversalCodes.Blocked, "The tracker is no longer reversible."));
        }

        return outcomes;
    }
}
