using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Platform.Context;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.MigrationImport;

/// <summary>BIL figures for the migration reconciliation (BO-MIG-10): opening receivable count and outstanding per currency. Read-only.</summary>
internal sealed class BillingMigrationReconciliationReader(BillingDbContext db, RequestContext context, ILegalEntityDirectory legalEntities) : IMigrationReconciliationReader
{
    public string ModuleName => "BIL";

    public async Task<IReadOnlyList<ReconciliationFigure>> ReadAsync(Guid? batchId, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var rows = await db.MigrationReceivables.AsNoTracking()
            .Where(r => r.LegalEntityId == legalEntity && (batchId == null || r.BatchId == batchId))
            .GroupBy(r => r.Currency)
            .Select(g => new { Currency = g.Key, Count = g.LongCount(), Amount = g.Sum(r => r.OutstandingAmount) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new ReconciliationFigure(BillingImportTypes.Receivable, string.Empty, r.Currency, r.Count, r.Amount)).ToList();
    }
}
