using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Platform.Context;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.MigrationImport;

/// <summary>
/// <c>IMigrationReverser</c> for module BIL (D-SL6-05). An opening receivable is removed only while nothing has been collected against
/// it; a mandate only while no collection has used it. The delete runs in <c>bil.reverse_migrated</c> (SECURITY DEFINER, repeats the
/// check under the row lock): the app role has no DELETE grant on the migration tables.
/// </summary>
internal sealed class BillingMigrationReverser(BillingDbContext db, RequestContext context, ILegalEntityDirectory legalEntities) : IMigrationReverser
{
    public string ModuleName => "BIL";

    public async Task<IReadOnlyList<ReversalOutcome>> ReverseAsync(Guid batchId, bool dryRun, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var outcomes = new List<ReversalOutcome>();
        var receivables = await db.MigrationReceivables.AsNoTracking().Where(r => r.BatchId == batchId && r.LegalEntityId == legalEntity)
            .OrderBy(r => r.ImportedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var r in receivables)
        {
            var activity = r.CollectedAmount > 0 ? "Cash has been collected against the opening receivable." : null;
            outcomes.Add(await ReverseOneAsync(BillingImportTypes.Receivable, "RECEIVABLE", r.ReceivableId, activity, dryRun, cancellationToken).ConfigureAwait(false));
        }

        var mandates = await db.MigrationMandates.AsNoTracking().Where(m => m.BatchId == batchId && m.LegalEntityId == legalEntity)
            .OrderBy(m => m.ImportedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var m in mandates)
        {
            var activity = m.FirstUsedAt is not null ? "The mandate has been used for a collection." : null;
            outcomes.Add(await ReverseOneAsync(BillingImportTypes.Mandate, "MANDATE", m.MandateId, activity, dryRun, cancellationToken).ConfigureAwait(false));
        }

        return outcomes;
    }

    private async Task<ReversalOutcome> ReverseOneAsync(string objectType, string kind, Guid id, string? activity, bool dryRun, CancellationToken cancellationToken)
    {
        if (activity is not null)
        {
            return Blocked(objectType, id, activity);
        }

        if (dryRun)
        {
            return new ReversalOutcome(objectType, id, true);
        }

        var removed = (await db.Database.SqlQuery<bool>($"SELECT bil.reverse_migrated({kind}, {id}) AS \"Value\"").ToListAsync(cancellationToken).ConfigureAwait(false))[0];
        return removed ? new ReversalOutcome(objectType, id, true) : Blocked(objectType, id, "Activity appeared on the object after the migration.");
    }

    private static ReversalOutcome Blocked(string objectType, Guid id, string message) =>
        new(objectType, id, false, MigrationReversalCodes.Blocked, message);
}
