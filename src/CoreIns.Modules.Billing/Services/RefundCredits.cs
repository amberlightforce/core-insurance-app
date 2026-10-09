using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Services;

/// <summary>
/// The credit balance of a billing account and the debit items it can be netted against (REQ-BIL-181, REQ-BIL-182). The
/// remaining credit of a credit item is the item less every credit application against it (<c>remaining = item − Σ
/// applications</c>: the original invoice's offset at credit time, then refunds and netting); open debit is an invoice item
/// less its cash allocations and the credit offset against it (<see cref="Allocator.OpenByItemAsync"/>).
/// </summary>
internal sealed class RefundCredits(BillingDbContext db, Allocator allocator)
{
    private static readonly string[] OpenInvoiceStates =
    [
        Codes.Of(InvoiceState.Billed), Codes.Of(InvoiceState.Due), Codes.Of(InvoiceState.PartiallyPaid), Codes.Of(InvoiceState.Overdue),
    ];

    /// <summary>The credit items of the account's credit notes with credit left; <paramref name="creditNotes"/> restricts them to the chosen notes.</summary>
    public async Task<List<CreditSource>> RemainingAsync(
        LegalEntityId legalEntity, BillingAccountId account, IReadOnlyCollection<Guid>? creditNotes, CancellationToken cancellationToken)
    {
        var notes = await db.Invoices.AsNoTracking()
            .Where(i => i.LegalEntityId == legalEntity && i.BillingAccountId == account && i.Kind == "CREDIT_NOTE")
            .Select(i => new { i.InvoiceId, i.PolicyId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (creditNotes is { Count: > 0 })
        {
            notes = [.. notes.Where(n => creditNotes.Contains(n.InvoiceId.Value))];
        }

        var noteIds = notes.Select(n => n.InvoiceId).ToList();
        var items = await db.InvoiceItems.AsNoTracking().Where(i => noteIds.Contains(i.InvoiceId) && i.CreditsItemId != null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var itemIds = items.Select(i => i.InvoiceItemId).ToList();
        var applied = await db.CreditApplications.AsNoTracking().Where(a => itemIds.Contains(a.CreditItemId))
            .GroupBy(a => a.CreditItemId).Select(g => new { Item = g.Key, Sum = g.Sum(a => a.Amount) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var used = applied.ToDictionary(a => a.Item, a => a.Sum);
        var policyOf = notes.ToDictionary(n => n.InvoiceId, n => n.PolicyId);
        return
        [
            .. items
                .Select(i => new CreditSource(
                    i.InvoiceItemId, i.InvoiceId, policyOf[i.InvoiceId], i.TermId, i.TransactionId, i.ChargeType, i.ChargeCategory,
                    i.Amount - used.GetValueOrDefault(i.InvoiceItemId)))
                .Where(c => c.Remaining > 0m),
        ];
    }

    /// <summary>The open items of the account's invoices (credit notes excluded) with their open amount.</summary>
    public async Task<List<DebitTarget>> OpenDebitsAsync(LegalEntityId legalEntity, BillingAccountId account, CancellationToken cancellationToken)
    {
        var invoices = await db.Invoices.AsNoTracking()
            .Where(i => i.LegalEntityId == legalEntity && i.BillingAccountId == account && i.Kind == "INVOICE" && OpenInvoiceStates.Contains(i.State))
            .Select(i => new { i.InvoiceId, i.InvoiceNumber, i.DueDate })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (invoices.Count == 0)
        {
            return [];
        }

        var ids = invoices.Select(i => i.InvoiceId).ToList();
        var open = await allocator.OpenByItemAsync(ids, cancellationToken).ConfigureAwait(false);
        var items = await db.InvoiceItems.AsNoTracking().Where(i => ids.Contains(i.InvoiceId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var byInvoice = invoices.ToDictionary(i => i.InvoiceId);
        return
        [
            .. items
                .Select(i => new DebitTarget(
                    i.InvoiceId, i.InvoiceItemId, i.TermId, byInvoice[i.InvoiceId].DueDate, byInvoice[i.InvoiceId].InvoiceNumber, i.LineNo,
                    open.GetValueOrDefault(i.InvoiceItemId)))
                .Where(d => d.Open > 0m),
        ];
    }
}
