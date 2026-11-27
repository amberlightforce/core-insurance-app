using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Data.Contracts.Api;
using CoreIns.Modules.Data.Persistence;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Data.Services.Marts;

/// <summary>
/// Runs, stores and reads marts (BO-DAT-03). A run computes the figures inline, stores them append-only in <c>rpt_mart</c> and records a
/// snapshot hash over the ordered figures. The hash covers figures only (not run id or time), so re-running with the same inputs gives the same
/// hash and a correction (a new record-time version of a fact) changes it. Compare works on two stored runs or on two record-time instants.
/// </summary>
internal sealed class MartEngine(DataDbContext db, IClock clock, AnalyticsReader reader, IEnumerable<IMartDefinition> definitions)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static readonly IReadOnlyList<string> Codes = [PortfolioPremiumMart.MartCode, ClaimsExperienceMart.MartCode, RiCessionSummaryMart.MartCode];

    private IMartDefinition? Find(string? code) => definitions.FirstOrDefault(d => string.Equals(d.Code, code, StringComparison.Ordinal));

    private sealed record Computed(MartScope Scope, IMartDefinition Definition, IReadOnlyList<MartFigure> Figures, string Hash, long SourceRows);

    private async Task<Result<Computed>> ComputeAsync(string legalEntity, string? mart, string? period, string? productCode, string? validAt, string? knownAt, CancellationToken ct)
    {
        if (Find(mart) is not { } definition)
        {
            return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, $"mart must be one of {string.Join(", ", Codes)}.");
        }

        if (!reader.TryInstants(validAt, knownAt, out var valid, out var known) || !AnalyticsReader.TryPeriod(period, out var from, out var to)
            || productCode is { Length: 0 or > 100 })
        {
            return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, "validAt/knownAt must be dates or RFC 3339 instants, period 'YYYY', 'YYYY-MM' or 'from..to', and productCode 1..100 characters.");
        }

        if (known > clock.Now.ToUtcDateTime())
        {
            return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, "knownAt must not be in the future.");
        }

        var scope = new MartScope(legalEntity, valid, known, productCode, from, to);
        var figures = (await definition.ComputeAsync(scope, ct).ConfigureAwait(false)).OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
        var lines = figures.Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Key}|{f.Value:0.0000}|{f.Unit}|{f.SourceCount}"));
        var sourceRows = figures.Where(f => f.Unit != "RATIO" && f.Unit != "COUNT").Sum(f => f.SourceCount);
        return new Computed(scope, definition, figures, Sha256Hash.ComputeUtf8(string.Join('\n', lines)).Value, sourceRows);
    }

    // ---- Mart.run ----------------------------------------------------------------------------------------------------------

    public async Task<Result<MartRunResponse>> RunAsync(string legalEntity, string actor, MartRunRequest request, CancellationToken ct)
    {
        if (Str(request.Entity) is { } entity && !string.Equals(entity, legalEntity, StringComparison.Ordinal))
        {
            return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, "entity must equal the legal entity in scope.");
        }

        var period = Str(request.Period);
        var product = Str(request.ProductCode);
        var computed = await ComputeAsync(legalEntity, request.Mart switch { MartRunRequest.MartValue.PortfolioPremium => PortfolioPremiumMart.MartCode, MartRunRequest.MartValue.ClaimsExperience => ClaimsExperienceMart.MartCode, MartRunRequest.MartValue.RiCessionSummary => RiCessionSummaryMart.MartCode, _ => null }, period, product, Str(request.ValidAt), Str(request.KnownAt), ct).ConfigureAwait(false);
        if (!computed.IsSuccess)
        {
            return computed.Error!;
        }

        var c = computed.Value;
        var runId = Guid.CreateVersion7();
        var snapshotKey = $"{c.Definition.Code}|{legalEntity}|{period ?? "*"}|{product ?? "*"}|{c.Hash[..16]}";
        db.MartRuns.Add(new MartRunRow
        {
            RunId = runId,
            LegalEntity = legalEntity,
            MartCode = c.Definition.Code,
            Period = period ?? string.Empty,
            ProductCode = product ?? string.Empty,
            ValidAt = Instant.FromUtcDateTime(c.Scope.Valid),
            KnownAt = Instant.FromUtcDateTime(c.Scope.Known),
            Status = "COMPLETED",
            DefinitionVersion = AnalyticsReader.DefinitionVersion,
            LegalStatus = c.Definition.LegalStatus,
            SnapshotHash = c.Hash,
            SnapshotKey = snapshotKey,
            FigureCount = c.Figures.Count,
            SourceRowCount = c.SourceRows,
            CreatedBy = actor,
            CreatedAt = clock.Now,
        });
        foreach (var f in c.Figures)
        {
            db.MartFigures.Add(new MartFigureRow
            {
                FigureId = Guid.CreateVersion7(), RunId = runId, FigureKey = f.Key, ProductCode = f.ProductCode, PeriodMonth = f.PeriodMonth, Dimension = f.Dimension,
                Metric = f.Metric, Value = f.Value, Unit = f.Unit, SourceCount = f.SourceCount,
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new MartRunResponse
        {
            RunId = runId,
            Status = "COMPLETED",
            SnapshotKey = snapshotKey,
            SnapshotHash = Sha256Hash.Parse(c.Hash),
            FigureCount = c.Figures.Count,
            LegalStatus = c.Definition.LegalStatus,
        };
    }

    private static string? Str(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // ---- Mart.get / figure -------------------------------------------------------------------------------------------------

    private async Task<(MartRunRow Run, List<MartFigureRow> Figures)?> LoadAsync(string legalEntity, string? id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var runId))
        {
            return null;
        }

        var run = await db.MartRuns.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == runId && r.LegalEntity == legalEntity, ct).ConfigureAwait(false);
        if (run is null)
        {
            return null;
        }

        var figures = await db.MartFigures.AsNoTracking().Where(f => f.RunId == runId).OrderBy(f => f.FigureKey).ToListAsync(ct).ConfigureAwait(false);
        return (run, figures);
    }

    private static DomainError NotFound() => DomainError.Of(ModuleCode.DAT, "NOT-FOUND", "The mart run does not exist.");

    public async Task<Result<MartGetResponse>> GetAsync(string legalEntity, string id, CancellationToken ct)
    {
        if (await LoadAsync(legalEntity, id, ct).ConfigureAwait(false) is not { } loaded)
        {
            return NotFound();
        }

        return new MartGetResponse
        {
            Run = Ser(RunJson(loaded.Run)),
            Figures = Ser(loaded.Figures.Select(FigureJson)),
            LineagePointer = Ser(Lineage(loaded.Run)),
            Diff = Ser(Array.Empty<object>()),
        };
    }

    public async Task<Result<MartFigureResponse>> FigureAsync(string legalEntity, string? runId, string? figureKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(figureKey))
        {
            return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, "runId and figureKey are required.");
        }

        if (await LoadAsync(legalEntity, runId, ct).ConfigureAwait(false) is not { } loaded)
        {
            return NotFound();
        }

        if (loaded.Figures.FirstOrDefault(f => string.Equals(f.FigureKey, figureKey, StringComparison.Ordinal)) is not { } figure)
        {
            return DomainError.Of(ModuleCode.DAT, "NOT-FOUND", "The figure does not exist in this run.");
        }

        return new MartFigureResponse { Figures = Ser(new[] { FigureJson(figure) }), LineagePointer = Ser(Lineage(loaded.Run, figure)), Diff = Ser(Array.Empty<object>()) };
    }

    // ---- Mart.compare ------------------------------------------------------------------------------------------------------

    public async Task<Result<MartCompareResponse>> CompareAsync(
        string legalEntity, string? runA, string? runB, string? mart, string? period, string? productCode, string? validAt, string? knownAtA, string? knownAtB, CancellationToken ct)
    {
        Dictionary<string, (decimal Value, string Unit)> left;
        Dictionary<string, (decimal Value, string Unit)> right;
        object pointer;
        if (runA is not null || runB is not null)
        {
            if (await LoadAsync(legalEntity, runA, ct).ConfigureAwait(false) is not { } a || await LoadAsync(legalEntity, runB, ct).ConfigureAwait(false) is not { } b)
            {
                return NotFound();
            }

            if (a.Run.MartCode != b.Run.MartCode)
            {
                return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, "The two runs are of different marts.");
            }

            left = a.Figures.ToDictionary(f => f.FigureKey, f => (f.Value, f.Unit));
            right = b.Figures.ToDictionary(f => f.FigureKey, f => (f.Value, f.Unit));
            pointer = new { mode = "runs", a = Lineage(a.Run), b = Lineage(b.Run), sameHash = a.Run.SnapshotHash == b.Run.SnapshotHash };
        }
        else
        {
            var one = await ComputeAsync(legalEntity, mart, Str(period), Str(productCode), validAt, knownAtA, ct).ConfigureAwait(false);
            var two = await ComputeAsync(legalEntity, mart, Str(period), Str(productCode), validAt, knownAtB, ct).ConfigureAwait(false);
            if (!one.IsSuccess)
            {
                return one.Error!;
            }

            if (!two.IsSuccess)
            {
                return two.Error!;
            }

            left = one.Value.Figures.ToDictionary(f => f.Key, f => (f.Value, f.Unit));
            right = two.Value.Figures.ToDictionary(f => f.Key, f => (f.Value, f.Unit));
            pointer = new
            {
                mode = "asOf",
                mart = one.Value.Definition.Code,
                a = new { knownAt = AnalyticsReader.Iso(one.Value.Scope.Known), snapshotHash = one.Value.Hash },
                b = new { knownAt = AnalyticsReader.Iso(two.Value.Scope.Known), snapshotHash = two.Value.Hash },
                sameHash = one.Value.Hash == two.Value.Hash,
            };
        }

        var keys = left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var diff = keys
            .Select(k =>
            {
                var hasA = left.TryGetValue(k, out var l);
                var hasB = right.TryGetValue(k, out var r);
                return new
                {
                    key = k,
                    a = hasA ? (decimal?)l.Value : null,
                    b = hasB ? (decimal?)r.Value : null,
                    delta = (hasB ? r.Value : 0m) - (hasA ? l.Value : 0m),
                    unit = hasA ? l.Unit : r.Unit,
                    status = !hasA ? "ADDED" : !hasB ? "REMOVED" : l.Value == r.Value ? "SAME" : "CHANGED",
                };
            })
            .Where(d => d.status != "SAME").ToList();
        return new MartCompareResponse
        {
            Figures = Ser(new { compared = keys.Count, changed = diff.Count }),
            LineagePointer = Ser(pointer),
            Diff = Ser(diff),
        };
    }

    // ---- shapes ------------------------------------------------------------------------------------------------------------

    private static JsonElement Ser<T>(T value) => JsonSerializer.SerializeToElement(value, Json);

    private static object RunJson(MartRunRow r) => new
    {
        runId = r.RunId,
        mart = r.MartCode,
        entity = r.LegalEntity,
        period = r.Period,
        productCode = r.ProductCode,
        validAt = AnalyticsReader.Iso(r.ValidAt.ToUtcDateTime()),
        knownAt = AnalyticsReader.Iso(r.KnownAt.ToUtcDateTime()),
        status = r.Status,
        snapshotHash = r.SnapshotHash,
        snapshotKey = r.SnapshotKey,
        legalStatus = r.LegalStatus,
        definitionVersion = r.DefinitionVersion,
        figureCount = r.FigureCount,
        createdAt = AnalyticsReader.Iso(r.CreatedAt.ToUtcDateTime()),
        createdBy = r.CreatedBy,
    };

    private static object FigureJson(MartFigureRow f) => new
    {
        key = f.FigureKey,
        metric = f.Metric,
        productCode = f.ProductCode,
        month = f.PeriodMonth,
        dimension = f.Dimension,
        value = f.Value,
        unit = f.Unit,
        sourceCount = f.SourceCount,
    };

    /// <summary>Where the figures came from: the source tables, the as-of scope and the count of source rows (BO-DAT-05 resolves this to row and raw event ids).</summary>
    private static object Lineage(MartRunRow r, MartFigureRow? figure = null) => new
    {
        runId = r.RunId,
        mart = r.MartCode,
        snapshotHash = r.SnapshotHash,
        validAt = AnalyticsReader.Iso(r.ValidAt.ToUtcDateTime()),
        knownAt = AnalyticsReader.Iso(r.KnownAt.ToUtcDateTime()),
        sources = r.MartCode == RiCessionSummaryMart.MartCode
            ? new[] { "rpt_raw.event_ledger" }
            : r.MartCode == ClaimsExperienceMart.MartCode ? new[] { "rpt_conformed.fact_premium", "rpt_conformed.fact_claim_movement" } : new[] { "rpt_conformed.fact_premium" },
        sourceRowCount = figure?.SourceCount ?? r.SourceRowCount,
    };
}
