using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Queries;

/// <summary>What the ledger reads are limited to; every part but the legal entity is optional. Dates are accounting dates, both ends inclusive.</summary>
internal sealed record LedgerFilter(
    LegalEntityId LegalEntity,
    BillingAccountId? BillingAccount = null,
    string? Account = null,
    string? EntryType = null,
    string? Currency = null,
    BusinessDate? From = null,
    BusinessDate? To = null);

/// <summary>One group of ledger lines: account, entry type and currency, with the debit and credit sums and the line count.</summary>
internal sealed record LedgerTotal(string Account, string EntryType, string Currency, decimal Debit, decimal Credit, int LineCount, IReadOnlyList<Guid>? EntryIds);

/// <summary>
/// Read side of the billing sub-ledger (BO-BIL-12, REQ-BIL-011, REQ-BIL-293): <c>bil.Ledger.query</c> (entries with their lines),
/// <c>bil.Ledger.balance</c> (debit, credit and net per account and currency) and the control totals FIN reconciles against.
/// The ledger is append-only, so these reads are consistent without locking.
/// </summary>
internal sealed class LedgerReader(BillingDbContext db)
{
    private const string Debit = "DEBIT";

    /// <summary>The most entry ids listed per total (a day can hold more; the count stays exact).</summary>
    // TODO(hardening): page the entry ids of a total instead of capping them.
    private const int MaxEntryIds = 1000;

    /// <summary>Entries oldest first (UUIDv7 ids are creation order and a stable cursor) with all their lines; the filter picks entries having a matching line.</summary>
    public async Task<LedgerQueryPage> QueryAsync(LedgerFilter filter, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(limit ?? 50, 1, 200);
        var entries = Entries(filter);
        if (Guid.TryParse(cursor, out var after))
        {
            entries = entries.Where(e => e.EntryId.CompareTo(after) > 0);
        }

        var window = await entries.OrderBy(e => e.EntryId).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = window.Take(size).ToList();
        var ids = page.Select(e => e.EntryId).ToList();
        var lines = (await db.LedgerLines.AsNoTracking().Where(l => ids.Contains(l.EntryId)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(l => l.EntryId).ToDictionary(g => g.Key, g => g.OrderBy(l => l.LineNo).ToList());
        return new LedgerQueryPage
        {
            Items = [.. page.Select(e => new LedgerQueryItem
            {
                EntryId = e.EntryId,
                EntryType = e.EntryType,
                AccountingDate = e.AccountingDate,
                BusinessDate = e.BusinessDate,
                BillingAccountId = e.BillingAccountId,
                DisbursementId = e.DisbursementId,
                ReversesEntryId = e.ReversesEntryId,
                CauseOperation = e.CauseOperation,
                Lines = [.. lines.GetValueOrDefault(e.EntryId, []).Select(l => new LedgerLineView
                {
                    LineNo = l.LineNo,
                    Account = l.AccountCode,
                    Side = l.Side == Debit ? LedgerLineView.SideValue.Debit : LedgerLineView.SideValue.Credit,
                    Amount = new Money(l.Amount, Currency.FromCode(l.Currency)),
                    ChargeType = l.ChargeType,
                    ChargeCategory = l.ChargeCategory,
                    InvoiceId = l.InvoiceId,
                    ReceiptId = l.ReceiptId,
                    DisbursementId = l.DisbursementId,
                })],
            })],
            NextCursor = window.Count > size ? page[^1].EntryId.ToString("D") : null,
            Limit = size,
        };
    }

    /// <summary>Debit, credit and net per account and currency.</summary>
    public async Task<LedgerBalanceResponse> BalanceAsync(LedgerFilter filter, CancellationToken cancellationToken)
    {
        var totals = await TotalsAsync(filter, includeEntryIds: false, cancellationToken).ConfigureAwait(false);
        return new LedgerBalanceResponse
        {
            Balances = [.. totals.GroupBy(t => (t.Account, t.Currency)).OrderBy(g => g.Key.Account, StringComparer.Ordinal).ThenBy(g => g.Key.Currency, StringComparer.Ordinal).Select(g =>
            {
                var currency = Currency.FromCode(g.Key.Currency);
                var debit = g.Sum(t => t.Debit);
                var credit = g.Sum(t => t.Credit);
                return new LedgerBalanceLine
                {
                    Account = g.Key.Account,
                    Currency = g.Key.Currency,
                    Debit = new Money(debit, currency),
                    Credit = new Money(credit, currency),
                    Net = new Money(debit - credit, currency),
                    LineCount = g.Sum(t => t.LineCount),
                };
            })],
        };
    }

    /// <summary>Line sums per account, entry type and currency over the filter, in account, entry type, currency order.</summary>
    public async Task<IReadOnlyList<LedgerTotal>> TotalsAsync(LedgerFilter filter, bool includeEntryIds, CancellationToken cancellationToken)
    {
        var lines = Lines(filter);
        var sums = await lines
            .GroupBy(x => new { x.Account, x.EntryType, x.Currency })
            .Select(g => new
            {
                g.Key.Account,
                g.Key.EntryType,
                g.Key.Currency,
                Debit = g.Sum(x => x.Side == Debit ? x.Amount : 0m),
                Credit = g.Sum(x => x.Side == Debit ? 0m : x.Amount),
                Count = g.Count(),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<(string, string, string), List<Guid>>? ids = null;
        if (includeEntryIds)
        {
            var pairs = await lines.Select(x => new { x.Account, x.EntryType, x.Currency, x.EntryId }).Distinct()
                .OrderBy(x => x.EntryId).ToListAsync(cancellationToken).ConfigureAwait(false);
            ids = pairs.GroupBy(p => (p.Account, p.EntryType, p.Currency)).ToDictionary(g => g.Key, g => g.Select(p => p.EntryId).Take(MaxEntryIds).ToList());
        }

        return [.. sums
            .OrderBy(s => s.Account, StringComparer.Ordinal).ThenBy(s => s.EntryType, StringComparer.Ordinal).ThenBy(s => s.Currency, StringComparer.Ordinal)
            .Select(s => new LedgerTotal(s.Account, s.EntryType, s.Currency, s.Debit, s.Credit, s.Count, ids?.GetValueOrDefault((s.Account, s.EntryType, s.Currency))))];
    }

    private IQueryable<LedgerEntryRow> Entries(LedgerFilter f)
    {
        var entries = db.LedgerEntries.AsNoTracking().Where(e => e.LegalEntityId == f.LegalEntity);
        if (f.BillingAccount is { } account)
        {
            entries = entries.Where(e => e.BillingAccountId == account);
        }

        if (f.EntryType is { } type)
        {
            entries = entries.Where(e => e.EntryType == type);
        }

        if (f.From is { } from)
        {
            entries = entries.Where(e => e.AccountingDate >= from);
        }

        if (f.To is { } to)
        {
            entries = entries.Where(e => e.AccountingDate <= to);
        }

        if (f.Account is { } code && f.Currency is { } currency)
        {
            entries = entries.Where(e => db.LedgerLines.Any(l => l.EntryId == e.EntryId && l.AccountCode == code && l.Currency == currency));
        }
        else if (f.Account is { } onlyCode)
        {
            entries = entries.Where(e => db.LedgerLines.Any(l => l.EntryId == e.EntryId && l.AccountCode == onlyCode));
        }
        else if (f.Currency is { } onlyCurrency)
        {
            entries = entries.Where(e => db.LedgerLines.Any(l => l.EntryId == e.EntryId && l.Currency == onlyCurrency));
        }

        return entries;
    }

    private IQueryable<LineOfEntry> Lines(LedgerFilter f)
    {
        var lines = from l in db.LedgerLines.AsNoTracking()
                    join e in Entries(f with { Account = null, Currency = null }) on l.EntryId equals e.EntryId
                    select new LineOfEntry { EntryId = e.EntryId, EntryType = e.EntryType, Account = l.AccountCode, Currency = l.Currency, Side = l.Side, Amount = l.Amount };
        if (f.Account is { } code)
        {
            lines = lines.Where(x => x.Account == code);
        }

        if (f.Currency is { } currency)
        {
            lines = lines.Where(x => x.Currency == currency);
        }

        return lines;
    }

    private sealed class LineOfEntry
    {
        public Guid EntryId { get; init; }

        public string EntryType { get; init; } = string.Empty;

        public string Account { get; init; } = string.Empty;

        public string Currency { get; init; } = string.Empty;

        public string Side { get; init; } = string.Empty;

        public decimal Amount { get; init; }
    }
}
