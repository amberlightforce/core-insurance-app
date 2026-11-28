using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Queries;

/// <summary>Filters of <c>bil.BillingAccount.search</c>.</summary>
internal sealed record BillingAccountFilter(PartyId? Payer, BillingAccountStatus? Status, string? Currency, string? AccountNumber);

/// <summary>Read side of <c>bil.BillingAccount.search</c> (BO-BIL-01): account rows with what is still open on each; no personal data.</summary>
internal sealed class BillingAccountSearchReader(BillingDbContext db)
{
    public async Task<BillingAccountSearchPage> SearchAsync(LegalEntityId legalEntity, BillingAccountFilter filter, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var size = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.Accounts.AsNoTracking().Where(a => a.LegalEntityId == legalEntity);
        if (filter.Payer is { } payer)
        {
            query = query.Where(a => a.PayerPartyId == payer);
        }

        if (filter.Status is { } status)
        {
            var code = Codes.Of(status);
            query = query.Where(a => a.Status == code);
        }

        if (filter.Currency is { } currency)
        {
            query = query.Where(a => a.Currency == currency);
        }

        if (filter.AccountNumber is { } number)
        {
            query = query.Where(a => a.AccountNumber == number);
        }

        // Account ids are UUIDv7: ordering by id is creation order and a stable cursor. Paged in memory over the filtered set, like the invoice list.
        // TODO(hardening): a keyset query for a legal entity with very many accounts and no filter.
        var all = (await query.ToListAsync(cancellationToken).ConfigureAwait(false)).OrderBy(a => a.BillingAccountId.Value).ToList();
        if (cursor is not null && Guid.TryParse(cursor, out var after))
        {
            all = [.. all.Where(a => a.BillingAccountId.Value.CompareTo(after) > 0)];
        }

        var window = all.Take(size + 1).ToList();
        var shown = window.Take(size).ToList();
        var ids = shown.Select(a => a.BillingAccountId).ToList();
        var lines = await db.LedgerLines.AsNoTracking()
            .Where(l => l.BillingAccountId != null && ids.Contains(l.BillingAccountId.Value) && l.AccountCode == LedgerAccounts.BilledReceivable)
            .GroupBy(l => new { l.BillingAccountId, l.Currency, l.Side })
            .Select(g => new { g.Key.BillingAccountId, g.Key.Currency, g.Key.Side, Sum = g.Sum(l => l.Amount) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var debit = Codes.Of(LedgerSide.Debit);
        return new BillingAccountSearchPage
        {
            Items = [.. shown.Select(a => new BillingAccountSearchItem
            {
                Account = new BillingAccountSummary
                {
                    BillingAccountId = a.BillingAccountId,
                    AccountNumber = AccountNumber.Parse(a.AccountNumber),
                    PayerPartyId = a.PayerPartyId,
                    Currency = Currency.FromCode(a.Currency),
                    Status = Codes.Api<BillingAccountSummary.StatusValue>(Codes.Parse<BillingAccountStatus>(a.Status)),
                    CreatedAt = a.CreatedAt,
                    RecordVersion = a.RecordVersion,
                },
                OpenBalance = new Money(
                    lines.Where(l => l.BillingAccountId == a.BillingAccountId && l.Currency == a.Currency).Sum(l => l.Side == debit ? l.Sum : -l.Sum),
                    Currency.FromCode(a.Currency)),
            })],
            NextCursor = window.Count > size ? shown[^1].BillingAccountId.Value.ToString("D") : null,
            Limit = size,
        };
    }
}
