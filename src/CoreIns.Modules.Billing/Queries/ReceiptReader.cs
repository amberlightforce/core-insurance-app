using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Paging;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Queries;

/// <summary>The filters of <c>bil.Receipt.list</c>; every one is optional and they combine with AND.</summary>
internal sealed record ReceiptListFilter(
    BillingAccountId? Account, PaymentState? State, BusinessDate? ValueFrom, BusinessDate? ValueTo, string? BankReferencePrefix, decimal? AmountMin, decimal? AmountMax);

/// <summary>
/// <c>bil.Receipt.list</c> (BO-BIL-07): keyset-paged receipts of the legal entity (the shared <see cref="KeysetPaging"/>, UUIDv7 ids are
/// creation order). A receipt holds no payer details; its bank reference is the only free text a payer influences, so the list shows it
/// masked (last four characters) and the filter by prefix runs in the database.
/// </summary>
internal sealed class ReceiptReader(BillingDbContext db)
{
    public async Task<ReceiptListPage> ListAsync(LegalEntityId legalEntity, ReceiptListFilter filter, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (!KeysetPaging.TryTake(limit, KeysetPaging.DefaultPageSize, out var take))
        {
            throw new DomainException(DomainError.Of(ModuleCode.BIL, "VALIDATION", $"The limit must be between 1 and {KeysetPaging.MaxPageSize}."));
        }

        if (!KeysetPaging.TryDecode(cursor, 1, out var parts) || (parts is not null && !Guid.TryParse(parts[0], out _)))
        {
            throw new DomainException(DomainError.Of(ModuleCode.BIL, "VALIDATION", "The cursor is malformed."));
        }

        var query = db.Receipts.AsNoTracking().Where(r => r.LegalEntityId == legalEntity);
        if (filter.Account is { } account)
        {
            query = query.Where(r => r.BillingAccountId == account);
        }

        if (filter.State is { } state)
        {
            var code = Codes.Of(state);
            query = query.Where(r => r.State == code);
        }

        if (filter.ValueFrom is { } from)
        {
            query = query.Where(r => r.ValueDate >= from);
        }

        if (filter.ValueTo is { } to)
        {
            query = query.Where(r => r.ValueDate <= to);
        }

        if (!string.IsNullOrEmpty(filter.BankReferencePrefix))
        {
            var prefix = filter.BankReferencePrefix;
            query = query.Where(r => r.BankReference != null && r.BankReference.StartsWith(prefix));
        }

        if (filter.AmountMin is { } min)
        {
            query = query.Where(r => r.Amount >= min);
        }

        if (filter.AmountMax is { } max)
        {
            query = query.Where(r => r.Amount <= max);
        }

        var allIds = (await query.Select(r => r.ReceiptId).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(i => i.Value);
        var pageIds = KeysetPaging.IdsAfter(allIds, parts is null ? null : Guid.Parse(parts[0]), take + 1).Select(g => new PaymentId(g)).ToList();
        var rows = (await db.Receipts.AsNoTracking().Where(r => pageIds.Contains(r.ReceiptId)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(r => r.ReceiptId.Value).ToList();
        var (page, next) = KeysetPaging.Slice(rows, take, r => KeysetPaging.Encode(KeysetPaging.Part(r.ReceiptId.Value)));

        var ids = page.Select(r => r.ReceiptId).ToList();
        var net = (await db.Allocations.AsNoTracking().Where(a => ids.Contains(a.ReceiptId)).GroupBy(a => a.ReceiptId)
                .Select(g => new { Receipt = g.Key, Sum = g.Sum(a => a.Amount) }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(x => x.Receipt, x => x.Sum);
        var receivable = (await db.ReceivableAllocations.AsNoTracking().Where(a => ids.Contains(a.ReceiptId)).GroupBy(a => a.ReceiptId)
                .Select(g => new { Receipt = g.Key, Sum = g.Sum(a => a.Amount) }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(x => x.Receipt, x => x.Sum);
        return new ReceiptListPage
        {
            Items = [.. page.Select(r => new ReceiptListItem
            {
                Receipt = BillingReader.Receipt(r, net.GetValueOrDefault(r.ReceiptId) + receivable.GetValueOrDefault(r.ReceiptId)),
                BankReferenceMasked = MaskReference(r.BankReference),
            })],
            NextCursor = next,
            Limit = take,
        };
    }

    /// <summary>All but the last four characters replaced by asterisks (a shorter reference is masked whole).</summary>
    public static string? MaskReference(string? reference) =>
        string.IsNullOrEmpty(reference) ? null : reference.Length <= 4 ? new string('*', reference.Length) : new string('*', reference.Length - 4) + reference[^4..];
}
