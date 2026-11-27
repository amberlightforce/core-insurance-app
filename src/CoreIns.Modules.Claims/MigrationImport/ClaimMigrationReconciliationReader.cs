using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Platform.Context;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.MigrationImport;

/// <summary>CLM figures for the migration reconciliation (BO-MIG-10): imported claims and their opening reserve per currency. Read-only.</summary>
// TODO(hardening): compare the live open reserve from the claim ledger once post-migration movements are expected; this is the opening position.
internal sealed class ClaimMigrationReconciliationReader(ClaimsDbContext db, ClaimProtection protection, RequestContext context) : IMigrationReconciliationReader
{
    public string ModuleName => "CLM";

    public async Task<IReadOnlyList<ReconciliationFigure>> ReadAsync(Guid? batchId, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var rows = await db.MigrationImports.AsNoTracking()
            .Where(m => m.LegalEntityId == legalEntity && (batchId == null || m.BatchId == batchId))
            .GroupBy(m => m.Currency)
            .Select(g => new { Currency = g.Key, Count = g.LongCount(), Amount = g.Sum(m => m.OpeningReserve) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new ReconciliationFigure(ClaimMigrationImporter.ObjectType, string.Empty, r.Currency, r.Count, r.Amount)).ToList();
    }
}
