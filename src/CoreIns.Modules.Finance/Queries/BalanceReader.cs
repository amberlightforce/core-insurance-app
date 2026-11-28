using System.Globalization;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Dapper;

namespace CoreIns.Modules.Finance.Queries;

/// <summary>One account's balance in one book (<c>fin.Balance.get</c>): debit, credit and the net (debit minus credit), functional currency.</summary>
internal sealed record AccountBalance(string Account, string Book, string View, string? Period, string Currency, string Debit, string Credit, string Balance, int Lines, string KnownAt);

/// <summary>
/// Read side of <c>fin.Balance.get</c> (REQ-FIN-078, -100): the balance of one account of the caller's legal entity as the sum of its journal lines.
/// <c>view</c> PERIOD (default, needs a period) sums the lines of one calendar month; CUMULATIVE sums everything up to the end of the period (or all lines
/// without a period). <c>dimensions</c> narrows the lines by line dimension (<c>key:value,key:value</c>). <c>knownAt</c> hides journals posted later.
/// </summary>
internal sealed class BalanceReader(DbSession session)
{
    public const string PeriodView = "PERIOD";
    public const string CumulativeView = "CUMULATIVE";

    /// <summary>The line dimensions a caller can filter by, mapped to their column (a closed list: the column name is never taken from input).</summary>
    private static readonly Dictionary<string, string> DimensionColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["productCode"] = "l.product_code",
        ["coverageCode"] = "l.coverage_code",
        ["chargeType"] = "l.charge_type",
        ["policyNumber"] = "l.policy_number",
    };

    /// <summary>Parses <c>key:value,key:value</c>; null when a key is unknown or a pair is malformed.</summary>
    public static IReadOnlyList<(string Column, string Value)>? ParseDimensions(string? dimensions)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(dimensions))
        {
            return result;
        }

        foreach (var pair in dimensions.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf(':', StringComparison.Ordinal);
            if (index <= 0 || index == pair.Length - 1 || !DimensionColumns.TryGetValue(pair[..index], out var column))
            {
                return null;
            }

            result.Add((column, pair[(index + 1)..]));
        }

        return result;
    }

    public async Task<AccountBalance> GetAsync(
        LegalEntityId legalEntity, string account, string? book, string view, string? period, IReadOnlyList<(string Column, string Value)> dimensions, Instant knownAt, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new { le = legalEntity.Value, account, book, knownAt = knownAt.ToUtcDateTime() });
        var filter = string.Empty;
        if (period is not null)
        {
            var start = DateOnly.ParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
            args.Add("end", start.AddMonths(1), System.Data.DbType.Date);
            if (view == PeriodView)
            {
                args.Add("start", start, System.Data.DbType.Date);
                filter += " AND j.accounting_date >= @start";
            }

            filter += " AND j.accounting_date < @end";
        }

        for (var i = 0; i < dimensions.Count; i++)
        {
            filter += $" AND {dimensions[i].Column} = @d{i}";
            args.Add($"d{i}", dimensions[i].Value);
        }

        var row = await connection.QuerySingleAsync<Sums>(new CommandDefinition(
            $"""
            SELECT coalesce(sum(l.amount_functional) FILTER (WHERE l.side = 'DEBIT'), 0) AS Debit,
                   coalesce(sum(l.amount_functional) FILTER (WHERE l.side = 'CREDIT'), 0) AS Credit,
                   count(*)::int AS Lines, max(trim(l.functional_currency)) AS Currency
              FROM fin.journal_line l JOIN fin.journal_entry j ON j.journal_id = l.journal_id
             WHERE j.legal_entity_id = @le AND l.account_code = @account AND (@book::text IS NULL OR l.book = @book) AND j.posted_at <= @knownAt{filter}
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new AccountBalance(account, book ?? "*", view, period, row.Currency ?? "EUR", Money(row.Debit), Money(row.Credit), Money(row.Debit - row.Credit), row.Lines, knownAt.ToString());
    }

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private sealed class Sums
    {
        public decimal Debit { get; set; }

        public decimal Credit { get; set; }

        public int Lines { get; set; }

        public string? Currency { get; set; }
    }
}
