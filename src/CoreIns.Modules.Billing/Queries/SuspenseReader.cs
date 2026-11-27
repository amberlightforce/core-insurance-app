using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Queries;

/// <summary>Read side of the suspense workbench (BO-BIL-08): <c>bil.Suspense.list</c> and the item views the commands return.</summary>
internal sealed class SuspenseReader(BillingDbContext db)
{
    /// <summary>Items of the legal entity, newest last (UUIDv7 ids are creation order and a stable cursor); state defaults to OPEN.</summary>
    public async Task<SuspenseListPage> ListAsync(
        LegalEntityId legalEntity, Guid? billingAccountId, SuspenseState? state, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(limit ?? 50, 1, 200);
        var code = Codes.Of(state ?? SuspenseState.Open);
        var query = db.SuspenseItems.AsNoTracking().Where(i => i.LegalEntityId == legalEntity && i.State == code);
        if (billingAccountId is { } a)
        {
            var account = new BillingAccountId(a);
            query = query.Where(i => i.BillingAccountId == account);
        }

        // Ids are UUIDv7; the comparison runs in memory like the other ops lists (the id value converter is not order-translatable).
        var rows = (await query.ToListAsync(cancellationToken).ConfigureAwait(false)).OrderBy(i => i.SuspenseItemId).ToList();
        if (Guid.TryParse(cursor, out var after))
        {
            rows = [.. rows.Where(i => i.SuspenseItemId.CompareTo(after) > 0)];
        }

        var window = rows.Take(size + 1).ToList();
        var page = window.Take(size).ToList();
        return new SuspenseListPage
        {
            Items = await ViewsAsync(page, cancellationToken).ConfigureAwait(false),
            NextCursor = window.Count > size ? page[^1].SuspenseItemId.ToString("D") : null,
            Limit = size,
        };
    }

    /// <summary>The views of already loaded rows (also used inside a command, after its changes).</summary>
    public async Task<IReadOnlyList<SuspenseListItem>> ViewsAsync(IReadOnlyList<SuspenseItemRow> rows, CancellationToken cancellationToken)
    {
        var receiptIds = rows.Select(r => r.ReceiptId).ToList();
        var receipts = await db.Receipts.AsNoTracking().Where(r => receiptIds.Contains(r.ReceiptId))
            .ToDictionaryAsync(r => r.ReceiptId, cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => View(r, receipts[r.ReceiptId]))];
    }

    public async Task<SuspenseListItem> ViewAsync(SuspenseItemRow row, CancellationToken cancellationToken) =>
        (await ViewsAsync([row], cancellationToken).ConfigureAwait(false))[0];

    private static SuspenseListItem View(SuspenseItemRow item, ReceiptRow receipt) => new()
    {
        SuspenseItemId = item.SuspenseItemId,
        ReceiptId = item.ReceiptId,
        ReceiptNumber = ReceiptNumber.Parse(receipt.ReceiptNumber),
        BillingAccountId = item.BillingAccountId,
        State = Codes.Parse<SuspenseState>(item.State),
        Amount = new Money(item.Amount, Currency.FromCode(item.Currency)),
        Reason = item.Reason,
        BankReference = receipt.BankReference,
        ValueDate = receipt.ValueDate,
        RefundId = item.RefundId,
        RecordedAt = item.CreatedAt,
        ResolvedAt = item.ResolvedAt,
    };
}
