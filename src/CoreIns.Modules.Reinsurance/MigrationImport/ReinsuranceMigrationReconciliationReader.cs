using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Platform.Context;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Reinsurance.MigrationImport;

/// <summary>RI figures for the migration reconciliation (BO-MIG-10): migrated treaty-year trackers and their opening aggregate. Read-only.</summary>
// TODO(hardening): trackers carry no currency yet; the aggregate is reported in EUR (the only functional currency imported).
internal sealed class ReinsuranceMigrationReconciliationReader(ReinsuranceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities) : IMigrationReconciliationReader
{
    public string ModuleName => "RI";

    public async Task<IReadOnlyList<ReconciliationFigure>> ReadAsync(Guid? batchId, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
        var query = db.Trackers.AsNoTracking().Where(t => t.LegalEntityId == legalEntity && t.BatchId != null && (batchId == null || t.BatchId == batchId));
        var count = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        if (count == 0)
        {
            return [];
        }

        var amount = await query.SumAsync(t => t.OpeningAggregate, cancellationToken).ConfigureAwait(false);
        return [new ReconciliationFigure(ReinsuranceMigrationImporter.ObjectType, string.Empty, "EUR", count, amount)];
    }
}
