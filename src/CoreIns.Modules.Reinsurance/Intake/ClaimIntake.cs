using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Modules.Reinsurance.Recovery;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreIns.Modules.Reinsurance.Intake;

/// <summary>
/// Builds RI's claim view from CLM events and triggers the recalculation of every contract year the claim touches
/// (REQ-RI-113, -114, -115, -122). Personal data is never stored: ids and amounts only.
/// <list type="bullet">
/// <item>Idempotent: the outbox dispatcher records each event per handler (<c>plt.processed_event</c>); additive facts (payments,
/// voids, recoveries) are therefore applied once, and the state-setting facts (open reserve, closed) carry the aggregate sequence
/// they were last set by, so an older event arriving late is a no-op (D-SL4-19).</item>
/// <item>One claim is handled by one transaction at a time (advisory lock), whichever handler delivers the event.</item>
/// <item>Applicability is asked per exposure from the registry (<c>ri.Contract.applicable</c>); per-risk, so the occurrence is the claim (D-SL4-04).</item>
/// </list>
/// </summary>
internal sealed class ClaimIntake(
    ReinsuranceDbContext db,
    ContractReader contracts,
    RecoveryBooker booker,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock)
{
    private const string Eur = "EUR";

    private Guid LegalEntityId => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;

    public Task OnClaimReportedAsync(EventEnvelope envelope, ClaimReportedV1 payload, CancellationToken cancellationToken) =>
        ApplyAsync(envelope, payload.ClaimId.Value, (view, _) =>
        {
            view.PolicyId = payload.PolicyId.Value;
            view.ProductCode = payload.ProductCode;
            view.LossDate = payload.LossDate;
            return Task.CompletedTask;
        }, cancellationToken);

    public Task OnExposureCreatedAsync(EventEnvelope envelope, ExposureCreatedV1 payload, CancellationToken cancellationToken) =>
        ApplyAsync(envelope, ClaimOf(envelope, null), async (view, claimId) =>
        {
            var exposure = await ExposureAsync(claimId, payload.ExposureId.Value, cancellationToken).ConfigureAwait(false);
            exposure.Kind = payload.Kind;
            exposure.CoverageCode = payload.CoverageCode;
            exposure.SiiLob = payload.SiiLob;
            view.LossDate ??= payload.AccidentDate;
        }, cancellationToken);

    public Task OnReserveChangedAsync(EventEnvelope envelope, ReserveChangedV1 payload, CancellationToken cancellationToken) =>
        ApplyAsync(envelope, ClaimOf(envelope, payload.ClaimId?.Value), async (view, claimId) =>
        {
            var open = await EurAsync(claimId, envelope, payload.NewOpenAmount.Functional, "RESERVE_CURRENCY", cancellationToken).ConfigureAwait(false);
            if (open is null)
            {
                return;
            }

            var line = await AmountAsync(claimId, payload.ExposureId.Value, payload.ReserveLine.CostType, cancellationToken).ConfigureAwait(false);
            if (payload.Kind == ReserveChangedV1.KindValue.RecoveryReserve)
            {
                // Anticipated recoveries are stored but do not inure (REQ-RI-117); older events never overwrite a newer open amount.
                if (envelope.AggregateSequence > line.RecoverySequence)
                {
                    line.RecoveryOpen = open.Value;
                    line.RecoverySequence = envelope.AggregateSequence;
                }
            }
            else if (envelope.AggregateSequence > line.ReserveSequence)
            {
                line.OpenReserve = open.Value;
                line.ReserveSequence = envelope.AggregateSequence;
            }
        }, cancellationToken);

    /// <summary>Method CLEARING counts as paid; a Friendly Settlement payable is claims cost like any other (REQ-RI-113).</summary>
    public Task OnPaymentIssuedAsync(EventEnvelope envelope, PaymentIssuedV1 payload, CancellationToken cancellationToken) =>
        ApplyAsync(envelope, ClaimOf(envelope, payload.ClaimId?.Value), (_, claimId) =>
            AddLinesAsync(claimId, envelope, payload.Lines, sign: 1, (row, amount) =>
            {
                row.Paid += amount;
                if (!payload.ExGratia)
                {
                    // An eroding payment reduces the open reserve (REQ-CLM-097); CLM sends no ReserveChanged for it, so incurred = paid + open stays right.
                    row.OpenReserve = Math.Max(0m, row.OpenReserve - amount);
                }
            }, cancellationToken), cancellationToken);

    public Task OnPaymentVoidedAsync(EventEnvelope envelope, PaymentVoidedV1 payload, CancellationToken cancellationToken) =>
        ApplyAsync(envelope, ClaimOf(envelope, payload.ClaimId?.Value), async (_, claimId) =>
        {
            if (payload.Lines is not { Count: > 0 } lines)
            {
                await LogAsync(claimId, null, envelope, "PAYMENT_VOID_WITHOUT_LINES", "A PaymentVoided without lines cannot be reversed.", cancellationToken).ConfigureAwait(false);
                return;
            }

            // TODO(hardening): PaymentVoided carries no ex-gratia flag; a voided ex-gratia payment would wrongly restore open reserve.
            await AddLinesAsync(claimId, envelope, lines, sign: -1, (row, amount) =>
            {
                row.Paid += amount;
                row.OpenReserve -= amount; // amount is negative: the reserve the payment had eroded comes back
            }, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task OnRecoveryRecordedAsync(EventEnvelope envelope, RecoveryRecordedV1 payload, CancellationToken cancellationToken) =>
        ApplyAsync(envelope, ClaimOf(envelope, payload.ClaimId?.Value), (_, claimId) =>
            AddLinesAsync(claimId, envelope, payload.AllocationByLine, sign: 1, (row, amount) => row.Realised += amount, cancellationToken), cancellationToken);

    /// <summary>A closed claim has no open reserve: the figures stay, the open parts go to zero (REQ-RI-122).</summary>
    public Task OnClaimClosedAsync(EventEnvelope envelope, CancellationToken cancellationToken) =>
        ApplyAsync(envelope, ClaimOf(envelope, null), async (view, claimId) =>
        {
            if (envelope.AggregateSequence <= view.ClosedSequence)
            {
                return;
            }

            view.Closed = true;
            view.ClosedSequence = envelope.AggregateSequence;
            foreach (var line in await db.ClaimAmounts.Where(a => a.ClaimId == claimId).ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                line.OpenReserve = 0m;
                line.RecoveryOpen = 0m;
            }
        }, cancellationToken);

    public Task OnClaimReopenedAsync(EventEnvelope envelope, CancellationToken cancellationToken) =>
        ApplyAsync(envelope, ClaimOf(envelope, null), (view, _) =>
        {
            if (envelope.AggregateSequence > view.ClosedSequence)
            {
                view.Closed = false;
                view.ClosedSequence = envelope.AggregateSequence;
            }

            return Task.CompletedTask;
        }, cancellationToken);

    private async Task ApplyAsync(EventEnvelope envelope, Guid claimId, Func<ClaimViewRow, Guid, Task> mutate, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", [new NpgsqlParameter("k", "ri:claim:" + claimId.ToString("D"))], cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        if (await db.ClaimEvents.AnyAsync(e => e.EventId == envelope.EventId.Value, cancellationToken).ConfigureAwait(false))
        {
            return; // Redelivery or replay of an applied event id: nothing changes.
        }

        db.ClaimEvents.Add(new ClaimEventRow { EventId = envelope.EventId.Value, ClaimId = claimId, AggregateSequence = envelope.AggregateSequence, AppliedAt = now });
        var view = await db.ClaimViews.SingleOrDefaultAsync(v => v.ClaimId == claimId, cancellationToken).ConfigureAwait(false);
        if (view is null)
        {
            view = new ClaimViewRow { ClaimId = claimId, LegalEntityId = LegalEntityId, UpdatedAt = now };
            db.ClaimViews.Add(view);
        }

        await mutate(view, claimId).ConfigureAwait(false);
        view.LastSequence = Math.Max(view.LastSequence, envelope.AggregateSequence);
        view.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var affected = await ResolveScopeAsync(view, envelope, cancellationToken).ConfigureAwait(false);
        foreach (var contractId in affected.Order())
        {
            await booker.RecalculateAsync(contractId, claimId, envelope.EventId.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Asks the registry which Active contract covers each exposure at the loss date (REQ-RI-115). No match: out of scope.
    /// Two matches: refused and logged, no order is invented. Returns every contract whose year must be recalculated (old and new).
    /// </summary>
    private async Task<HashSet<Guid>> ResolveScopeAsync(ClaimViewRow view, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var affected = new HashSet<Guid>();
        if (view.ProductCode is null || view.LossDate is not { } lossDate)
        {
            return affected;
        }

        var exposures = await db.ClaimExposures.Where(e => e.ClaimId == view.ClaimId).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var exposure in exposures.Where(e => e.CoverageCode is not null))
        {
            var found = await contracts.ApplicableAsync(view.ProductCode, exposure.CoverageCode!, ValidAt.From(lossDate), cancellationToken).ConfigureAwait(false);
            Guid? target = null;
            if (found.IsSuccess && found.Value.Contracts.Count == 1)
            {
                target = found.Value.Contracts[0].ContractId.Value;
            }
            else if (found.IsSuccess && found.Value.Contracts.Count > 1)
            {
                var numbers = string.Join(",", found.Value.Contracts.Select(c => c.ContractNumber).Order(StringComparer.Ordinal));
                var detail = $"Exposure {exposure.ExposureId:D} matches several contracts ({numbers}); no inuring order is defined, so it is not applied.";
                if (!await db.IntakeExceptions.AnyAsync(e => e.ClaimId == view.ClaimId && e.Code == "OVERLAPPING_CONTRACTS" && e.Detail == detail, cancellationToken).ConfigureAwait(false))
                {
                    await LogAsync(view.ClaimId, null, envelope, "OVERLAPPING_CONTRACTS", detail, cancellationToken).ConfigureAwait(false);
                }
            }

            if (exposure.ContractId != target)
            {
                if (exposure.ContractId is { } old)
                {
                    affected.Add(old);
                }

                exposure.ContractId = target;
            }

            if (target is { } current)
            {
                affected.Add(current);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return affected;
    }

    private async Task AddLinesAsync(
        Guid claimId, EventEnvelope envelope, IReadOnlyList<ClaimPaymentLine> lines, int sign, Action<ClaimAmountRow, decimal> apply, CancellationToken cancellationToken)
    {
        foreach (var line in lines)
        {
            if (line.ExposureId is not { } exposureId || string.IsNullOrEmpty(line.CostType))
            {
                await LogAsync(claimId, null, envelope, "LINE_WITHOUT_EXPOSURE", "A financial line without exposure or cost type cannot be attributed.", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var amount = await EurAsync(claimId, envelope, line.Amount.Functional, "LINE_CURRENCY", cancellationToken).ConfigureAwait(false);
            if (amount is null)
            {
                continue;
            }

            apply(await AmountAsync(claimId, exposureId.Value, line.CostType, cancellationToken).ConfigureAwait(false), sign * amount.Value);
        }
    }

    private async Task<decimal?> EurAsync(Guid claimId, EventEnvelope envelope, Money money, string code, CancellationToken cancellationToken)
    {
        if (money.Currency.Code == Eur)
        {
            return money.Amount;
        }

        await LogAsync(claimId, null, envelope, code, $"Only EUR is handled in slice 4 (D-SL4-04); got {money.Currency.Code}.", cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async Task<ClaimExposureRow> ExposureAsync(Guid claimId, Guid exposureId, CancellationToken cancellationToken)
    {
        var row = await db.ClaimExposures.FindAsync([claimId, exposureId], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new ClaimExposureRow { ClaimId = claimId, ExposureId = exposureId };
            db.ClaimExposures.Add(row);
        }

        return row;
    }

    private async Task<ClaimAmountRow> AmountAsync(Guid claimId, Guid exposureId, string costType, CancellationToken cancellationToken)
    {
        await ExposureAsync(claimId, exposureId, cancellationToken).ConfigureAwait(false);
        var row = await db.ClaimAmounts.FindAsync([claimId, exposureId, costType], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new ClaimAmountRow { ClaimId = claimId, ExposureId = exposureId, CostType = costType };
            db.ClaimAmounts.Add(row);
        }

        return row;
    }

    private async Task LogAsync(Guid claimId, Guid? contractId, EventEnvelope envelope, string code, string detail, CancellationToken cancellationToken)
    {
        db.IntakeExceptions.Add(new IntakeExceptionRow
        {
            ExceptionId = Guid.CreateVersion7(), LegalEntityId = LegalEntityId, ClaimId = claimId, ContractId = contractId, Code = code, Detail = detail,
            SourceEventId = envelope.EventId.Value, RecordedAt = clock.Now,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Guid ClaimOf(EventEnvelope envelope, Guid? fromPayload) =>
        fromPayload ?? (Guid.TryParse(envelope.AggregateId, out var id) ? id : throw new InvalidOperationException("The CLM event has no claim id (aggregate id)."));
}
