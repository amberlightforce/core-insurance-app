using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Reinsurance.Cession;

/// <summary>pol.ChargeDeltaEmitted (premium delta sets, including out-of-sequence corrections) to the cession.</summary>
internal sealed class ChargeDeltaCessionHandler(CessionIntake intake) : IEventHandler<ChargeDeltaEmittedV1>
{
    public const string Name = "RI.ChargeDeltaEmitted.Cession";

    public Task HandleAsync(EventEnvelope envelope, ChargeDeltaEmittedV1 payload, CancellationToken cancellationToken) =>
        intake.OnChargeDeltaAsync(envelope, payload, cancellationToken);
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
    IOptions<CessionOptions> options,
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

        var cfg = options.Value;
        var now = clock.Now;
        if (!CessionEngine.InBase(cfg.Mode, payload.ChargeCategory))
        {
            return; // NET: tax, levy and stamp lines are not cession base.
        }

        if (!string.Equals(payload.NetAmount.Currency.Code, Eur, StringComparison.Ordinal))
        {
            // TODO(hardening): multi-currency cession.
            await RaiseAsync(termId, eventId, "CESSION_CURRENCY", $"Currency {payload.NetAmount.Currency.Code} is not EUR; not ceded.", now, cancellationToken).ConfigureAwait(false);
            return;
        }

        var from = payload.ValidPeriod.Start;
        var treaties = cfg.Treaties.Where(t => t.CoverageCodes.Contains(payload.CoverageCode, StringComparer.Ordinal)).ToList();
        if (treaties.Count == 0)
        {
            await RaiseAsync(termId, eventId, "CESSION_NO_TREATY", $"No treaty is registered for coverage {payload.CoverageCode}.", now, cancellationToken).ConfigureAwait(false);
            return;
        }

        var matching = treaties.Where(t => Covers(t, from)).ToList();
        if (matching.Count == 0)
        {
            await RaiseAsync(termId, eventId, "CESSION_OUT_OF_PERIOD", $"No treaty for coverage {payload.CoverageCode} covers {from.Value}.", now, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (matching.Count(t => t.Kind == TreatyKinds.QuotaShare) > 1)
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
            await ReconcileAsync(treaty, termId, setKey, cfg.Mode, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReconcileAsync(CessionTreatyOptions treaty, Guid termId, Guid setKey, CessionMode mode, Instant now, CancellationToken cancellationToken)
    {
        if (await db.Cessions.AnyAsync(c => c.TermId == termId && c.TreatyId == treaty.TreatyId && c.SetKey == setKey, cancellationToken).ConfigureAwait(false))
        {
            return; // Once per set_id.
        }

        var coverage = treaty.CoverageCodes;
        var lines = await db.PremiumDeltas.Where(p => p.TermId == termId && coverage.Contains(p.CoverageCode)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var written = lines.Where(p => Covers(treaty, p.ValidFrom)).Sum(p => p.Amount);
        var booked = await db.Cessions.Where(c => c.TermId == termId && c.TreatyId == treaty.TreatyId).SumAsync(c => (decimal?)c.Delta, cancellationToken).ConfigureAwait(false) ?? 0m;
        var target = CessionEngine.Target(treaty.SharePct, written);
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

    private static bool Covers(CessionTreatyOptions treaty, BusinessDate date) => date.Value >= treaty.ValidFrom && date.Value < treaty.ValidTo;

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
