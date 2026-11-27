using CoreIns.Modules.Data.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Data.Services.Marts;

/// <summary>One grouped sum read by the mart queries.</summary>
internal sealed class MartLine
{
    public string Product { get; set; } = string.Empty;

    public string Month { get; set; } = string.Empty;

    public string Dimension { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public long N { get; set; }
}

internal static class MartSql
{
    public const string Eur = AnalyticsReader.ReportingCurrency;

    public static async Task<List<MartLine>> GroupAsync(DataDbContext db, string table, string extra, string groupMonth, AnalyticsReader.SqlArgs args, CancellationToken ct)
    {
        var month = groupMonth.Length == 0 ? "''" : "to_char(valid_from AT TIME ZONE 'UTC', 'YYYY-MM')";
        var sql = $"""
            SELECT product_code AS "Product", {month} AS "Month", ''::text AS "Dimension", COALESCE(SUM(amount), 0)::numeric AS "Amount", COUNT(*)::bigint AS "N"
            FROM rpt_conformed.{table}
            WHERE {args.Predicate} AND {extra}
            GROUP BY 1, 2
            """;
        return await db.Database.SqlQueryRaw<MartLine>(sql, args.Values).ToListAsync(ct).ConfigureAwait(false);
    }

    public static AnalyticsReader.SqlArgs Bounded(MartScope s) => AnalyticsReader.Args(s.LegalEntity, s.Valid, s.Known, s.Product, s.From, s.To);

    /// <summary>A stock (open reserve) runs up to the period end, not from its start.</summary>
    public static AnalyticsReader.SqlArgs Stock(MartScope s) => AnalyticsReader.Args(s.LegalEntity, s.Valid, s.Known, s.Product, null, s.To);
}

/// <summary>PORTFOLIO_PREMIUM: gross written premium and premium lines by product and month (KPI GWP v1.0 definition).</summary>
internal sealed class PortfolioPremiumMart(DataDbContext db) : IMartDefinition
{
    public const string MartCode = "PORTFOLIO_PREMIUM";

    public string Code => MartCode;

    public string LegalStatus => "NotRegulatory";

    public async Task<IReadOnlyList<MartFigure>> ComputeAsync(MartScope scope, CancellationToken cancellationToken)
    {
        var rows = await MartSql.GroupAsync(db, "fact_premium", "category = 'PREMIUM'", "month", MartSql.Bounded(scope), cancellationToken).ConfigureAwait(false);
        return [.. rows.SelectMany(r => new[]
        {
            new MartFigure(r.Product, r.Month, string.Empty, "GWP", r.Amount, MartSql.Eur, r.N),
            new MartFigure(r.Product, r.Month, string.Empty, "PREMIUM_LINES", r.N, "COUNT", r.N),
        })];
    }
}

/// <summary>CLAIMS_EXPERIENCE: claims paid, open reserve, GWP and loss ratio by product (KPI definitions v1.0, loss ratio truncated to 4 dp).</summary>
internal sealed class ClaimsExperienceMart(DataDbContext db) : IMartDefinition
{
    public const string MartCode = "CLAIMS_EXPERIENCE";

    public string Code => MartCode;

    public string LegalStatus => "NotRegulatory";

    public async Task<IReadOnlyList<MartFigure>> ComputeAsync(MartScope scope, CancellationToken cancellationToken)
    {
        var gwp = (await MartSql.GroupAsync(db, "fact_premium", "category = 'PREMIUM'", string.Empty, MartSql.Bounded(scope), cancellationToken).ConfigureAwait(false)).ToDictionary(l => l.Product);
        var paid = (await MartSql.GroupAsync(db, "fact_claim_movement", "movement_type IN ('PAYMENT','PAYMENT_VOID')", string.Empty, MartSql.Bounded(scope), cancellationToken).ConfigureAwait(false)).ToDictionary(l => l.Product);
        var reserve = (await MartSql.GroupAsync(db, "fact_claim_movement", "movement_type = 'RESERVE_CHANGE'", string.Empty, MartSql.Stock(scope), cancellationToken).ConfigureAwait(false)).ToDictionary(l => l.Product);

        var figures = new List<MartFigure>();
        foreach (var product in gwp.Keys.Concat(paid.Keys).Concat(reserve.Keys).Distinct(StringComparer.Ordinal))
        {
            var g = gwp.GetValueOrDefault(product);
            var p = paid.GetValueOrDefault(product);
            var r = reserve.GetValueOrDefault(product);
            figures.Add(new MartFigure(product, string.Empty, string.Empty, "GWP", g?.Amount ?? 0m, MartSql.Eur, g?.N ?? 0));
            figures.Add(new MartFigure(product, string.Empty, string.Empty, "CLAIMS_PAID", p?.Amount ?? 0m, MartSql.Eur, p?.N ?? 0));
            figures.Add(new MartFigure(product, string.Empty, string.Empty, "OPEN_RESERVE", r?.Amount ?? 0m, MartSql.Eur, r?.N ?? 0));
            if ((g?.Amount ?? 0m) != 0m)
            {
                figures.Add(new MartFigure(product, string.Empty, string.Empty, "LOSS_RATIO", AnalyticsReader.Ratio((p?.Amount ?? 0m) + (r?.Amount ?? 0m), g!.Amount), "RATIO", (g.N) + (p?.N ?? 0) + (r?.N ?? 0)));
            }
        }

        return figures;
    }
}

