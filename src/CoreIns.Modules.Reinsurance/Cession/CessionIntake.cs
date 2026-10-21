using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreIns.Modules.Reinsurance.Cession;

/// <summary>pol.ChargeDeltaEmitted (premium delta sets, including out-of-sequence corrections) to the cession.</summary>
internal sealed class ChargeDeltaCessionHandler(CessionIntake intake) : IEventHandler<ChargeDeltaEmittedV1>
{
    public const string Name = "RI.ChargeDeltaEmitted.Cession";

    public Task HandleAsync(EventEnvelope envelope, ChargeDeltaEmittedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // Opening deltas of a migrated in-force policy are history, not new business: they are not ceded again (E2E-05 step 3).
        // E2E-05: the legacy cession of the in-force part stays with the migration; ceding the unexpired part needs the cut-over date and the legacy treaty, so it is deferred. TODO(hardening)
        return payload.Opening == true ? Task.CompletedTask : intake.OnChargeDeltaAsync(envelope, payload, cancellationToken);
    }
}

/// <summary>
/// Cession target minus booked (SL6-RI-CESSION-DELTA). Each premium delta is stored once (by event id). When the delta's set is
/// complete (D4: set_size members seen; an event without a set is its own set) the written premium of the term is summed from the
/// ledger and, per quota-share treaty, target = share x premium; the difference to the booked cessions of the term is recorded once
/// per (term, treaty, set). A POL out-of-sequence reverse-and-reapply is just more deltas in the ledger, so the next set
/// re-converges booked to target. Unknown coverage, or a period no treaty covers, is an exception row, never a guess.
/// </summary>
internal sealed class CessionIntake(
    ReinsuranceDbContext db,
    CessionTreatyRegistry treaties,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock)
{
    private const string Eur = "EUR";

    private Guid LegalEntityId => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;

    public async Task OnChargeDeltaAsync(EventEnvelope envelope, ChargeDeltaEmittedV1 payload, CancellationToken cancellationToken)
    {
        var termId = payload.TermId.Value;
        await LockAsync("ri:cession:" + termId.ToString("D"), cancellationToken).ConfigureAwait(false);
        var eventId = envelope.EventId.Value;
        if (await db.PremiumDeltas.AnyAsync(p => p.EventId == eventId, cancellationToken).ConfigureAwait(false))
        {
            return; // Redelivery or replay: nothing changes.
        }

        var now = clock.Now;
        var from = payload.ValidPeriod.Start;
        // The approved treaties in force come from the registry (NX-HD-07); with none for the coverage, only a premium line is "in base" (NET default).
        var forCoverage = await treaties.ForCoverageAsync(payload.CoverageCode, cancellationToken).ConfigureAwait(false);
        var inBase = forCoverage.Count == 0
            ? CessionEngine.InBase(CessionMode.Net, payload.ChargeCategory)
            : forCoverage.Any(t => CessionEngine.InBase(t.Base, payload.ChargeCategory));
        // Per-treaty currency: the line must be in the currency of every treaty that would cede it; no conversion is guessed.
        var wrongCurrency = forCoverage.FirstOrDefault(t => CessionEngine.InBase(t.Base, payload.ChargeCategory) && Covers(t, from)
            && !string.Equals(t.Currency.Trim(), payload.NetAmount.Currency.Code, StringComparison.Ordinal));
        if (wrongCurrency is not null || (forCoverage.Count == 0 && inBase && !string.Equals(payload.NetAmount.Currency.Code, Eur, StringComparison.Ordinal)))
        {
            // TODO(hardening): currency conversion (FX rate at the booking date) instead of an exception row.
            await RaiseAsync(termId, eventId, "CESSION_CURRENCY", $"Currency {payload.NetAmount.Currency.Code} is not the treaty currency {wrongCurrency?.Currency.Trim() ?? Eur}; not ceded.", now, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (forCoverage.Count == 0 && inBase)
        {
            await RaiseAsync(termId, eventId, "CESSION_NO_TREATY", $"No treaty is registered for coverage {payload.CoverageCode}.", now, cancellationToken).ConfigureAwait(false);
            return;
        }

        var matching = forCoverage.Where(t => Covers(t, from)).ToList();
        if (matching.Count == 0 && inBase)
        {
            await RaiseAsync(termId, eventId, "CESSION_OUT_OF_PERIOD", $"No treaty for coverage {payload.CoverageCode} covers {from.Value}.", now, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (inBase && matching.Count(t => t.Kind == TreatyKinds.QuotaShare && CessionEngine.InBase(t.Base, payload.ChargeCategory)) > 1)
        {
            await RaiseAsync(termId, eventId, "CESSION_AMBIGUOUS", $"More than one quota share covers {payload.CoverageCode} on {from.Value}.", now, cancellationToken).ConfigureAwait(false);
            return;
        }

        var policyId = envelope.BusinessKeys.TryGetValue("policyId", out var key) && Guid.TryParse(key, out var parsed) ? parsed : Guid.Empty;
        db.PremiumDeltas.Add(new PremiumDeltaRow
        {
            EventId = eventId,
            LegalEntityId = LegalEntityId,
            PolicyId = policyId,
            TermId = termId,
            CoverageCode = payload.CoverageCode,
            ChargeCategory = payload.ChargeCategory,
            Amount = payload.NetAmount.Amount,
            ValidFrom = from,
            SetId = envelope.Set?.SetId,
            SetSize = envelope.Set?.Size ?? 1,
            CorrectionOf = payload.CorrectionOf,
            OosSetId = payload.OosSetId,
            RecordedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var setKey = envelope.Set?.SetId ?? eventId;
        if (envelope.Set is { } set)
        {
            var seen = await db.PremiumDeltas.CountAsync(p => p.SetId == set.SetId && p.TermId == termId, cancellationToken).ConfigureAwait(false);
            if (seen < set.Size)
            {
                return; // The set is incomplete: nothing is booked on a partial set (D4).
            }
        }

        foreach (var treaty in matching.Where(t => t.Kind == TreatyKinds.QuotaShare))
        {
            await ReconcileAsync(treaty, forCoverage.Where(t => t.Kind == TreatyKinds.QuotaShare && CessionEngine.InBase(t.Base, payload.ChargeCategory)).ToList(), termId, setKey, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReconcileAsync(CessionTreatyRow treaty, IReadOnlyList<CessionTreatyRow> quotaShares, Guid termId, Guid setKey, Instant now, CancellationToken cancellationToken)
    {
        if (await db.Cessions.AnyAsync(c => c.TermId == termId && c.TreatyId == treaty.TreatyId && c.SetKey == setKey, cancellationToken).ConfigureAwait(false))
        {
            return; // Once per set_id.
        }

        var mode = treaty.Base;
        var coverage = treaty.CoverageCodes;
        var lines = await db.PremiumDeltas.Where(p => p.TermId == termId && coverage.Contains(p.CoverageCode)).ToListAsync(cancellationToken).ConfigureAwait(false);
        // Lines outside the cession base (NET: tax, levies) are stored only so that a set counts as complete.
        var written = WrittenFor(treaty, lines);
        var booked = await db.Cessions.Where(c => c.TermId == termId && c.TreatyId == treaty.TreatyId).SumAsync(c => (decimal?)c.Delta, cancellationToken).ConfigureAwait(false) ?? 0m;
        // Joint largest-remainder target over every quota share of the term, so the cents ceded across treaties (premium lines either
        // side of a treaty boundary) sum to the rounded total share instead of drifting by independent rounding (NX-EC-06).
        // TODO(hardening): a sibling treaty not matched by this set converges at its own next set, not here.
        var group = quotaShares.Where(t => t.TreatyVersionId != treaty.TreatyVersionId).Append(treaty)
            .OrderBy(t => t.TreatyId, StringComparer.Ordinal).ThenBy(t => t.ContractYear).ThenBy(t => t.TreatyVersionId).ToList();
        var target = CessionEngine.JointTargets(group.Select(t => (t.SharePct, WrittenFor(t, lines))).ToList())[group.FindIndex(t => t.TreatyVersionId == treaty.TreatyVersionId)];
        var difference = CessionEngine.Difference(target, booked);
        if (difference == 0m)
        {
            return; // Already converged: a replayed or no-op set adds no row.
        }

        db.Cessions.Add(new CessionRow
        {
            CessionId = Guid.CreateVersion7(),
            LegalEntityId = LegalEntityId,
            TreatyId = treaty.TreatyId,
            ContractYear = treaty.ContractYear,
            TermId = termId,
            SetKey = setKey,
            WrittenPremium = written,
            Target = target,
            BookedBefore = booked,
            Delta = difference,
            Mode = mode == CessionMode.Gross ? "GROSS" : "NET",
            RecordedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static decimal WrittenFor(CessionTreatyRow treaty, List<PremiumDeltaRow> lines) =>
        lines.Where(p => treaty.CoverageCodes.Contains(p.CoverageCode) && CessionEngine.InBase(treaty.Base, p.ChargeCategory) && Covers(treaty, p.ValidFrom)).Sum(p => p.Amount);

    private static bool Covers(CessionTreatyRow treaty, BusinessDate date) => date >= treaty.ValidFrom && date < treaty.ValidTo;

    private async Task RaiseAsync(Guid termId, Guid eventId, string code, string detail, Instant now, CancellationToken cancellationToken)
    {
        db.CessionExceptions.Add(new CessionExceptionRow
        {
            ExceptionId = Guid.CreateVersion7(), LegalEntityId = LegalEntityId, TermId = termId, SourceEventId = eventId, Code = code, Detail = detail, RecordedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<int> LockAsync(string key, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", [new NpgsqlParameter("k", key)], cancellationToken);
}
