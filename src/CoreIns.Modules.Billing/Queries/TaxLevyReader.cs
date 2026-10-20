using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Queries;

/// <summary>
/// Reads the IPT (LA-06) and levy (LA-07) payable accruals of the sub-ledger per accounting month and currency (bil.TaxLevy.periods / detail,
/// REQ-BIL-269..278). Accrued = credits minus debits on the payable account. Reads only: tax payments are TAX_REMITTANCE disbursements requested by FIN.
/// TODO(hardening): split levy per authority/type and track remitted vs outstanding once the levy-type dimension is on the ledger line.
/// </summary>
internal sealed class TaxLevyReader(BillingDbContext db)
{
    private static readonly string[] PayableAccounts = ["LA-06", "LA-07"];

    private sealed record Bucket(string Period, string Kind, string Currency, decimal Accrued, int Lines);

    /// <summary>Per-period totals; <paramref name="period"/> (yyyy-MM) narrows to one month.</summary>
    public async Task<TaxLevyPeriodsResponse> PeriodsAsync(LegalEntityId legalEntity, string? period, CancellationToken cancellationToken)
    {
        var buckets = await BucketsAsync(legalEntity, period, cancellationToken).ConfigureAwait(false);
        var totals = buckets.GroupBy(b => (b.Kind, b.Currency))
            .Select(g => new { kind = g.Key.Kind, currency = g.Key.Currency, accrued = g.Sum(b => b.Accrued) }).ToList();
        var detail = buckets.GroupBy(b => b.Period).OrderBy(g => g.Key)
            .Select(g => new { period = g.Key, items = g.Select(b => new { kind = b.Kind, currency = b.Currency, accrued = b.Accrued }).ToList() }).ToList();
        return new TaxLevyPeriodsResponse { Totals = JsonSerializer.SerializeToElement(totals, JsonSerializerOptions.Web), Detail = JsonSerializer.SerializeToElement(detail, JsonSerializerOptions.Web) };
    }

    /// <summary>Per-period, per-rule detail of the accruals; <paramref name="period"/> (yyyy-MM) narrows to one month.</summary>
    public async Task<TaxLevyDetailResponse> DetailAsync(LegalEntityId legalEntity, string? period, CancellationToken cancellationToken)
    {
        var rows = await LinesAsync(legalEntity, period, cancellationToken).ConfigureAwait(false);
        var detail = rows.GroupBy(r => (r.Period, r.AccountCode, r.RuleId, r.Currency)).OrderBy(g => g.Key.Period).ThenBy(g => g.Key.AccountCode).ThenBy(g => g.Key.RuleId)
            .Select(g => new
            {
                period = g.Key.Period,
                kind = Kind(g.Key.AccountCode),
                ruleId = g.Key.RuleId,
                currency = g.Key.Currency,
                accrued = g.Sum(r => Signed(r.Side, r.Amount)),
                lines = g.Count(),
            }).ToList();
        var totals = rows.GroupBy(r => (r.AccountCode, r.Currency))
            .Select(g => new { kind = Kind(g.Key.AccountCode), currency = g.Key.Currency, accrued = g.Sum(r => Signed(r.Side, r.Amount)) }).ToList();
        return new TaxLevyDetailResponse { Totals = JsonSerializer.SerializeToElement(totals, JsonSerializerOptions.Web), Detail = JsonSerializer.SerializeToElement(detail, JsonSerializerOptions.Web) };
    }

    /// <summary>True when <paramref name="period"/> is a calendar month <c>yyyy-MM</c>.</summary>
    public static bool IsPeriod(string period) => period.Length == 7 && period[4] == '-' && int.TryParse(period.AsSpan(0, 4), out var y) && y is > 1999 and < 2200
        && int.TryParse(period.AsSpan(5, 2), out var m) && m is >= 1 and <= 12;

    private async Task<List<Bucket>> BucketsAsync(LegalEntityId legalEntity, string? period, CancellationToken cancellationToken)
    {
        var rows = await LinesAsync(legalEntity, period, cancellationToken).ConfigureAwait(false);
        return [.. rows.GroupBy(r => (r.Period, r.AccountCode, r.Currency)).OrderBy(g => g.Key.Period).ThenBy(g => g.Key.AccountCode)
            .Select(g => new Bucket(g.Key.Period, Kind(g.Key.AccountCode), g.Key.Currency, g.Sum(r => Signed(r.Side, r.Amount)), g.Count()))];
    }

    private async Task<List<LevyLine>> LinesAsync(LegalEntityId legalEntity, string? period, CancellationToken cancellationToken)
    {
        var query = from line in db.LedgerLines.AsNoTracking()
                    join entry in db.LedgerEntries.AsNoTracking() on line.EntryId equals entry.EntryId
                    where entry.LegalEntityId == legalEntity && PayableAccounts.Contains(line.AccountCode)
                    select new { entry.AccountingDate, line.AccountCode, line.RuleId, line.Side, line.Currency, line.Amount };
        if (period is not null)
        {
            var first = new BusinessDate(int.Parse(period[..4], CultureInfo.InvariantCulture), int.Parse(period[5..], CultureInfo.InvariantCulture), 1);
            var next = first.AddMonths(1);
            query = query.Where(l => l.AccountingDate >= first && l.AccountingDate < next);
        }

        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new LevyLine(r.AccountingDate.Year.ToString("0000", CultureInfo.InvariantCulture) + "-" + r.AccountingDate.Month.ToString("00", CultureInfo.InvariantCulture), r.AccountCode, r.RuleId, r.Side, r.Currency, r.Amount))];
    }

    private static string Kind(string accountCode) => accountCode == "LA-06" ? "IPT" : "LEVY";

    private static decimal Signed(string side, decimal amount) => string.Equals(side, "CREDIT", StringComparison.OrdinalIgnoreCase) ? amount : -amount;

    private sealed record LevyLine(string Period, string AccountCode, string RuleId, string Side, string Currency, decimal Amount);
}
