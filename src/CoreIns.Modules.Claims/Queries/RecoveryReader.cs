using System.Text.Json;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.Queries;

/// <summary>Recovery case read model (SL4-CLM-RECOVERY-OPS): balances are derived from approved transactions, never stored.</summary>
internal sealed class RecoveryReader(ClaimsDbContext db)
{
    /// <summary>Reserved milestone code that carries a user-specified allocation (JSON in the note); hidden from views.</summary>
    public const string AllocationMilestone = "ALLOCATION";

    private static readonly string[] Effective = [Codes.Of(SetStatus.Approved), Codes.Of(SetStatus.Posted)];

    /// <summary>Σ approved recovery reserves and Σ approved recoveries per recovery.</summary>
    public async Task<Dictionary<RecoveryId, (decimal Reserved, decimal Recovered)>> TotalsAsync(IReadOnlyCollection<RecoveryId> ids, CancellationToken cancellationToken)
    {
        var list = ids.ToList();
        var rows = await (
                from t in db.FinancialTransactions.AsNoTracking()
                join s in db.TransactionSets.AsNoTracking() on t.SetId equals s.SetId
                where t.RecoveryId != null && list.Contains(t.RecoveryId.Value) && Effective.Contains(s.Status)
                select new { RecoveryId = t.RecoveryId!.Value, t.Kind, t.Amount })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var reserve = Codes.Of(TransactionKind.RecoveryReserve);
        var recovery = Codes.Of(TransactionKind.Recovery);
        return rows.GroupBy(r => r.RecoveryId).ToDictionary(
            g => g.Key, g => (g.Where(r => r.Kind == reserve).Sum(r => r.Amount), g.Where(r => r.Kind == recovery).Sum(r => r.Amount)));
    }

    public async Task<RecoveryView> ViewAsync(RecoveryRow row, CancellationToken cancellationToken) =>
        (await ViewsAsync([row], cancellationToken).ConfigureAwait(false))[0];

    public async Task<List<RecoveryView>> ViewsAsync(IReadOnlyList<RecoveryRow> rows, CancellationToken cancellationToken)
    {
        var totals = await TotalsAsync([.. rows.Select(r => r.RecoveryId)], cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => View(r, totals.GetValueOrDefault(r.RecoveryId)))];
    }

    public async Task<RecoveryListPage> ListAsync(LegalEntityId legalEntity, ClaimId claimId, string? state, string? type, Guid? after, int take, CancellationToken cancellationToken)
    {
        var query = db.Recoveries.AsNoTracking().Where(r => r.LegalEntityId == legalEntity && r.ClaimId == claimId);
        if (state is not null)
        {
            query = query.Where(r => r.Status == state);
        }

        if (type is not null)
        {
            query = query.Where(r => r.Type == type);
        }

        if (after is { } last)
        {
            var id = new RecoveryId(last);
            query = query.Where(r => r.RecoveryId.Value.CompareTo(id.Value) > 0);
        }

        var rows = await query.OrderBy(r => r.RecoveryId).Take(take + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(take).ToList();
        return new RecoveryListPage
        {
            Items = await ViewsAsync(page, cancellationToken).ConfigureAwait(false),
            NextCursor = rows.Count > take ? page[^1].RecoveryId.Value.ToString("D") : null,
            Limit = take,
        };
    }

    public Task<RecoveryRow?> GetRowAsync(LegalEntityId legalEntity, RecoveryId id, CancellationToken cancellationToken) =>
        db.Recoveries.AsNoTracking().SingleOrDefaultAsync(r => r.LegalEntityId == legalEntity && r.RecoveryId == id, cancellationToken);

    public static List<RecoveryMilestoneView> ReadMilestones(RecoveryRow row) =>
        row.Milestones.Deserialize<List<RecoveryMilestoneView>>(SharedKernelJson.Options) ?? [];

    public static RecoveryView View(RecoveryRow r, (decimal Reserved, decimal Recovered) totals)
    {
        var all = ReadMilestones(r);
        var sold = all.LastOrDefault(m => m.Milestone == "SALVAGE_SOLD");
        return new RecoveryView
        {
            RecoveryId = r.RecoveryId,
            ClaimId = r.ClaimId,
            ExposureId = r.ExposureId,
            Type = Codes.Parse<RecoveryType>(r.Type),
            State = Codes.Parse<RecoveryState>(r.Status),
            CounterpartyPartyId = r.CounterpartyPartyId,
            ExpectedAmount = ClaimMoney.Of(r.ExpectedAmount, r.Currency),
            RecoveryReserve = ClaimMoney.Of(Math.Max(0m, totals.Reserved - totals.Recovered), r.Currency),
            RecoveredAmount = ClaimMoney.Of(totals.Recovered, r.Currency),
            AllocationRule = r.AllocationRule == "SPECIFIED" ? RecoveryView.AllocationRuleValue.Specified : RecoveryView.AllocationRuleValue.ProRataPaid,
            Salvage = r.Type == "SALVAGE"
                ? new SalvageDetails
                {
                    Estimate = r.SalvageEstimate is { } e ? ClaimMoney.Of(e, r.Currency) : null,
                    BuyerPartyId = r.BuyerPartyId?.Value,
                    SalePrice = r.SalePrice is { } p ? ClaimMoney.Of(p, r.Currency) : null,
                    SoldOn = sold?.OccurredOn,
                }
                : null,
            Milestones = [.. all.Where(m => m.Milestone != AllocationMilestone)],
            ReceivableId = r.ReceivableId,
            PaymentReference = r.PaymentReference,
            FsCaseId = r.FsCaseId is { } fs ? new FsCaseId(fs) : null,
            FsStatementId = r.FsStatementId,
            DemandedOn = r.DemandedOn,
            CreatedAt = r.CreatedAt,
            RecordVersion = r.RecordVersion,
        };
    }
}
