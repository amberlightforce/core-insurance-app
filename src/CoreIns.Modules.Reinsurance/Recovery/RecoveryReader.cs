using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Paging;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Reinsurance.Recovery;

/// <summary>One recoverable position: the sum of the sealed rows of a contract x claim x layer x participant as known at a record time.</summary>
internal sealed record RecoverablePosition(
    Guid ContractId, int ContractYear, Guid ClaimId, Guid LayerId, int LayerNo, Guid ParticipantPartyId,
    decimal Incurred, decimal Paid, Guid LastRecoveryId, Guid LastBatchId, string State)
{
    public decimal Outstanding => Incurred - Paid;
}

/// <summary>
/// <c>ri.Recovery.listByClaim / listByContract / trace</c> (REQ-RI-136). The answer "as known at <c>knownAt</c>" is the sum of
/// the append-only rows recorded at or before it, so an earlier question gets the earlier answer. A future <c>knownAt</c> is refused (PITFALLS 13).
/// </summary>
internal sealed class RecoveryReader(ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities)
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;
    private static readonly Currency EurCurrency = Currency.FromCode("EUR");

    private Guid LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;

    public async Task<Result<(IReadOnlyList<RecoverablePosition> Items, string? Next, int Limit)>> ByClaimAsync(
        Guid claimId, Instant? knownAt, string? cursor, int? limit, CancellationToken cancellationToken) =>
        await PageAsync(r => r.ClaimId == claimId, knownAt, cursor, limit, cancellationToken).ConfigureAwait(false);

    public async Task<Result<(IReadOnlyList<RecoverablePosition> Items, string? Next, int Limit)>> ByContractAsync(
        Guid contractId, Guid? claimId, Instant? knownAt, string? cursor, int? limit, CancellationToken cancellationToken) =>
        await PageAsync(r => r.ContractId == contractId && (claimId == null || r.ClaimId == claimId), knownAt, cursor, limit, cancellationToken).ConfigureAwait(false);

    /// <summary><c>ri.Recovery.trace</c>: the batch's delta rows and the stored calculation trace; null when the batch is not in the caller's legal entity.</summary>
    public async Task<RecoveryTraceResponse?> TraceAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var batch = await db.RecoveryBatches.AsNoTracking().SingleOrDefaultAsync(b => b.BatchId == batchId && b.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            return null;
        }

        var rows = await db.Recoveries.AsNoTracking().Where(r => r.BatchId == batchId).OrderBy(r => r.ClaimId).ThenBy(r => r.LayerNo).ThenBy(r => r.ParticipantPartyId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var recoverables = rows.Select(r => ToClaimItem(new RecoverablePosition(
            r.ContractId, r.ContractYear, r.ClaimId, r.LayerId, r.LayerNo, r.ParticipantPartyId, r.IncurredDelta, r.PaidDelta, r.RecoveryId, r.BatchId, r.State))).ToList();

        var steps = new List<RecoveryTraceStep>();
        using (var document = JsonDocument.Parse(batch.Trace))
        {
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var claim = Guid.TryParse(entry.GetProperty("occurrenceId").GetString(), out var id) ? new ClaimId(id) : (ClaimId?)null;
                steps.Add(Step(steps, "UNL_INCURRED", claim, entry, Dec(entry, "unlIncurred", "unl"), "ultimate net loss, incurred basis"));
                steps.Add(Step(steps, "UNL_PAID", claim, entry, Dec(entry, "unlPaid", "unl"), "ultimate net loss, paid basis"));
                steps.Add(Step(steps, "LAYER_LOSS", claim, entry, entry.GetProperty("layerLossIncurred").GetDecimal(), "layer loss (incurred)"));
                steps.Add(Step(steps, "AGGREGATE", claim, entry, entry.GetProperty("recoverableIncurred").GetDecimal(), "recoverable after AAD / AAL (incurred)"));
                steps.Add(Step(steps, "ROUNDING", claim, entry, entry.GetProperty("placedIncurred").GetDecimal(), "placed share after rounding (incurred)"));
            }
        }

        foreach (var row in rows)
        {
            steps.Add(new RecoveryTraceStep
            {
                StepNo = steps.Count + 1, Kind = "DELTA", ClaimId = new ClaimId(row.ClaimId), LayerNo = row.LayerNo, ParticipantId = row.ParticipantPartyId,
                Amount = new Money(row.IncurredDelta, EurCurrency), Note = $"paid {row.PaidDelta.ToString(CultureInfo.InvariantCulture)}",
            });
        }

        return new RecoveryTraceResponse
        {
            BatchId = batchId, ContractId = new RiContractId(batch.ContractId), ContractYear = batch.ContractYear, EngineVersion = batch.EngineVersion,
            Recoverables = recoverables, Trace = steps,
        };
    }

    public static RecoveryListByClaimItem ToClaimItem(RecoverablePosition p) => new()
    {
        ContractId = new RiContractId(p.ContractId), LayerId = new RiLayerId(p.LayerId), ParticipantId = p.ParticipantPartyId,
        RecoverableIncurred = Eur(p.Incurred), RecoverablePaid = Eur(p.Paid), RecoverableOutstanding = Eur(p.Outstanding),
        ClaimId = new ClaimId(p.ClaimId), ContractYear = p.ContractYear, LayerNo = p.LayerNo, RecoveryId = p.LastRecoveryId, BatchId = p.LastBatchId,
        State = Enum.Parse<RecoveryListByClaimItem.StateValue>(p.State, ignoreCase: true),
    };

    public static RecoveryListByContractItem ToContractItem(RecoverablePosition p) => new()
    {
        ContractId = new RiContractId(p.ContractId), LayerId = new RiLayerId(p.LayerId), ParticipantId = p.ParticipantPartyId,
        RecoverableIncurred = Eur(p.Incurred), RecoverablePaid = Eur(p.Paid), RecoverableOutstanding = Eur(p.Outstanding),
        ClaimId = new ClaimId(p.ClaimId), ContractYear = p.ContractYear, LayerNo = p.LayerNo, RecoveryId = p.LastRecoveryId, BatchId = p.LastBatchId,
        State = Enum.Parse<RecoveryListByContractItem.StateValue>(p.State, ignoreCase: true),
    };

    private async Task<Result<(IReadOnlyList<RecoverablePosition> Items, string? Next, int Limit)>> PageAsync(
        System.Linq.Expressions.Expression<Func<RecoveryRow, bool>> filter, Instant? knownAt, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (knownAt is { } at && at > clock.Now)
        {
            return RiErrors.Validation("knownAt", "KNOWN_AT_FUTURE", "knownAt must not be in the future.");
        }

        var size = limit ?? DefaultLimit;
        if (size is < 1 or > MaxLimit)
        {
            return RiErrors.Validation("limit", "LIMIT_RANGE", $"The limit is between 1 and {MaxLimit}.");
        }

        string[]? after = null;
        if (!KeysetPaging.TryDecode(cursor, 4, out after)
            || (after is not null && !(Guid.TryParse(after[0], out _) && Guid.TryParse(after[1], out _) && int.TryParse(after[2], NumberStyles.None, CultureInfo.InvariantCulture, out _) && Guid.TryParse(after[3], out _))))
        {
            return RiErrors.Validation("cursor", "CURSOR", "The cursor is not valid.");
        }

        var legalEntity = LegalEntity;
        var query = db.Recoveries.AsNoTracking().Where(r => r.LegalEntityId == legalEntity).Where(filter);
        if (knownAt is { } cutoff)
        {
            query = query.Where(r => r.RecordedAt <= cutoff);
        }

        // TODO(hardening): aggregate in SQL once the row volume warrants it; the sum is small per claim or contract today.
        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        var positions = rows
            .GroupBy(r => (r.ContractId, r.ClaimId, r.LayerId, r.ParticipantPartyId))
            .Select(g =>
            {
                var last = g.OrderBy(r => r.RecordedAt).ThenBy(r => r.RecoveryId).Last();
                return new RecoverablePosition(
                    g.Key.ContractId, last.ContractYear, g.Key.ClaimId, g.Key.LayerId, last.LayerNo, g.Key.ParticipantPartyId,
                    g.Sum(r => r.IncurredDelta), g.Sum(r => r.PaidDelta), last.RecoveryId, last.BatchId, last.State);
            })
            .OrderBy(p => p.ContractId).ThenBy(p => p.ClaimId).ThenBy(p => p.LayerNo).ThenBy(p => p.ParticipantPartyId)
            .AsEnumerable();
        if (after is not null)
        {
            var key = (Contract: Guid.Parse(after[0]), Claim: Guid.Parse(after[1]), Layer: int.Parse(after[2], CultureInfo.InvariantCulture), Participant: Guid.Parse(after[3]));
            positions = positions.Where(p => CompareKey(p, key) > 0);
        }

        // TODO(hardening): the positions are aggregated in memory (above), so the keyset applies after the grouping; push both into SQL with the sum.
        var (page, next) = KeysetPaging.Slice(positions.Take(size + 1).ToList(), size, p => KeysetPaging.Encode(
            KeysetPaging.Part(p.ContractId), KeysetPaging.Part(p.ClaimId), p.LayerNo.ToString(CultureInfo.InvariantCulture), KeysetPaging.Part(p.ParticipantPartyId)));
        return (page, next, size);
    }

    private static int CompareKey(RecoverablePosition p, (Guid Contract, Guid Claim, int Layer, Guid Participant) key)
    {
        var c = p.ContractId.CompareTo(key.Contract);
        c = c != 0 ? c : p.ClaimId.CompareTo(key.Claim);
        c = c != 0 ? c : p.LayerNo.CompareTo(key.Layer);
        return c != 0 ? c : p.ParticipantPartyId.CompareTo(key.Participant);
    }

    private static Money Eur(decimal amount) => new(ContractSupport.Amount(amount), EurCurrency);

    private static decimal Dec(JsonElement entry, string objectName, string property) =>
        entry.TryGetProperty(objectName, out var o) && o.ValueKind == JsonValueKind.Object ? o.GetProperty(property).GetDecimal() : 0m;

    private static RecoveryTraceStep Step(List<RecoveryTraceStep> steps, string kind, ClaimId? claim, JsonElement entry, decimal amount, string note) => new()
    {
        StepNo = steps.Count + 1, Kind = kind, ClaimId = claim, LayerNo = null, Amount = new Money(amount, EurCurrency), Note = $"{note}; layer {entry.GetProperty("layerId").GetString()}",
    };
}
