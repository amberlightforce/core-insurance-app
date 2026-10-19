using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Policy.Commands.Change.Oos;

/// <summary>A segment of the corrected timeline: what holds from <paramref name="From"/> to <paramref name="To"/> and which transaction it restates.</summary>
internal sealed record OosSegment(Instant From, Instant To, RiskTree Tree, Guid Source);

/// <summary>The corrected history of a term: the chain, the changes that were reversed and re-applied and the segments from the new change on.</summary>
/// <param name="Chain">All changes in effective order, the new one in its place.</param>
/// <param name="Reversed">The bound transactions reversed and re-applied (those effective after the new change).</param>
/// <param name="Segments">The segments from the new change's effective time to the end of the term.</param>
/// <param name="Mode">The delta mode.</param>
internal sealed record OosPlan(IReadOnlyList<OosEntry> Chain, IReadOnlyList<OosEntry> Reversed, IReadOnlyList<OosSegment> Segments, OosMode Mode);

/// <summary>The priced correction and the plan to write it.</summary>
internal sealed record OosPlanned(ChangePricing Pricing, OosPlan Plan);

/// <summary>
/// Plans an out-of-sequence change (REQ-POL-006, D-SL6-10): the new change goes into the term's effective history at its place, the later
/// changes carry its edit (re-rated with the term's pinned artefacts, never under another configuration, REQ-POL-033), the engine restates
/// the term and the difference to what is written is the correction (NET). Pure of persistence: quote, preview and bind run this one path.
/// </summary>
internal sealed class OosPlanner(ChangeHistory history, IServicingTax tax, RequestContext context, IOptions<ChangeOptions> changeOptions)
{
    /// <summary>Re-rates a risk tree at an effective time under the term's pinned artefacts.</summary>
    internal delegate Task<Result<IReadOnlyList<ChargeRate>>> Rate(RiskTree tree, Instant effectiveAt);

    public async Task<Result<OosPlanned>> PlanAsync(
        JobRow job, ChangeContext change, RiskTree edited, IReadOnlyList<ChargeRate> editedRates, Rate rate, OosMode mode, string jurisdiction, TimeZoneInfo zone,
        CancellationToken cancellationToken)
    {
        var effective = job.EffectiveAt;
        var chain = OosIntents.Chain(change.History.Transactions);
        if (chain is null)
        {
            return DomainError.Of(ModuleCode.POL, "SEGMENT-INVARIANT", "The term history cannot be read for an out-of-sequence change.");
        }

        var later = chain.Where(e => e.EffectiveAt > effective).ToList();
        var earlier = chain.Where(e => e.EffectiveAt <= effective).ToList();
        if (later.Count == 0)
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The change is not out of sequence; no later transaction is bound on the term.");
        }

        var diff = RiskDiffs.Compute(change.BaseTree, edited, changeOptions.Value.EditableVehicleFields);
        if (diff.Violations.Count > 0 || diff.AddedVehicles.Count > 0 || diff.RemovedVehicles.Count > 0)
        {
            return DomainError.Of(
                ModuleCode.POL, "VALIDATION", "An out-of-sequence change may edit the rating fields of the vehicle only; a replaced vehicle is not supported (TODO(hardening)).");
        }

        var thisChange = new OosEntry(effective, editedRates, edited, job.JobId.Value);
        var reapplied = new List<OosEntry>();
        foreach (var entry in later)
        {
            var tree = OosTrees.Propagate(diff.Entries, edited, entry.Tree);
            if (tree is null)
            {
                return DomainError.Of(ModuleCode.POL, "VALIDATION", "The edit cannot be carried over to the later transactions.");
            }

            if (OosTrees.Same(tree, entry.Tree))
            {
                reapplied.Add(entry);
                continue;
            }

            var rated = await rate(tree, entry.EffectiveAt).ConfigureAwait(false);
            if (rated.IsFailure)
            {
                return rated.Error!;
            }

            reapplied.Add(entry with { Tree = tree, Rates = rated.Value });
        }

        var corrected = new List<OosEntry>([.. earlier, thisChange, .. reapplied]);
        var opening = await history.OpenAsync(change.History, change.Engine.Engine, change.Engine.Convention, zone, cancellationToken).ConfigureAwait(false);
        if (opening.IsFailure)
        {
            return opening.Error!;
        }

        var (opened, _) = opening.Value;
        var correlation = new DeltaCorrelation(context.CorrelationId.ToString(), job.JobId.Value.ToString());
        var restated = OosEngine.ApplyChain(change.Engine.Engine, opened, corrected.Select(e => new OosStep(e.EffectiveAt, e.Rates)), correlation);
        if (!restated.IsAccepted)
        {
            return DomainError.Of(ModuleCode.POL, "SEGMENT-INVARIANT", restated.Message ?? "The corrected history does not apply to the term.");
        }

        var term = change.State.Term;
        var lines = OosEngine.Lines(mode, change.State, restated.State!);
        var deltas = OosEngine.NetDeltas(lines, term, effective, correlation);
        IReadOnlyList<PricedTaxLine> taxLines = [];
        if (deltas.Count > 0)
        {
            var taxed = await tax.LinesAsync(
                new ServicingTaxRequest(context.LegalEntity!.Value.Value, jurisdiction, effective.ToBusinessDate(zone), term.Currency, deltas), cancellationToken).ConfigureAwait(false);
            if (taxed.IsFailure)
            {
                return taxed.Error!;
            }

            taxLines = taxed.Value;
            var checkedTax = ChangePricer.CheckTax(deltas, taxLines);
            if (checkedTax is not null)
            {
                return checkedTax;
            }
        }

        var premium = Money.Sum(deltas.Select(d => new Money(d.Amount, term.Currency)), term.Currency);
        var taxes = Money.Sum(taxLines.Select(t => new Money(t.Amount, term.Currency)), term.Currency);
        var pricing = new ChangePricing(change.State, restated.State!, deltas, taxLines, editedRates, premium, taxes);

        // The corrected timeline from the new change on: its own segment, then one per re-applied change, each holding that change's risk.
        var segments = new List<OosSegment>();
        var starts = new List<OosEntry> { thisChange };
        starts.AddRange(reapplied);
        for (var i = 0; i < starts.Count; i++)
        {
            var from = starts[i].EffectiveAt;
            var to = i + 1 < starts.Count ? starts[i + 1].EffectiveAt : term.To;
            if (to > from)
            {
                segments.Add(new OosSegment(from, to, starts[i].Tree, starts[i].Source));
            }
        }

        return new OosPlanned(pricing, new OosPlan(corrected, later, segments, mode));
    }
}
