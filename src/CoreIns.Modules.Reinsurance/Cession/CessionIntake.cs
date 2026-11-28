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

/// <summary>One premium line as the intake sees it; <see cref="Currency"/> is null for a ledger line (the ledger keeps no currency).</summary>
internal sealed record DeltaLine(
    Guid EventId, Guid PolicyId, Guid TermId, string Coverage, string Category, decimal Amount, string? Currency, BusinessDate ValidFrom,
    Guid? SetId, int SetSize, Guid? CorrectionOf, Guid? OosSetId);

/// <summary>Why the intake cannot apply a line (the exception row it would raise).</summary>
internal sealed record CessionFailure(string Code, string Detail);

/// <summary>Result of <see cref="CessionIntake.CheckAsync"/>: the failure (null when the line can be ceded), the treaties of the coverage and those covering the line's date.</summary>
internal sealed record LineCheck(CessionFailure? Failure, IReadOnlyList<CessionTreatyRow> ForCoverage, IReadOnlyList<CessionTreatyRow> Matching);

/// <summary>One treaty of a term's cession plan: target, booked and the difference still to book.</summary>
internal sealed record TermCessionPlan(Guid TermId, CessionTreatyRow Treaty, decimal Written, decimal Target, decimal Booked, decimal Delta);

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
        var policyId = envelope.BusinessKeys.TryGetValue("policyId", out var key) && Guid.TryParse(key, out var parsed) ? parsed : Guid.Empty;
        var line = new DeltaLine(
            eventId, policyId, termId, payload.CoverageCode, payload.ChargeCategory, payload.NetAmount.Amount, payload.NetAmount.Currency.Code, payload.ValidPeriod.Start,
            envelope.Set?.SetId, envelope.Set?.Size ?? 1, payload.CorrectionOf, payload.OosSetId);
        var check = await CheckAsync(line, cancellationToken).ConfigureAwait(false);
        if (check.Failure is { } failure)
        {
            await RaiseAsync(line, failure, now, cancellationToken).ConfigureAwait(false);
            return;
        }

        db.PremiumDeltas.Add(ToRow(line, now));
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

        foreach (var treaty in check.Matching.Where(t => t.Kind == TreatyKinds.QuotaShare))
        {
            await ReconcileAsync(treaty, check.ForCoverage.Where(t => t.Kind == TreatyKinds.QuotaShare && CessionEngine.InBase(t.Base, line.Category)).ToList(), termId, setKey, now, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The checks the intake applies to one premium line: the treaties that cover it, or the exception it would raise (nothing is written).</summary>
    public async Task<LineCheck> CheckAsync(DeltaLine line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        var from = line.ValidFrom;
        // The approved treaties in force come from the registry (NX-HD-07); with none for the coverage, only a premium line is "in base" (NET default).
        var forCoverage = await treaties.ForCoverageAsync(line.Coverage, cancellationToken).ConfigureAwait(false);
        var inBase = forCoverage.Count == 0
            ? CessionEngine.InBase(CessionMode.Net, line.Category)
            : forCoverage.Any(t => CessionEngine.InBase(t.Base, line.Category));
        // Per-treaty currency: the line must be in the currency of every treaty that would cede it; no conversion is guessed. A ledger line has no currency to check.
        if (line.Currency is { } currency)
        {
            var wrongCurrency = forCoverage.FirstOrDefault(t => CessionEngine.InBase(t.Base, line.Category) && Covers(t, from) && !string.Equals(t.Currency.Trim(), currency, StringComparison.Ordinal));
            if (wrongCurrency is not null || (forCoverage.Count == 0 && inBase && !string.Equals(currency, Eur, StringComparison.Ordinal)))
            {
                // TODO(hardening): currency conversion (FX rate at the booking date) instead of an exception row.
                return new(new("CESSION_CURRENCY", $"Currency {currency} is not the treaty currency {wrongCurrency?.Currency.Trim() ?? Eur}; not ceded."), forCoverage, []);
            }
        }

        if (forCoverage.Count == 0 && inBase)
        {
            return new(new("CESSION_NO_TREATY", $"No treaty is registered for coverage {line.Coverage}."), forCoverage, []);
        }

        var matching = forCoverage.Where(t => Covers(t, from)).ToList();
        if (matching.Count == 0 && inBase)
        {
            return new(new("CESSION_OUT_OF_PERIOD", $"No treaty for coverage {line.Coverage} covers {from.Value}."), forCoverage, []);
        }

        if (inBase && matching.Count(t => t.Kind == TreatyKinds.QuotaShare && CessionEngine.InBase(t.Base, line.Category)) > 1)
        {
            return new(new("CESSION_AMBIGUOUS", $"More than one quota share covers {line.Coverage} on {from.Value}."), forCoverage, []);
        }

        return new(null, forCoverage, matching);
    }

    /// <summary>
    /// The cession of a term as it stands now: per quota share, target (from the premium ledger plus the hypothetical <paramref name="extra"/> lines) against
    /// what is booked. Writes nothing; the caller books the nonzero differences (rerun) or only shows them (preview).
    /// </summary>
    public async Task<IReadOnlyList<TermCessionPlan>> PlanTermAsync(Guid termId, IReadOnlyList<PremiumDeltaRow> extra, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(extra);
        var lines = await db.PremiumDeltas.AsNoTracking().Where(p => p.TermId == termId).ToListAsync(cancellationToken).ConfigureAwait(false);
        lines.AddRange(extra);
        var quotaShares = new Dictionary<Guid, CessionTreatyRow>();
        foreach (var coverage in lines.Select(l => l.CoverageCode).Distinct(StringComparer.Ordinal))
        {
            foreach (var treaty in (await treaties.ForCoverageAsync(coverage, cancellationToken).ConfigureAwait(false)).Where(t => t.Kind == TreatyKinds.QuotaShare))
            {
                quotaShares[treaty.TreatyVersionId] = treaty;
            }
        }

        var plans = new List<TermCessionPlan>();
        foreach (var treaty in quotaShares.Values.OrderBy(t => t.TreatyId, StringComparer.Ordinal).ThenBy(t => t.ContractYear).ThenBy(t => t.TreatyVersionId))
        {
            var written = WrittenFor(treaty, lines);
            var booked = await db.Cessions.AsNoTracking().Where(c => c.TermId == termId && c.TreatyId == treaty.TreatyId).SumAsync(c => (decimal?)c.Delta, cancellationToken).ConfigureAwait(false) ?? 0m;
            if (written == 0m && booked == 0m)
            {
                continue;
            }

            // TODO(hardening): the joint-rounding group is "quota shares sharing a coverage and base"; the event intake groups by the delta's coverage and category.
            var group = quotaShares.Values.Where(t => t.Base == treaty.Base && t.CoverageCodes.Intersect(treaty.CoverageCodes).Any())
                .OrderBy(t => t.TreatyId, StringComparer.Ordinal).ThenBy(t => t.ContractYear).ThenBy(t => t.TreatyVersionId).ToList();
            var target = CessionEngine.JointTargets(group.Select(t => (t.SharePct, WrittenFor(t, lines))).ToList())[group.FindIndex(t => t.TreatyVersionId == treaty.TreatyVersionId)];
            plans.Add(new(termId, treaty, written, target, booked, CessionEngine.Difference(target, booked)));
        }

        return plans;
    }

    /// <summary>The terms of a policy known from its premium ledger.</summary>
    public async Task<IReadOnlyList<Guid>> TermsOfPolicyAsync(Guid policyId, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntityId;
        return await db.PremiumDeltas.AsNoTracking().Where(p => p.LegalEntityId == legalEntity && p.PolicyId == policyId).Select(p => p.TermId).Distinct().OrderBy(t => t).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replays one term: books the nonzero differences of <see cref="PlanTermAsync"/> as cession rows of <paramref name="runId"/> (and of the exception, when given).
    /// A second run finds target equal to booked and writes nothing, so a rerun only ever writes deltas.
    /// </summary>
    public async Task<IReadOnlyList<(TermCessionPlan Plan, CessionRow? Row)>> RerunTermAsync(Guid termId, Guid runId, Guid? exceptionId, CancellationToken cancellationToken)
    {
        await LockAsync("ri:cession:" + termId.ToString("D"), cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        var result = new List<(TermCessionPlan, CessionRow?)>();
        foreach (var plan in await PlanTermAsync(termId, [], cancellationToken).ConfigureAwait(false))
        {
            CessionRow? row = null;
            if (plan.Delta != 0m)
            {
                row = new CessionRow
                {
                    CessionId = Guid.CreateVersion7(),
                    LegalEntityId = LegalEntityId,
                    TreatyId = plan.Treaty.TreatyId,
                    ContractYear = plan.Treaty.ContractYear,
                    TermId = termId,
                    SetKey = runId,
                    WrittenPremium = plan.Written,
                    Target = plan.Target,
                    BookedBefore = plan.Booked,
                    Delta = plan.Delta,
                    Mode = plan.Treaty.Base == CessionMode.Gross ? "GROSS" : "NET",
                    RecordedAt = now,
                    RunId = runId,
                    ExceptionId = exceptionId,
                };
                db.Cessions.Add(row);
            }

            result.Add((plan, row));
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Replays the premium line an exception held: when the line now passes the intake checks it joins the ledger (once, by event id) and the term is rerun;
    /// when it still fails, nothing changes and the failure is returned.
    /// </summary>
    public async Task<(CessionFailure? Failure, IReadOnlyList<(TermCessionPlan Plan, CessionRow? Row)> Deltas)> RerunExceptionAsync(CessionExceptionRow exception, Guid runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        await LockAsync("ri:cession:" + exception.TermId.ToString("D"), cancellationToken).ConfigureAwait(false);
        if (exception.CoverageCode is { } coverage && exception.ChargeCategory is { } category && exception.Amount is { } amount && exception.ValidFrom is { } validFrom)
        {
            var line = new DeltaLine(
                exception.SourceEventId, exception.PolicyId ?? Guid.Empty, exception.TermId, coverage, category, amount, exception.Currency?.Trim(), validFrom,
                exception.SetId, exception.SetSize ?? 1, exception.CorrectionOf, exception.OosSetId);
            var check = await CheckAsync(line, cancellationToken).ConfigureAwait(false);
            if (check.Failure is { } failure)
            {
                return (failure, []);
            }

            if (!await db.PremiumDeltas.AnyAsync(p => p.EventId == line.EventId, cancellationToken).ConfigureAwait(false))
            {
                db.PremiumDeltas.Add(ToRow(line, clock.Now));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // TODO(hardening): an exception raised before the held line was stored (older rows) can only re-converge the ledger it already has.
        return (null, await RerunTermAsync(exception.TermId, runId, exception.ExceptionId, cancellationToken).ConfigureAwait(false));
    }

    private PremiumDeltaRow ToRow(DeltaLine line, Instant now) => new()
    {
        EventId = line.EventId,
        LegalEntityId = LegalEntityId,
        PolicyId = line.PolicyId,
        TermId = line.TermId,
        CoverageCode = line.Coverage,
        ChargeCategory = line.Category,
        Amount = line.Amount,
        ValidFrom = line.ValidFrom,
        SetId = line.SetId,
        SetSize = line.SetSize,
        CorrectionOf = line.CorrectionOf,
        OosSetId = line.OosSetId,
        RecordedAt = now,
    };

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

    internal static decimal WrittenFor(CessionTreatyRow treaty, List<PremiumDeltaRow> lines) =>
        lines.Where(p => treaty.CoverageCodes.Contains(p.CoverageCode) && CessionEngine.InBase(treaty.Base, p.ChargeCategory) && Covers(treaty, p.ValidFrom)).Sum(p => p.Amount);

    internal static bool Covers(CessionTreatyRow treaty, BusinessDate date) => date >= treaty.ValidFrom && date < treaty.ValidTo;

    private async Task RaiseAsync(DeltaLine line, CessionFailure failure, Instant now, CancellationToken cancellationToken)
    {
        db.CessionExceptions.Add(new CessionExceptionRow
        {
            ExceptionId = Guid.CreateVersion7(),
            LegalEntityId = LegalEntityId,
            TermId = line.TermId,
            SourceEventId = line.EventId,
            Code = failure.Code,
            Detail = failure.Detail,
            RecordedAt = now,
            PolicyId = line.PolicyId,
            CoverageCode = line.Coverage,
            ChargeCategory = line.Category,
            Amount = line.Amount,
            Currency = line.Currency,
            ValidFrom = line.ValidFrom,
            SetId = line.SetId,
            SetSize = line.SetSize,
            CorrectionOf = line.CorrectionOf,
            OosSetId = line.OosSetId,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<int> LockAsync(string key, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", [new NpgsqlParameter("k", key)], cancellationToken);
}
