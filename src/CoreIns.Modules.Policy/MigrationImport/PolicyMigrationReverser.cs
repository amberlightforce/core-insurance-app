using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Modules.Policy.Commands;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CoreIns.Modules.Policy.MigrationImport;

/// <summary>
/// <c>IMigrationReverser</c> for module POL (D-SL6-05). Removes an imported policy only while it still has nothing but its OPENING
/// transaction: any other transaction, a later term version, a job or a pack-rollback exception on it is post-migration activity and
/// the policy is reported <c>MIG-ERR-REVERSAL-BLOCKED</c>. The delete itself runs in <c>pol.reverse_migrated_policy</c> (SECURITY
/// DEFINER, repeats the check under the policy row lock): the append-only triggers refuse any other delete.
/// </summary>
internal sealed class PolicyMigrationReverser(PolicyDbContext db, RequestContext context, ILegalEntityDirectory legalEntities) : IMigrationReverser
{
    public string ModuleName => "POL";

    public async Task<IReadOnlyList<ReversalOutcome>> ReverseAsync(Guid batchId, bool dryRun, CancellationToken cancellationToken)
    {
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var imports = await db.MigrationImports.AsNoTracking()
            .Where(m => m.BatchId == batchId && m.LegalEntityId == legalEntity).OrderBy(m => m.ImportedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var outcomes = new List<ReversalOutcome>(imports.Count);
        foreach (var import in imports)
        {
            var activity = await ActivityAsync(import.PolicyId, cancellationToken).ConfigureAwait(false);
            if (activity is not null)
            {
                outcomes.Add(Blocked(import, activity));
                continue;
            }

            if (dryRun)
            {
                outcomes.Add(new ReversalOutcome(PolicyImportModes.ObjectType, import.PolicyId.Value, true));
                continue;
            }

            var connection = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var removed = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT pol.reverse_migrated_policy(@id)", new { id = import.PolicyId.Value },
                db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: cancellationToken)).ConfigureAwait(false);
            outcomes.Add(removed
                ? new ReversalOutcome(PolicyImportModes.ObjectType, import.PolicyId.Value, true)
                : Blocked(import, "Activity appeared on the policy after the migration."));
        }

        return outcomes;
    }

    private async Task<string?> ActivityAsync(PolicyId policyId, CancellationToken cancellationToken)
    {
        var opening = Codes.Of(PolicyTransactionKind.Opening);
        if (await db.Transactions.AnyAsync(t => t.PolicyId == policyId && t.Kind != opening, cancellationToken).ConfigureAwait(false))
        {
            return "The policy has a transaction other than its OPENING transaction.";
        }

        if (await db.Jobs.AnyAsync(j => j.PolicyId == policyId, cancellationToken).ConfigureAwait(false))
        {
            return "The policy has a job (renewal or servicing) since the migration.";
        }

        return null;
    }

    private static ReversalOutcome Blocked(MigrationImportRow import, string message) =>
        new(PolicyImportModes.ObjectType, import.PolicyId.Value, false, MigrationReversalCodes.Blocked, message);
}
