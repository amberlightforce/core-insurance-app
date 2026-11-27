using System.Text.Json;
using CoreIns.Modules.Data.Contracts.Api;
using CoreIns.Modules.Data.Persistence;
using CoreIns.Modules.Data.Services.Marts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Paging;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Data.Services.Lineage;

/// <summary>
/// Upstream lineage of one mart figure (BO-DAT-05): figure -> run -> the conformed fact rows (or raw events) it summed -> the raw ledger events they came
/// from. It re-runs the same scope predicate the mart used (run valid/known instants, period, product, plus the figure's product and month), so the
/// traced rows are exactly the rows behind <c>source_count</c>; <c>complete</c> says the two agree. Read-only; adjustment lines are part of the lineage.
/// </summary>
internal sealed class LineageTracer(DataDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed class TraceRow
    {
        public string Source { get; set; } = string.Empty;

        public Guid RowId { get; set; }

        public Guid SourceEventId { get; set; }

        public string ValidAt { get; set; } = string.Empty;

        public decimal? Amount { get; set; }

        public string Detail { get; set; } = string.Empty;
    }

    private sealed class CountLine
    {
        public long N { get; set; }
    }

    /// <summary>One set of source rows: a conformed table (or the raw ledger) with the SQL predicate and its parameters.</summary>
    private sealed record Source(string Name, string Table, string Select, string Where, object[] Values, string OrderColumn);

    public async Task<Result<LineageTraceResponse>> TraceAsync(
        string legalEntity, string? node, string? runId, string? figureKey, string? direction, string? cursor, int? limit, CancellationToken ct)
    {
        if (node is { Length: > 0 } && node.IndexOf('/', StringComparison.Ordinal) is var slash and > 0)
        {
            runId ??= node[..slash];
            figureKey ??= node[(slash + 1)..];
        }
        else if (node is { Length: > 0 })
        {
            runId ??= node;
        }

        if (direction is not null && !string.Equals(direction, "upstream", StringComparison.Ordinal))
        {
            return Invalid("direction must be 'upstream'.");
        }

        if (!Guid.TryParse(runId, out var run) || string.IsNullOrEmpty(figureKey) || figureKey.Length > 300)
        {
            return Invalid("runId (a UUID) and figureKey are required (or node '<runId>/<figureKey>').");
        }

        if (!KeysetPaging.TryTake(limit, KeysetPaging.DefaultPageSize, out var take) || !KeysetPaging.TryDecode(cursor, 2, out var parts))
        {
            return Invalid("cursor and limit (1..200) must be well formed.");
        }

        var startSource = 0;
        Guid? after = null;
        if (parts is not null)
        {
            if (!int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out startSource) || !Guid.TryParse(parts[1], out var a))
            {
                return Invalid("cursor is malformed.");
            }

            after = a;
        }

        var runRow = await db.MartRuns.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == run && r.LegalEntity == legalEntity, ct).ConfigureAwait(false);
        var figure = runRow is null ? null : await db.MartFigures.AsNoTracking().FirstOrDefaultAsync(f => f.RunId == run && f.FigureKey == figureKey, ct).ConfigureAwait(false);
        if (runRow is null || figure is null)
        {
            return DomainError.Of(ModuleCode.DAT, "NOT-FOUND", "The mart run or figure does not exist.");
        }

        if (!AnalyticsReader.TryPeriod(string.IsNullOrEmpty(runRow.Period) ? null : runRow.Period, out var from, out var to))
        {
            return Invalid("The stored run period cannot be read.");
        }

        var sources = SourcesOf(runRow, figure, from, to);

        // Page across the sources in order; the cursor is (source index, last row id).
        var collected = new List<(int Index, TraceRow Row)>();
        for (var i = startSource; i < sources.Count && collected.Count < take + 1; i++)
        {
            var rows = await ReadAsync(sources[i], i == startSource ? after : null, take + 1 - collected.Count, ct).ConfigureAwait(false);
            collected.AddRange(rows.Select(r => (i, r)));
        }

        var (page, next) = KeysetPaging.Slice(collected, take, c => KeysetPaging.Encode(c.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), c.Row.RowId.ToString("D")));
        long traced = 0;
        var nodes = new List<object>();
        foreach (var source in sources)
        {
            var count = await CountAsync(source, ct).ConfigureAwait(false);
            traced += count;
            nodes.Add(new { id = $"source:{source.Name}", type = "source", label = source.Name, rows = count });
        }

        var eventIds = page.Select(c => c.Row.SourceEventId).Distinct().ToList();
        var events = eventIds.Count == 0
            ? []
            : await db.EventLedger.AsNoTracking().Where(e => eventIds.Contains(e.EventId) && e.LegalEntity == legalEntity)
                .Select(e => new { e.EventId, e.RegistryName, e.Producer, e.AggregateType, e.AggregateId, e.OccurredAt, e.ReceivedAt }).ToListAsync(ct).ConfigureAwait(false);

        // Adjustment lines belong to the figure's lineage; they are listed once, on the first page.
        var adjustments = after is null && startSource == 0
            ? (await db.MartAdjustments.AsNoTracking().Where(a => a.RunId == run && a.FigureKey == figureKey).ToListAsync(ct).ConfigureAwait(false))
                .OrderBy(a => a.CreatedAt).ThenBy(a => a.AdjustmentId)
                .Select(a => new { adjustmentId = a.AdjustmentId, amount = a.Amount, currency = a.Currency, reason = a.Reason, evidence = a.EvidenceRef, createdBy = a.CreatedBy, createdAt = AnalyticsReader.Iso(a.CreatedAt.ToUtcDateTime()) })
                .ToList()
            : null;

        nodes.Insert(0, new { id = $"run:{runRow.RunId}", type = "mart_run", label = runRow.MartCode, snapshotHash = runRow.SnapshotHash });
        nodes.Insert(0, new { id = $"figure:{figure.FigureKey}", type = "figure", label = figure.Metric, sourceCount = figure.SourceCount });
        nodes.Add(new { id = "raw:rpt_raw.event_ledger", type = "raw", label = "rpt_raw.event_ledger" });
        var edges = new List<object> { new { from = $"run:{runRow.RunId}", to = $"figure:{figure.FigureKey}" } };
        foreach (var source in sources)
        {
            edges.Add(new { from = $"source:{source.Name}", to = $"run:{runRow.RunId}" });
            if (source.Table != "rpt_raw.event_ledger")
            {
                edges.Add(new { from = "raw:rpt_raw.event_ledger", to = $"source:{source.Name}" });
            }
        }

        return new LineageTraceResponse
        {
            Graph = Ser(new { direction = "upstream", nodes, edges }),
            SourceRecordsRef = Ser(new
            {
                runId = runRow.RunId,
                figureKey = figure.FigureKey,
                validAt = AnalyticsReader.Iso(runRow.ValidAt.ToUtcDateTime()),
                knownAt = AnalyticsReader.Iso(runRow.KnownAt.ToUtcDateTime()),
                sourceCount = figure.SourceCount,
                tracedCount = traced,
                complete = traced == figure.SourceCount,
                rows = page.Select(c => new { source = c.Row.Source, rowId = c.Row.RowId, sourceEventId = c.Row.SourceEventId, validAt = c.Row.ValidAt, amount = c.Row.Amount, detail = c.Row.Detail }).ToList(),
                rawEvents = events.OrderBy(e => e.OccurredAt).ThenBy(e => e.EventId).Select(e => new
                {
                    eventId = e.EventId, registry = e.RegistryName, producer = e.Producer, aggregateType = e.AggregateType, aggregateId = e.AggregateId,
                    occurredAt = AnalyticsReader.Iso(e.OccurredAt.ToUtcDateTime()), receivedAt = AnalyticsReader.Iso(e.ReceivedAt.ToUtcDateTime()),
                }).ToList(),
                adjustments,
            }),
            NextCursor = next,
        };
    }

    private static DomainError Invalid(string message) => DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, message);

    private static JsonElement Ser<T>(T value) => JsonSerializer.SerializeToElement(value, Json);

    private const string FactSelect = """
        row_id AS "RowId", source_event_id AS "SourceEventId", to_char(valid_from AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"') AS "ValidAt", amount AS "Amount"
        """;

    private static Source Fact(string table, string extra, AnalyticsReader.SqlArgs args, string detail, MartFigureRow figure)
    {
        var values = new List<object>(args.Values);
        var where = $"{args.Predicate} AND {extra}";
        if (figure.ProductCode.Length > 0)
        {
            where += $" AND product_code = {{{values.Count}}}";
            values.Add(figure.ProductCode);
        }

        if (figure.PeriodMonth.Length > 0)
        {
            where += $" AND to_char(valid_from AT TIME ZONE 'UTC', 'YYYY-MM') = {{{values.Count}}}";
            values.Add(figure.PeriodMonth);
        }

        return new Source($"rpt_conformed.{table}", $"rpt_conformed.{table}", $"'rpt_conformed.{table}'::text AS \"Source\", {FactSelect}, {detail} AS \"Detail\"", where, [.. values], "row_id");
    }

    private static List<Source> SourcesOf(MartRunRow run, MartFigureRow figure, DateTime? from, DateTime? to)
    {
        var scope = new MartScope(run.LegalEntity, run.ValidAt.ToUtcDateTime(), run.KnownAt.ToUtcDateTime(), run.ProductCode.Length == 0 ? null : run.ProductCode, from, to);
        if (run.MartCode == RiCessionSummaryMart.MartCode)
        {
            return [RiSource(scope, figure)];
        }

        var premium = Fact("fact_premium", "category = 'PREMIUM'", MartSql.Bounded(scope), "category", figure);
        var paid = Fact("fact_claim_movement", "movement_type IN ('PAYMENT','PAYMENT_VOID')", MartSql.Bounded(scope), "movement_type", figure);
        var reserve = Fact("fact_claim_movement", "movement_type = 'RESERVE_CHANGE'", MartSql.Stock(scope), "movement_type", figure);
        if (run.MartCode == PortfolioPremiumMart.MartCode)
        {
            return [premium];
        }

        return figure.Metric switch
        {
            "GWP" => [premium],
            "CLAIMS_PAID" => [paid],
            "OPEN_RESERVE" => [reserve],
            _ => [premium, paid, reserve], // LOSS_RATIO
        };
    }

    /// <summary>The raw <c>ri.CessionCalculated</c> deltas behind a cession figure (same predicate as <see cref="RiCessionSummaryMart"/>, plus the figure's month and contract).</summary>
    private static Source RiSource(MartScope scope, MartFigureRow figure)
    {
        var values = new List<object> { scope.LegalEntity, scope.Valid, scope.Known };
        var where = "legal_entity = {0} AND registry_name = 'ri.CessionCalculated' AND occurred_at <= {1} AND received_at <= {2}";
        if (scope.From is { } from)
        {
            where += $" AND occurred_at >= {{{values.Count}}}";
            values.Add(from);
        }

        if (scope.To is { } to)
        {
            where += $" AND occurred_at < {{{values.Count}}}";
            values.Add(to);
        }

        where += $" AND to_char(occurred_at AT TIME ZONE 'UTC', 'YYYY-MM') = {{{values.Count}}}";
        values.Add(figure.PeriodMonth);
        where += $" AND COALESCE(envelope->'payload'->>'riContractId', '') = {{{values.Count}}}";
        values.Add(figure.Dimension);
        var amount = figure.Metric == "CEDED_COMMISSION" ? "commissionAmount" : "cededAmount";
        return new Source(
            "rpt_raw.event_ledger",
            "rpt_raw.event_ledger",
            $"""
            'rpt_raw.event_ledger'::text AS "Source", event_id AS "RowId", event_id AS "SourceEventId", to_char(occurred_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"') AS "ValidAt",
            (envelope->'payload'->'{amount}'->'transaction'->>'amount')::numeric AS "Amount", registry_name AS "Detail"
            """,
            where,
            [.. values],
            "event_id");
    }

    private async Task<List<TraceRow>> ReadAsync(Source source, Guid? after, int take, CancellationToken ct)
    {
        var values = new List<object>(source.Values);
        var where = source.Where;
        if (after is { } id)
        {
            where += $" AND {source.OrderColumn} > {{{values.Count}}}";
            values.Add(id);
        }

        var sql = $"SELECT {source.Select} FROM {source.Table} WHERE {where} ORDER BY {source.OrderColumn} LIMIT {take}";
        return await db.Database.SqlQueryRaw<TraceRow>(sql, [.. values]).ToListAsync(ct).ConfigureAwait(false);
    }

    private async Task<long> CountAsync(Source source, CancellationToken ct)
    {
        var sql = $"""SELECT COUNT(*)::bigint AS "N" FROM {source.Table} WHERE {source.Where}""";
        return (await db.Database.SqlQueryRaw<CountLine>(sql, source.Values).ToListAsync(ct).ConfigureAwait(false)).Single().N;
    }
}
