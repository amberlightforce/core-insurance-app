using CoreIns.Modules.Data.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Data.Services.Marts;

/// <summary>
/// RI_CESSION_SUMMARY: ceded premium and ceding commission by RI contract and month, summed from the <c>ri.CessionCalculated</c> deltas of the raw
/// layer (valid time = occurred_at, record time = received_at). There is no conformed RI fact yet.
/// Summing is correct and intentional: <c>ri.CessionCalculated</c> carries true incremental deltas, not restated totals (RI books
/// "cession target minus booked" per delta set; a recomputation or POL reverse-and-reapply emits further, possibly negative, deltas with a new
/// deltaSetId). The net of all deltas is the current cession, so latest-per-key would be wrong. Row order never affects the sum, so the snapshot hash stays deterministic.
/// The product filter is not applied (RI keys on contract, the event has no product code).
/// </summary>
internal sealed class RiCessionSummaryMart(DataDbContext db) : IMartDefinition
{
    public const string MartCode = "RI_CESSION_SUMMARY";

    public string Code => MartCode;

    public string LegalStatus => "NotRegulatory";

    public async Task<IReadOnlyList<MartFigure>> ComputeAsync(MartScope scope, CancellationToken cancellationToken)
    {
        var values = new List<object> { scope.LegalEntity, scope.Valid, scope.Known };
        var predicate = "legal_entity = {0} AND registry_name = 'ri.CessionCalculated' AND occurred_at <= {1} AND received_at <= {2}";
        if (scope.From is { } from)
        {
            predicate += $" AND occurred_at >= {{{values.Count}}}";
            values.Add(from);
        }

        if (scope.To is { } to)
        {
            predicate += $" AND occurred_at < {{{values.Count}}}";
            values.Add(to);
        }

        var sql = $"""
            SELECT 'ALL'::text AS "Product", to_char(occurred_at AT TIME ZONE 'UTC', 'YYYY-MM') AS "Month",
                   COALESCE(envelope->'payload'->>'riContractId', '') AS "Dimension",
                   COALESCE(SUM((envelope->'payload'->'cededAmount'->'transaction'->>'amount')::numeric), 0)::numeric AS "Amount",
                   COALESCE(SUM((envelope->'payload'->'commissionAmount'->'transaction'->>'amount')::numeric), 0)::numeric AS "Amount2",
                   COUNT(*)::bigint AS "N"
            FROM rpt_raw.event_ledger
            WHERE {predicate}
            GROUP BY 1, 2, 3
            """;
        var rows = await db.Database.SqlQueryRaw<CessionLine>(sql, [.. values]).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.SelectMany(r => new[]
        {
            new MartFigure(r.Product, r.Month, r.Dimension, "CEDED_PREMIUM", r.Amount, MartSql.Eur, r.N),
            new MartFigure(r.Product, r.Month, r.Dimension, "CEDED_COMMISSION", r.Amount2, MartSql.Eur, r.N),
            new MartFigure(r.Product, r.Month, r.Dimension, "CESSION_COUNT", r.N, "COUNT", r.N),
        })];
    }

    private sealed class CessionLine
    {
        public string Product { get; set; } = string.Empty;

        public string Month { get; set; } = string.Empty;

        public string Dimension { get; set; } = string.Empty;

        public decimal Amount { get; set; }

        public decimal Amount2 { get; set; }

        public long N { get; set; }
    }
}
