using System.Globalization;
using CoreIns.Modules.Migration.Commands;
using CoreIns.Modules.Migration.Domain;
using CoreIns.Modules.Migration.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Migration.Services.Waves;

/// <summary>The wave terms a batch converts, by legacy key.</summary>
internal sealed record WaveBatchContext(string WaveName, Dictionary<string, WaveTermRow> Terms);

/// <summary>
/// What a running batch does for a wave (BO-MIG-07): a term that is not offered any more is not imported, and a term whose conversion item is
/// imported becomes CONVERTED and its policy routes to CORE from the renewal date. The changes are staged on the batch run's context, so they are
/// saved in the same save as the item outcome and the xref: the flip never happens without the import being recorded, and a rerun after a lost
/// chunk finds the xref (SKIPPED_EXISTING) and completes the flip.
/// </summary>
internal sealed class WaveConversion(MigrationDbContext db, RoutingPeriods routing, IClock clock)
{
    public async Task<WaveBatchContext?> LoadAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var terms = await db.WaveTerms.Where(t => t.BatchId == batchId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (terms.Count == 0)
        {
            return null;
        }

        var waveId = terms[0].WaveId;
        var name = await db.Waves.AsNoTracking().Where(w => w.WaveId == waveId).Select(w => w.Name).SingleAsync(cancellationToken).ConfigureAwait(false);
        return new WaveBatchContext(name, terms.ToDictionary(t => t.LegacyKey, StringComparer.Ordinal));
    }

    /// <summary>The reason the item is not imported, or null when it may run. A redriven (NOT_CONVERTED) term may run again.</summary>
    public static string? Refuse(WaveBatchContext context, BatchItemRow item) =>
        context.Terms.TryGetValue(item.LegacyId, out var term) && term.State is not (TermState.Offered or TermState.NotConverted)
            ? $"The wave term of {item.LegacyId} is {term.State}; it is not imported."
            : null;

    public async Task AfterItemAsync(WaveBatchContext context, BatchItemRow item, CancellationToken cancellationToken)
    {
        if (!context.Terms.TryGetValue(item.LegacyId, out var term) || term.State is not (TermState.Offered or TermState.NotConverted))
        {
            return;
        }

        term.UpdatedAt = clock.Now;
        if (item.Outcome is ItemOutcome.Imported or ItemOutcome.SkippedExisting && item.CoreId is { } coreId)
        {
            term.State = TermState.Converted;
            term.CoreId = coreId;
            term.Reason = null;
            await FlipAsync(context, term, cancellationToken).ConfigureAwait(false);
        }
        else if (item.Outcome == ItemOutcome.Failed)
        {
            // The policy stays with the legacy system and is free for a later wave.
            term.State = TermState.NotConverted;
            term.Reason = Truncate($"{item.ErrorCode}: {item.Message}");
        }
    }

    private async Task FlipAsync(WaveBatchContext context, WaveTermRow term, CancellationToken cancellationToken)
    {
        var from = new BusinessDate(term.TermEnd);
        var periods = await db.Routes.AsNoTracking().Where(r => r.LegalEntityId == term.LegalEntityId && r.ObjectType == term.ObjectType && r.Key == term.LegacyKey)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (periods.Any(p => p.MasterState == RouteState.Core && p.ValidFrom <= from && (p.ValidTo is null || p.ValidTo > from)))
        {
            return; // already routed to CORE (a rerun)
        }

        var staged = await routing.StageAsync(
            new RoutingOverrideBody(term.ObjectType, term.LegacyKey, RouteState.Core, term.TermEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null, $"Renewal conversion wave {context.WaveName}"),
            cancellationToken).ConfigureAwait(false);
        if (!staged.IsSuccess)
        {
            // TODO(hardening): the policy is imported but its routing could not be flipped (an overlapping period); today the term is CONVERTED with the reason
            // and an operator fixes the routing with mig.Routing.override. Pre-check the routing before the import to refuse the item instead.
            term.Reason = Truncate($"ROUTING: {staged.Error.Detail}");
        }
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500];
}
