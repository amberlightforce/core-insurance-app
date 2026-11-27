using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Data.Contracts.Api;
using CoreIns.Modules.Data.Persistence;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Paging;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Data.Services;

/// <summary>
/// Reads of the conformed facts (BO-DAT-02): bitemporal as-of query, population aggregate and KPIs. Every read is scoped to the
/// caller's legal entity. A row is visible at (validAt, knownAt) when valid_from &lt;= validAt &lt; valid_to and
/// recorded_from &lt;= knownAt &lt; recorded_to (open ends are null).
/// </summary>
internal sealed class AnalyticsReader(DataDbContext db, IClock clock)
{
    public static readonly IReadOnlyList<string> Views = ["fact_policy_term", "fact_premium", "fact_claim_movement"];

    public const string DefinitionVersion = "1.0";

    // TODO(hardening): single reporting currency assumed (EUR); convert or group by currency when a second one is issued.
    internal const string ReportingCurrency = "EUR";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- Query.asOf --------------------------------------------------------------------------------------------------------

    public async Task<Result<QueryAsOfResponse>> AsOfAsync(string legalEntity, string? view, string? validAt, string? knownAt, string? filters, string? cursor, int? limit, CancellationToken ct)
    {
        if (view is null || !Views.Contains(view, StringComparer.Ordinal))
        {
            return DomainError.Of(ModuleCode.DAT, "010", $"view must be one of {string.Join(", ", Views)}.");
        }

        if (!TryInstants(validAt, knownAt, out var valid, out var known)
            || !KeysetPaging.TryTake(limit, KeysetPaging.DefaultPageSize, out var take) || !KeysetPaging.TryDecode(cursor, 1, out var parts)
            || !TryFilters(filters, out var filter))
        {
            return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, "validAt/knownAt must be dates or RFC 3339 instants, filters 'key=value,key=value', and the cursor and limit (1..200) well formed.");
        }

        Guid? after = parts is null ? null : Guid.TryParse(parts[0], out var g) ? g : null;
        if (parts is not null && after is null)
        {
            return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, "cursor is malformed.");
        }

        var rows = view switch
        {
            "fact_policy_term" => await PageAsync(db.FactPolicyTerms.AsNoTracking(), legalEntity, valid, known, filter, after, take, r => new JsonObject
            {
                ["policyId"] = r.PolicyId, ["termId"] = r.TermId, ["status"] = r.Status, ["reason"] = r.Reason,
            }, ct).ConfigureAwait(false),
            "fact_premium" => await PageAsync(db.FactPremiums.AsNoTracking(), legalEntity, valid, known, filter, after, take, r => new JsonObject
            {
                ["invoiceId"] = r.InvoiceId, ["termId"] = r.TermId, ["category"] = r.Category, ["kind"] = r.Kind, ["amount"] = r.Amount, ["currency"] = r.Currency,
            }, ct).ConfigureAwait(false),
            _ => await PageAsync(db.FactClaimMovements.AsNoTracking(), legalEntity, valid, known, filter, after, take, r => new JsonObject
            {
                ["claimId"] = r.ClaimId, ["movementKey"] = r.MovementKey, ["movementType"] = r.MovementType, ["amount"] = r.Amount, ["currency"] = r.Currency, ["policyTermId"] = r.PolicyTermId,
            }, ct).ConfigureAwait(false),
        };

        if (rows is null)
        {
            return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, "unknown filter key (allowed: productCode, termId, policyId, claimId, invoiceId, status).");
        }

        var (items, next) = KeysetPaging.Slice(rows, take, r => KeysetPaging.Encode(r.RowId.ToString("D")));
        var json = items.Select(r => r.Json).ToList();
        var asMetadata = new { view, validAt = Iso(valid), knownAt = Iso(known), count = json.Count };
        return new QueryAsOfResponse
        {
            Rows = json.Select(j => JsonSerializer.SerializeToElement(j, Json)).ToList(),
            AsMetadata = JsonSerializer.SerializeToElement(asMetadata, Json),
            Hash = Sha256Hash.ComputeUtf8(new JsonArray(json.Select(j => (JsonNode?)j.DeepClone()).ToArray()).ToJsonString()),
            NextCursor = next,
        };
    }

    private sealed record FactRow(Guid RowId, JsonObject Json);

    private static async Task<List<FactRow>?> PageAsync<T>(
        IQueryable<T> source, string legalEntity, DateTime valid, DateTime known, Dictionary<string, string> filter, Guid? after, int take,
        Func<T, JsonObject> project, CancellationToken ct)
        where T : ConformedFactRow
    {
        var v = Instant.FromUtcDateTime(valid);
        var k = Instant.FromUtcDateTime(known);
        var query = source.Where(f => f.LegalEntity == legalEntity
            && f.ValidFrom <= v && (f.ValidTo == null || f.ValidTo > v)
            && f.RecordedFrom <= k && (f.RecordedTo == null || f.RecordedTo > k));
        foreach (var (key, value) in filter)
        {
            switch (key)
            {
                case "productCode":
                    query = query.Where(f => f.ProductCode == value);
                    break;
                case "termId" or "policyId" or "claimId" or "invoiceId" or "status":
                    query = Narrow(query, key, value);
                    break;
                default:
                    return null;
            }
        }

        if (after is { } cursorId)
        {
            query = query.Where(f => f.RowId > cursorId);
        }

        var page = await query.OrderBy(f => f.RowId).Take(take + 1).ToListAsync(ct).ConfigureAwait(false);
        return [.. page.Select(r =>
        {
            var json = project(r);
            json["rowId"] = r.RowId;
            json["productCode"] = r.ProductCode;
            json["validFrom"] = Iso(r.ValidFrom.ToUtcDateTime());
            json["validTo"] = r.ValidTo is { } vt ? Iso(vt.ToUtcDateTime()) : null;
            json["recordedFrom"] = Iso(r.RecordedFrom.ToUtcDateTime());
            json["recordedTo"] = r.RecordedTo is { } rt ? Iso(rt.ToUtcDateTime()) : null;
            json["sourceEventId"] = r.SourceEventId;
            return new FactRow(r.RowId, json);
        })];
    }

    private static IQueryable<T> Narrow<T>(IQueryable<T> query, string key, string value)
        where T : ConformedFactRow
    {
        if (query is IQueryable<FactPolicyTermRow> policy)
        {
            var id = Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty;
            return (IQueryable<T>)(key switch
            {
                "termId" => policy.Where(f => f.TermId == id),
                "policyId" => policy.Where(f => f.PolicyId == id),
                "status" => policy.Where(f => f.Status == value),
                _ => policy.Where(_ => false),
            });
        }

        if (query is IQueryable<FactPremiumRow> premium)
        {
            var id = Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty;
            return (IQueryable<T>)(key switch
            {
                "termId" => premium.Where(f => f.TermId == id),
                "invoiceId" => premium.Where(f => f.InvoiceId == id),
                _ => premium.Where(_ => false),
            });
        }

        var claims = (IQueryable<FactClaimMovementRow>)query;
        var claimKey = Guid.TryParse(value, out var parsedClaim) ? parsedClaim : Guid.Empty;
        return (IQueryable<T>)(key switch
        {
            "termId" => claims.Where(f => f.PolicyTermId == claimKey),
            "claimId" => claims.Where(f => f.ClaimId == claimKey),
            _ => claims.Where(_ => false),
        });
    }

    // ---- Population.aggregate ----------------------------------------------------------------------------------------------

    private sealed class AggLine
    {
        public string ProductCode { get; set; } = string.Empty;

        public string Month { get; set; } = string.Empty;

        public decimal Amount { get; set; }
    }

    public async Task<Result<PopulationAggregateResponse>> AggregateAsync(
        string legalEntity, string? productSlot, string? asValid, string? validAt, string? knownAt, string? window, string? cursor, int? limit, CancellationToken ct)
    {
        if (!TryInstants(validAt ?? asValid, knownAt, out var valid, out var known)
            || !KeysetPaging.TryTake(limit, KeysetPaging.DefaultPageSize, out var take) || !KeysetPaging.TryDecode(cursor, 2, out var parts)
            || !TryPeriod(window, out var from, out var to))
        {
            return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, "validAt/knownAt must be dates or RFC 3339 instants, window 'YYYY', 'YYYY-MM' or 'from..to', and the cursor and limit (1..200) well formed.");
        }

        var args = Args(legalEntity, valid, known, productSlot, from, to);
        var gwp = await SumByMonthAsync("fact_premium", "amount", "category = 'PREMIUM'", args, ct).ConfigureAwait(false);
        var paid = await SumByMonthAsync("fact_claim_movement", "amount", "movement_type IN ('PAYMENT','PAYMENT_VOID')", args, ct).ConfigureAwait(false);
        var reserve = await SumByMonthAsync("fact_claim_movement", "amount", "movement_type = 'RESERVE_CHANGE'", args, ct).ConfigureAwait(false);
        var terms = await SumByMonthAsync("fact_policy_term", "1", "status = 'IN_FORCE'", args, ct).ConfigureAwait(false);

        var keys = gwp.Keys.Concat(paid.Keys).Concat(reserve.Keys).Concat(terms.Keys).Distinct()
            .OrderBy(k => k.Product, StringComparer.Ordinal).ThenBy(k => k.Month, StringComparer.Ordinal)
            .Where(k => parts is null || string.CompareOrdinal(k.Product, parts[0]) > 0 || (k.Product == parts[0] && string.CompareOrdinal(k.Month, parts[1]) > 0))
            .Take(take + 1).ToList();
        var (page, next) = KeysetPaging.Slice(keys, take, k => KeysetPaging.Encode(k.Product, k.Month));
        var rows = page.Select(k =>
        {
            var g = gwp.GetValueOrDefault(k);
            var p = paid.GetValueOrDefault(k);
            var r = reserve.GetValueOrDefault(k);
            return new
            {
                productCode = k.Product,
                legalEntity,
                month = k.Month,
                gwp = g,
                claimsPaid = p,
                reserveChange = r,
                lossRatio = g == 0 ? (decimal?)null : Ratio(p + r, g),
                inForceTerms = (long)terms.GetValueOrDefault(k),
                currency = ReportingCurrency,
            };
        }).ToList();
        return new PopulationAggregateResponse
        {
            RowsOrAggregates = JsonSerializer.SerializeToElement(rows, Json),
            SnapshotHash = Sha256Hash.ComputeUtf8(JsonSerializer.Serialize(rows, Json)),
            NextCursor = next,
        };
    }

    private async Task<Dictionary<(string Product, string Month), decimal>> SumByMonthAsync(string table, string measure, string extra, SqlArgs args, CancellationToken ct)
    {
        var sql = $"""
            SELECT product_code AS "ProductCode", to_char(valid_from AT TIME ZONE 'UTC', 'YYYY-MM') AS "Month", COALESCE(SUM({measure}), 0)::numeric AS "Amount"
            FROM rpt_conformed.{table}
            WHERE {args.Predicate} AND {extra}
            GROUP BY 1, 2
            """;
        var lines = await db.Database.SqlQueryRaw<AggLine>(sql, args.Values).ToListAsync(ct).ConfigureAwait(false);
        return lines.ToDictionary(l => (l.ProductCode, l.Month), l => l.Amount);
    }

    // ---- Kpi.get -----------------------------------------------------------------------------------------------------------

    public static readonly IReadOnlyList<string> KpiCodes = ["GWP", "CLAIMS_PAID", "OPEN_RESERVE", "LOSS_RATIO"];

    private sealed class SumLine
    {
        public decimal Amount { get; set; }
    }

    public async Task<Result<KpiGetResponse>> KpiAsync(string legalEntity, string? kpiCode, string? filters, string? period, string? validAt, string? knownAt, CancellationToken ct)
    {
        if (kpiCode is not null && !KpiCodes.Contains(kpiCode, StringComparer.Ordinal))
        {
            return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, $"kpiCode must be one of {string.Join(", ", KpiCodes)}.");
        }

        if (!TryInstants(validAt, knownAt, out var valid, out var known) || !TryPeriod(period, out var from, out var to) || !TryFilters(filters, out var filter)
            || filter.Keys.Any(k => k != "productCode"))
        {
            return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, "validAt/knownAt must be dates or RFC 3339 instants, period 'YYYY', 'YYYY-MM' or 'from..to', and filters only 'productCode=...'.");
        }

        var product = filter.GetValueOrDefault("productCode");
        var bounded = Args(legalEntity, valid, known, product, from, to);
        var stock = Args(legalEntity, valid, known, product, null, to); // the open reserve is a stock: up to the period end
        var gwp = await SumAsync("fact_premium", "category = 'PREMIUM'", bounded, ct).ConfigureAwait(false);
        var paid = await SumAsync("fact_claim_movement", "movement_type IN ('PAYMENT','PAYMENT_VOID')", bounded, ct).ConfigureAwait(false);
        var reserve = await SumAsync("fact_claim_movement", "movement_type = 'RESERVE_CHANGE'", stock, ct).ConfigureAwait(false);

        // Definitions v1.0: loss ratio = (claims paid + open reserve) / GWP, null without premium.
        var all = new Dictionary<string, object?>
        {
            ["GWP"] = new { amount = gwp, currency = ReportingCurrency },
            ["CLAIMS_PAID"] = new { amount = paid, currency = ReportingCurrency },
            ["OPEN_RESERVE"] = new { amount = reserve, currency = ReportingCurrency },
            ["LOSS_RATIO"] = new { ratio = gwp == 0 ? (decimal?)null : Ratio(paid + reserve, gwp) },
        };
        var values = kpiCode is null ? all : all.Where(p => p.Key == kpiCode).ToDictionary(p => p.Key, p => p.Value);
        return new KpiGetResponse
        {
            Values = JsonSerializer.SerializeToElement(values, Json),
            DefinitionVersion = JsonSerializer.SerializeToElement(DefinitionVersion, Json),
            As = JsonSerializer.SerializeToElement(new { validAt = Iso(valid), knownAt = Iso(known), period }, Json),
        };
    }

    private async Task<decimal> SumAsync(string table, string extra, SqlArgs args, CancellationToken ct)
    {
        var sql = $"""SELECT COALESCE(SUM(amount), 0)::numeric AS "Amount" FROM rpt_conformed.{table} WHERE {args.Predicate} AND {extra}""";
        return (await db.Database.SqlQueryRaw<SumLine>(sql, args.Values).ToListAsync(ct).ConfigureAwait(false)).Single().Amount;
    }

    // ---- shared ------------------------------------------------------------------------------------------------------------

    /// <summary>A ratio (not money) truncated to four decimals.</summary>
    internal static decimal Ratio(decimal numerator, decimal denominator) => decimal.Truncate(numerator / denominator * 10000m) / 10000m;

    internal sealed record SqlArgs(string Predicate, object[] Values);

    internal static SqlArgs Args(string legalEntity, DateTime valid, DateTime known, string? product, DateTime? from, DateTime? to)
    {
                var values = new List<object> { legalEntity, valid, known };
        var predicate = "legal_entity = {0} AND valid_from <= {1} AND (valid_to IS NULL OR valid_to > {1}) AND recorded_from <= {2} AND (recorded_to IS NULL OR recorded_to > {2})";
        if (product is not null)
        {
            predicate += $" AND product_code = {{{values.Count}}}";
            values.Add(product);
        }

        if (from is { } f)
        {
            predicate += $" AND valid_from >= {{{values.Count}}}";
            values.Add(f);
        }

        if (to is { } t)
        {
            predicate += $" AND valid_from < {{{values.Count}}}";
            values.Add(t);
        }

        return new SqlArgs(predicate, [.. values]);
    }

    internal bool TryInstants(string? validAt, string? knownAt, out DateTime valid, out DateTime known)
    {
        var now = clock.Now.ToUtcDateTime();
        valid = now;
        known = now;
        return (validAt is null || TryInstant(validAt, out valid)) && (knownAt is null || TryInstant(knownAt, out known));
    }

    private static bool TryInstant(string text, out DateTime value)
    {
        if (Instant.TryParse(text, out var instant))
        {
            value = instant.ToUtcDateTime();
            return true;
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            value = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MaxValue), DateTimeKind.Utc); // a date means the end of that day
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Period 'YYYY', 'YYYY-MM' or 'start..end' (each of those); half-open [from, to).</summary>
    internal static bool TryPeriod(string? text, out DateTime? from, out DateTime? to)
    {
        from = null;
        to = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var ends = text.Split("..", 2);
        if (!TryBounds(ends[0], out var start, out var end) || (ends.Length == 2 && !TryBounds(ends[1], out _, out end)))
        {
            return false;
        }

        from = start;
        to = end;
        return true;
    }

    private static bool TryBounds(string text, out DateTime start, out DateTime end)
    {
        start = default;
        end = default;
        if (text.Length == 4 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is > 1900 and < 3000)
        {
            start = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            end = start.AddYears(1);
            return true;
        }

        if (DateTime.TryParseExact(text, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var month))
        {
            start = new DateTime(month.Year, month.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            end = start.AddMonths(1);
            return true;
        }

        return false;
    }

    private static bool TryFilters(string? text, out Dictionary<string, string> filter)
    {
        filter = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || pair[0].Length == 0 || pair[1].Length == 0)
            {
                return false;
            }

            filter[pair[0]] = pair[1];
        }

        return true;
    }

    internal static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
}
