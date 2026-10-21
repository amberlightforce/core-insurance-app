using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Compliance.Contracts;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Claims.MigrationImport;

/// <summary>
/// <c>IMigrationReverser</c> for module CLM (D-SL6-05). Removes an imported claim only while nothing has happened to it since the
/// import: no ledger transaction (so no payment and no reserve change), no recovery, no friendly-settlement case, no re-verification
/// and an unchanged record. The delete runs in <c>clm.reverse_migrated_claim</c> (SECURITY DEFINER, repeats the check under the claim
/// row lock): claim rows are otherwise never deleted. The imported claim clock is cancelled in CMP through
/// <c>cmp.Clock.cancel</c> once the claim is gone (CMP keeps the cancelled instance as its audit trail; cancel is idempotent).
/// </summary>
internal sealed class ClaimMigrationReverser(ClaimsDbContext db, ClaimProtection protection, RequestContext context, IServiceProvider services) : IMigrationReverser
{
    public string ModuleName => "CLM";

    public async Task<IReadOnlyList<ReversalOutcome>> ReverseAsync(Guid batchId, bool dryRun, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var imports = await db.MigrationImports.AsNoTracking()
            .Where(m => m.BatchId == batchId && m.LegalEntityId == legalEntity).OrderBy(m => m.ImportedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
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
                outcomes.Add(new ReversalOutcome(ClaimMigrationImporter.ObjectType, import.ClaimId.Value, true));
                continue;
            }

            var connection = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var removed = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT clm.reverse_migrated_claim(@id)", new { id = import.ClaimId.Value },
                db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (removed && import.ClockId is { } clockId)
            {
                await CancelClockAsync(clockId, batchId, cancellationToken).ConfigureAwait(false);
            }

            outcomes.Add(removed ? new ReversalOutcome(ClaimMigrationImporter.ObjectType, import.ClaimId.Value, true) : Blocked(import, "Activity appeared on the claim after the migration."));
        }

        return outcomes;
    }

    private async Task CancelClockAsync(Guid clockId, Guid batchId, CancellationToken cancellationToken)
    {
        var clocks = services.GetService<IComplianceClockService>()
            ?? throw new InvalidOperationException("CMP is not registered in this deployment; the imported claim clock cannot be cancelled.");
        await clocks.CancelAsync(
            new ClockCancelRequest { InstanceId = clockId, Reason = $"Migration batch {batchId} reversed: the imported claim was removed." },
            CommandOptions.New(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ActivityAsync(ClaimMigrationImportRow import, CancellationToken cancellationToken)
    {
        var claimId = import.ClaimId;
        if (await db.FinancialTransactions.AnyAsync(t => t.ClaimId == claimId, cancellationToken).ConfigureAwait(false)
            || await db.TransactionSets.AnyAsync(t => t.ClaimId == claimId, cancellationToken).ConfigureAwait(false)
            || await db.ClaimPayments.AnyAsync(p => p.ClaimId == claimId, cancellationToken).ConfigureAwait(false))
        {
            return "The claim has a payment or a reserve change since the import.";
        }

        if (await db.Recoveries.AnyAsync(r => r.ClaimId == claimId, cancellationToken).ConfigureAwait(false)
            || await db.Reverifications.AnyAsync(r => r.ClaimId == claimId, cancellationToken).ConfigureAwait(false))
        {
            return "The claim has a recovery or a re-verification since the import.";
        }

        var claim = await db.Claims.AsNoTracking().SingleOrDefaultAsync(c => c.ClaimId == claimId, cancellationToken).ConfigureAwait(false);
        return claim is { RecordVersion: 1, ReopenCount: 0 } ? null : "The claim was changed since the import.";
    }

    private static ReversalOutcome Blocked(ClaimMigrationImportRow import, string message) =>
        new(ClaimMigrationImporter.ObjectType, import.ClaimId.Value, false, MigrationReversalCodes.Blocked, message);
}
