using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Platform.Context;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Party.MigrationImport;

/// <summary>
/// <c>IMigrationReverser</c> for module PTY (D-SL6-05). Removes only the parties and contact points stamped with the batch
/// (<c>source_channel = MIGRATION:{batch}:{legacyId}</c>, contact <c>source = MIGRATION:{batch}</c>). A party with a DSAR restriction,
/// an intermediary record or any change since the import (record version above 1) is blocked. The delete runs in
/// <c>pty.reverse_migrated</c> (SECURITY DEFINER): the app role has no DELETE grant.
/// </summary>
internal sealed class PartyMigrationReverser(PartyDbContext db, RequestContext context, ILegalEntityDirectory legalEntities) : IMigrationReverser
{
    public string ModuleName => "PTY";

    public async Task<IReadOnlyList<ReversalOutcome>> ReverseAsync(Guid batchId, bool dryRun, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var outcomes = new List<ReversalOutcome>();
        var prefix = PartyImportMarks.PartyPrefix(batchId);
        var contactSource = PartyImportMarks.ContactSource(batchId);

        var contacts = await db.ContactPoints.AsNoTracking()
            .Where(c => c.LegalEntityId == legalEntity && c.Source == contactSource && c.RecordedTo == null)
            .Select(c => new { c.ContactPointId, c.PartyId }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var parties = await db.Parties.AsNoTracking()
            .Where(p => p.LegalEntityId == legalEntity && p.SourceChannel != null && p.SourceChannel.StartsWith(prefix))
            .Select(p => new { p.PartyId, p.RecordVersion, p.PartyType }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var batchPartyIds = parties.Select(p => p.PartyId).ToHashSet();

        // A contact of a batch party goes with the party; one added to an older party is removed on its own.
        foreach (var contact in contacts.Where(c => !batchPartyIds.Contains(c.PartyId)))
        {
            outcomes.Add(await ReverseOneAsync(PartyImportTypes.Contact, "CONTACT", batchId, contact.ContactPointId, null, dryRun, cancellationToken).ConfigureAwait(false));
        }

        var restricted = await db.DsarRestrictions.AsNoTracking().Where(r => batchPartyIds.Contains(r.PartyId)).Select(r => r.PartyId).Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var party in parties)
        {
            var activity = restricted.Contains(party.PartyId) ? "The party is under a DSAR restriction and must be retained."
                : party.RecordVersion > 1 ? "The party has been changed since the import."
                : null;
            outcomes.Add(await ReverseOneAsync(party.PartyType == "PERSON" ? PartyImportTypes.Person : PartyImportTypes.Organisation, "PARTY", batchId, party.PartyId.Value, activity, dryRun, cancellationToken).ConfigureAwait(false));
        }

        return outcomes;
    }

    private async Task<ReversalOutcome> ReverseOneAsync(string objectType, string kind, Guid batchId, Guid id, string? activity, bool dryRun, CancellationToken cancellationToken)
    {
        if (activity is not null)
        {
            return Blocked(objectType, id, activity);
        }

        if (dryRun)
        {
            return new ReversalOutcome(objectType, id, true);
        }

        var removed = (await db.Database.SqlQuery<bool>($"SELECT pty.reverse_migrated({kind}, {batchId}, {id}) AS \"Value\"").ToListAsync(cancellationToken).ConfigureAwait(false))[0];
        return removed ? new ReversalOutcome(objectType, id, true) : Blocked(objectType, id, "Activity appeared on the object after the migration.");
    }

    private static ReversalOutcome Blocked(string objectType, Guid id, string message) =>
        new(objectType, id, false, MigrationReversalCodes.Blocked, message);
}
