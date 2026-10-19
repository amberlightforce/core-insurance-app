using System.Text.Json;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Claims.Queries;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using Microsoft.EntityFrameworkCore;
using Item = CoreIns.Modules.Claims.Contracts.Api.TransactionSetBuildRequest.TransactionItem;

namespace CoreIns.Modules.Claims.Services;

/// <summary>
/// Cash evidence handed over by the BIL <c>CashAllocated</c> consumer within the same scope: the amount comes from BIL's event,
/// never from a client (PITFALLS 7). The reference is the idempotency key of the system set.
/// </summary>
internal sealed class RecoveryCashBuffer
{
    private readonly Dictionary<string, (RecoveryId RecoveryId, decimal Amount)> entries = new(StringComparer.Ordinal);

    public void Put(string reference, RecoveryId recoveryId, decimal amount) => entries[reference] = (recoveryId, amount);

    public bool TryGet(string reference, out (RecoveryId RecoveryId, decimal Amount) entry) => entries.TryGetValue(reference, out entry);
}

/// <summary>
/// Resolves <c>BIL_ALLOCATION</c> evidence into RECOVERY lines (REQ-CLM-151, BR-CLM-039): default pro rata to the amount paid
/// per reserve line, or the allocation specified at demand (scaled to the cash received). Any other evidence kind is not ours.
/// </summary>
internal sealed class RecoveryEvidenceSource(ClaimsDbContext db, RecoveryCashBuffer buffer, FinancialsReader financials, Microsoft.Extensions.Options.IOptions<ClaimsOptions> options) : IClaimFinancialEvidenceProvider
{
    public async Task<ResolvedClaimFinancialEvidence?> ResolveAsync(ClaimId claimId, ClaimFinancialEvidence evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Kind != "BIL_ALLOCATION" || !buffer.TryGet(evidence.Reference, out var cash) || cash.Amount <= 0m)
        {
            return null;
        }

        var recovery = await db.Recoveries.AsNoTracking().SingleOrDefaultAsync(r => r.RecoveryId == cash.RecoveryId && r.ClaimId == claimId, cancellationToken).ConfigureAwait(false);
        if (recovery is null || recovery.ReceivableId is null)
        {
            return null;
        }

        var shares = await ShareAsync(claimId, recovery, cash.Amount, cancellationToken).ConfigureAwait(false);
        if (shares.Count == 0)
        {
            return null;
        }

        return new ResolvedClaimFinancialEvidence(new TransactionSetBuildRequest
        {
            ClaimId = claimId,
            Transactions = [.. shares.Select(s => new Item
            {
                Kind = Item.KindValue.Recovery, ExposureId = s.Exposure, CostType = s.CostType, CostCategory = s.CostCategory,
                Amount = ClaimMoney.Eur(s.Amount), RecoveryId = recovery.RecoveryId,
            })],
        });
    }

    private sealed record Share(ExposureId Exposure, string CostType, string CostCategory, decimal Amount);

    private async Task<List<Share>> ShareAsync(ClaimId claimId, RecoveryRow recovery, decimal cash, CancellationToken cancellationToken)
    {
        List<(ExposureId Exposure, string CostType, string CostCategory, decimal Weight)> weights = [];
        if (recovery.AllocationRule == "SPECIFIED")
        {
            var note = RecoveryReader.ReadMilestones(recovery).LastOrDefault(m => m.Milestone == RecoveryReader.AllocationMilestone)?.Note;
            var lines = note is null ? null : JsonSerializer.Deserialize<List<RecoveryAllocationLine>>(note, SharedKernelJson.Options);
            weights = [.. (lines ?? []).Select(l => (l.ExposureId, l.CostType, l.CostCategory, l.Amount.Amount))];
        }
        else
        {
            var lines = (await financials.LinesAsync(claimId, cancellationToken).ConfigureAwait(false))
                .Where(l => recovery.ExposureId is null || l.ExposureId == recovery.ExposureId).ToList();
            var approved = await financials.ApprovedAsync(claimId, null, cancellationToken).ConfigureAwait(false);
            weights = [.. lines.Select(l => (l.ExposureId, l.CostType, l.CostCategory, Weight: approved.GetValueOrDefault(l.ReserveLineId).Paid)).Where(w => w.Weight > 0m)];
            if (weights.Count == 0)
            {
                // Nothing paid yet (e.g. salvage): follow the recovery reserve, then the first line of the case.
                weights = [.. lines.Select(l => (l.ExposureId, l.CostType, l.CostCategory, Weight: approved.GetValueOrDefault(l.ReserveLineId).RecoveryReserved)).Where(w => w.Weight > 0m)];
            }

            if (weights.Count == 0 && lines.FirstOrDefault() is { } first)
            {
                weights = [(first.ExposureId, first.CostType, first.CostCategory, 1m)];
            }

            if (weights.Count == 0)
            {
                // No line yet (nothing reserved or paid): the case exposure (or the claim first one) on the first configured indemnity category.
                var configured = options.Value.Financials.CostCategories.Count > 0 ? options.Value.Financials.CostCategories : ClaimFinancialsOptions.DefaultCostCategories;
                var exposure = recovery.ExposureId ?? await db.Exposures.AsNoTracking().Where(e => e.ClaimId == claimId).OrderBy(e => e.Sequence)
                    .Select(e => (ExposureId?)e.ExposureId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (exposure is { } fallback && configured.TryGetValue("INDEMNITY", out var categories) && categories.Length > 0)
                {
                    weights = [(fallback, "INDEMNITY", categories[0], 1m)];
                }
            }
        }

        weights = [.. weights.Where(w => w.Weight > 0m).Take(20)];
        var sum = weights.Sum(w => w.Weight);
        if (sum <= 0m)
        {
            return [];
        }

        var result = new List<Share>();
        var allocated = 0m;
        for (var i = 0; i < weights.Count; i++)
        {
            var amount = i == weights.Count - 1 ? cash - allocated : decimal.Round(cash * weights[i].Weight / sum, 2, MidpointRounding.ToEven);
            allocated += amount;
            if (amount > 0m)
            {
                result.Add(new Share(weights[i].Exposure, weights[i].CostType, weights[i].CostCategory, amount));
            }
        }

        return result;
    }
}
