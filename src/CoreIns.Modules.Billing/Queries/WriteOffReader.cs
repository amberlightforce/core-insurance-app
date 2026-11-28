using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Queries;

/// <summary><c>bil.WriteOff.list</c> (BO-BIL-13): write-offs of the legal entity, newest first (the inbox of the maker-checker flow).</summary>
internal sealed class WriteOffReader(BillingDbContext db)
{
    public async Task<WriteOffListResponse> ListAsync(LegalEntityId legalEntity, string? state, BillingAccountId? account, int limit, CancellationToken cancellationToken)
    {
        var query = db.WriteOffs.AsNoTracking().Where(w => w.LegalEntityId == legalEntity);
        if (state is not null)
        {
            query = query.Where(w => w.State == state);
        }

        if (account is { } a)
        {
            query = query.Where(w => w.BillingAccountId == a);
        }

        // TODO(hardening): keyset paging; the inbox is small (proposals wait for a decision), so a limit is enough for now.
        var rows = await query.OrderByDescending(w => w.ProposedAt).ThenByDescending(w => w.WriteOffId).Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var invoiceIds = rows.Select(w => w.InvoiceId).Distinct().ToList();
        var numbers = await db.Invoices.AsNoTracking().Where(i => invoiceIds.Contains(i.InvoiceId)).ToDictionaryAsync(i => i.InvoiceId, i => i.InvoiceNumber, cancellationToken).ConfigureAwait(false);
        return new WriteOffListResponse { Items = [.. rows.Select(w => WriteOffViews.View(w, numbers.GetValueOrDefault(w.InvoiceId)))] };
    }
}
