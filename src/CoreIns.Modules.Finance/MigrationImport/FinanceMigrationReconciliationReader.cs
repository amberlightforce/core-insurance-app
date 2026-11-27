using CoreIns.Modules.Finance.Persistence;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Platform.Context;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.MigrationImport;

/// <summary>
/// FIN figures for the migration reconciliation (BO-MIG-10), read-only: per currency the count of opening-balance journals and the sum of their debits
/// (key empty), and per ledger account the net debit (key = account code) so a sub-ledger can be checked against its control account.
/// Journals already reversed by a reversal journal are not counted.
/// </summary>
internal sealed class FinanceMigrationReconciliationReader(FinanceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities) : IMigrationReconciliationReader
{
    public string ModuleName => "FIN";

    public async Task<IReadOnlyList<ReconciliationFigure>> ReadAsync(Guid? batchId, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
        var journals = db.MigrationImports.AsNoTracking()
            .Where(m => m.LegalEntityId == legalEntity && (batchId == null || m.BatchId == batchId)
                && !db.Journals.Any(j => j.ReversesJournalId == m.JournalId))
            .Select(m => m.JournalId);
        var lines = await db.JournalLines.AsNoTracking().Where(l => journals.Contains(l.JournalId))
            .Select(l => new { l.JournalId, l.AccountCode, l.Currency, l.Side, l.Amount }).ToListAsync(cancellationToken).ConfigureAwait(false);

        var figures = new List<ReconciliationFigure>();
        foreach (var currency in lines.GroupBy(l => l.Currency))
        {
            var debit = currency.Where(l => l.Side == Sides.Debit).Sum(l => l.Amount);
            figures.Add(new ReconciliationFigure(FinanceImportTypes.OpeningBalance, string.Empty, currency.Key, currency.Select(l => l.JournalId).Distinct().LongCount(), debit));
            foreach (var account in currency.GroupBy(l => l.AccountCode))
            {
                var net = account.Sum(l => l.Side == Sides.Debit ? l.Amount : -l.Amount);
                figures.Add(new ReconciliationFigure(FinanceImportTypes.OpeningBalance, account.Key, currency.Key, account.Select(l => l.JournalId).Distinct().LongCount(), net));
            }
        }

        return figures;
    }
}
