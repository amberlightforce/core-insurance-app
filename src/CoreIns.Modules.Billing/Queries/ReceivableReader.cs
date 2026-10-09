using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Queries;

internal sealed class ReceivableReader(BillingDbContext db)
{
    public async Task<ReceivableView?> GetAsync(LegalEntityId entity, Guid id, CancellationToken ct)
    {
        var row = await db.Receivables.AsNoTracking().SingleOrDefaultAsync(r => r.LegalEntityId == entity && r.ReceivableId == id, ct).ConfigureAwait(false);
        return row is null ? null : await ViewAsync(row, ct).ConfigureAwait(false);
    }

    public async Task<ReceivableView> ViewAsync(ReceivableRow row, CancellationToken ct)
    {
        var paid = await db.ReceivableAllocations.Where(a => a.ReceivableId == row.ReceivableId).SumAsync(a => a.Amount, ct).ConfigureAwait(false);
        return View(row, paid);
    }

    public static ReceivableView View(ReceivableRow row, decimal paid) => new()
    {
        ReceivableId = row.ReceivableId,
        BillingAccountId = row.BillingAccountId,
        SourceType = row.SourceType == "FS_CLEARING" ? ReceivableSourceType.FsClearing : ReceivableSourceType.ClmClaimPayment,
        SourceId = row.SourceId,
        Purpose = Enum.Parse<ReceivablePurpose>(row.Purpose switch { "FS_NET" => "FsNet", "SALVAGE" => "Salvage", _ => "Subrogation" }),
        CounterpartyPartyId = row.CounterpartyPartyId,
        ClaimId = row.ClaimId,
        RecoveryId = row.RecoveryId,
        StatementRef = row.StatementRef,
        Amount = new Money(row.Amount, Currency.FromCode(row.Currency)),
        OpenAmount = new Money(row.Amount - paid, Currency.FromCode(row.Currency)),
        Status = paid == row.Amount ? ReceivableStatus.Paid : paid > 0 ? ReceivableStatus.PartiallyPaid : ReceivableStatus.Open,
        DueDate = row.DueDate,
        PaymentReference = row.PaymentReference,
        RegisteredAt = row.RegisteredAt,
        RecordVersion = row.RecordVersion,
    };

    public async Task<ReceivableListPage> ListAsync(LegalEntityId entity, BillingAccountId? account, PartyId? counterparty, ClaimId? claim, Guid? recovery, string? statement, string? reference, ReceivableSourceType? source, ReceivableStatus? status, string? cursor, int? limit, CancellationToken ct)
    {
        var query = db.Receivables.AsNoTracking().Where(r => r.LegalEntityId == entity);
        if (account is { } a) query = query.Where(r => r.BillingAccountId == a);
        if (counterparty is { } p) query = query.Where(r => r.CounterpartyPartyId == p);
        if (claim is { } c) query = query.Where(r => r.ClaimId == c);
        if (recovery is { } id) query = query.Where(r => r.RecoveryId == id);
        if (statement is not null) query = query.Where(r => r.StatementRef == statement);
        if (reference is not null) query = query.Where(r => r.PaymentReference == reference);
        if (source is { } s)
        {
            var code = s == ReceivableSourceType.FsClearing ? "FS_CLEARING" : "CLM_CLAIM_PAYMENT";
            query = query.Where(r => r.SourceType == code);
        }
        if (status is { } st)
        {
            query = st switch
            {
                ReceivableStatus.Open => query.Where(r => !db.ReceivableAllocations.Any(x => x.ReceivableId == r.ReceivableId)),
                ReceivableStatus.Paid => query.Where(r => db.ReceivableAllocations.Where(x => x.ReceivableId == r.ReceivableId).Sum(x => x.Amount) == r.Amount),
                ReceivableStatus.PartiallyPaid => query.Where(r => db.ReceivableAllocations.Where(x => x.ReceivableId == r.ReceivableId).Sum(x => x.Amount) > 0 && db.ReceivableAllocations.Where(x => x.ReceivableId == r.ReceivableId).Sum(x => x.Amount) < r.Amount),
                _ => query.Where(r => false),
            };
        }
        if (cursor is not null)
        {
            if (!Guid.TryParse(cursor, out var after)) throw new ArgumentException("Invalid receivable cursor.", nameof(cursor));
            query = query.Where(r => r.ReceivableId.CompareTo(after) > 0);
        }
        var size = Math.Clamp(limit ?? 50, 1, 200);
        var rows = await query.OrderBy(r => r.ReceivableId).Take(size + 1).ToListAsync(ct).ConfigureAwait(false);
        var views = new List<ReceivableView>();
        foreach (var row in rows.Take(size)) views.Add(await ViewAsync(row, ct).ConfigureAwait(false));
        return new ReceivableListPage { Items = views, NextCursor = rows.Count > size ? views[^1].ReceivableId.ToString("D") : null, Limit = size };
    }
}

