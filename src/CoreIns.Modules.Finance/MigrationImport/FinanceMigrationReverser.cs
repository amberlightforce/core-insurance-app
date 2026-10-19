using CoreIns.Modules.Finance.Persistence;
using CoreIns.Modules.Finance.Posting;
using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.MigrationImport;

/// <summary>
/// <c>IMigrationReverser</c> for module FIN (D-SL6-05). Journals are append-only, so an opening balance is reversed by a reversal
/// journal (REQ-FIN-072). It is allowed only while nothing was posted against the opening balance: any other journal that touches one
/// of its accounts after it (other than another migration opening balance) is post-migration activity and blocks the reversal.
/// </summary>
internal sealed class FinanceMigrationReverser(FinanceDbContext db, JournalReversal reversal, RequestContext context, ILegalEntityDirectory legalEntities) : IMigrationReverser
{
    public string ModuleName => "FIN";

    public async Task<IReadOnlyList<ReversalOutcome>> ReverseAsync(Guid batchId, bool dryRun, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var imports = await db.MigrationImports.AsNoTracking().Where(m => m.BatchId == batchId && m.LegalEntityId == legalEntity.Value)
            .OrderBy(m => m.ImportedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var outcomes = new List<ReversalOutcome>(imports.Count);
        foreach (var import in imports)
        {
            var activity = await ActivityAsync(import, cancellationToken).ConfigureAwait(false);
            if (activity is not null)
            {
                outcomes.Add(Blocked(import, activity));
                continue;
            }

            if (dryRun)
            {
                outcomes.Add(new ReversalOutcome(FinanceImportTypes.OpeningBalance, import.JournalId, true));
                continue;
            }

            // TODO(hardening): the reversal is dated like the opening balance; a closed period would need the next open date.
            var result = await reversal.ReverseAsync(
                legalEntity, import.JournalId, new BusinessDate(import.AccountingDate), $"Migration batch {batchId} reversed.", cancellationToken).ConfigureAwait(false);
            outcomes.Add(result.IsSuccess
                ? new ReversalOutcome(FinanceImportTypes.OpeningBalance, import.JournalId, true)
                : Blocked(import, result.Error!.Detail ?? "The opening-balance journal is already reversed."));
        }

        return outcomes;
    }

    private async Task<string?> ActivityAsync(MigrationImportRow import, CancellationToken cancellationToken)
    {
        if (await db.Journals.AnyAsync(j => j.ReversesJournalId == import.JournalId, cancellationToken).ConfigureAwait(false))
        {
            return "The opening balance is already reversed.";
        }

        var opened = await db.Journals.AsNoTracking().Where(j => j.JournalId == import.JournalId).Select(j => j.PostedAt).SingleAsync(cancellationToken).ConfigureAwait(false);
        var accounts = await db.JournalLines.AsNoTracking().Where(l => l.JournalId == import.JournalId).Select(l => l.AccountCode).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var migrated = db.MigrationImports.Select(m => m.JournalId);
        var touched = await db.JournalLines.AsNoTracking()
            .Where(l => l.LegalEntityId == import.LegalEntityId && l.Book == import.Book && accounts.Contains(l.AccountCode) && l.JournalId != import.JournalId)
            .Join(db.Journals, l => l.JournalId, j => j.JournalId, (l, j) => j)
            .AnyAsync(j => j.PostedAt >= opened && !migrated.Contains(j.JournalId), cancellationToken).ConfigureAwait(false);
        return touched ? "A journal was posted against the opening balance's accounts after the migration." : null;
    }

    private static ReversalOutcome Blocked(MigrationImportRow import, string message) =>
        new(FinanceImportTypes.OpeningBalance, import.JournalId, false, MigrationReversalCodes.Blocked, message);
}
