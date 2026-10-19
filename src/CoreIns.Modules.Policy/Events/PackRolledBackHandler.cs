using CoreIns.Modules.Market.Contracts.Events;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Policy.Events;

/// <summary>
/// <c>mkt.PackRolledBack</c> → POL's exception queue (REQ-POL-354 subset, D-SL5-10). Finds every bound transaction and every open
/// quote version whose stored configuration hash is one the rolled-back pack version issued, writes one exception per affected bound
/// transaction (unique per activation and transaction, so a redelivery adds nothing) and marks the open quotes stale
/// (<c>PACK_ROLLBACK</c>; bind already refuses them). It never touches segments, charges or terms: no delta, no re-rating, no watermark bump.
/// </summary>
internal sealed class PackRolledBackHandler(PolicyDbContext db, ILegalEntityDirectory legalEntities, IClock clock) : IEventHandler<PackRolledBackV1>
{
    public const string Name = "POL.PackRolledBack.RaiseExceptions";

    public async Task HandleAsync(EventEnvelope envelope, PackRolledBackV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(payload);
        var hashes = payload.HashesIssued.Select(h => h.Value).Distinct(StringComparer.Ordinal).ToList();
        if (hashes.Count == 0)
        {
            return;
        }

        var legalEntity = legalEntities.Resolve(envelope.LegalEntity);
        var now = clock.Now;

        var affected = await db.Transactions.AsNoTracking()
            .Where(t => t.LegalEntityId == legalEntity && hashes.Contains(t.ConfigurationHash))
            .Select(t => new { t.TransactionId, t.PolicyId, t.TermId, t.Kind, t.ConfigurationHash })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var already = await db.PackRollbackExceptions.AsNoTracking()
            .Where(e => e.ActivationId == payload.ActivationId).Select(e => e.TransactionId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var seen = already.ToHashSet();
        var termIds = affected.Select(a => a.TermId).Distinct().ToList();
        var terms = await db.Terms.AsNoTracking().Where(t => termIds.Contains(t.TermId) && t.RecordedTo == null)
            .Select(t => new { t.TermId, t.ProductVersion, t.ValidFrom }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var productByTerm = terms.GroupBy(t => t.TermId).ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.ValidFrom).First().ProductVersion);

        foreach (var tx in affected.Where(a => !seen.Contains(a.TransactionId)))
        {
            db.PackRollbackExceptions.Add(new PackRollbackExceptionRow
            {
                ExceptionId = Guid.CreateVersion7(),
                ActivationId = payload.ActivationId,
                LegalEntityId = legalEntity,
                Pack = payload.Pack,
                FromVersion = payload.FromVersion,
                ToVersion = payload.ToVersion,
                PolicyId = tx.PolicyId,
                TermId = tx.TermId,
                TransactionId = tx.TransactionId,
                TransactionKind = tx.Kind,
                ConfigurationHash = tx.ConfigurationHash,
                ProductVersion = productByTerm.GetValueOrDefault(tx.TermId, string.Empty),
                IdentifiedAt = now,
                SourceEventId = envelope.EventId.Value,
            });
        }

        var quotes = await db.QuoteVersions
            .Where(q => q.LegalEntityId == legalEntity && q.State == "QUOTED" && q.StaleReason == null && q.ConfigurationHash != null && hashes.Contains(q.ConfigurationHash))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var quote in quotes)
        {
            quote.StaleReason = "PACK_ROLLBACK";
            quote.RecordVersion++;
            quote.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
