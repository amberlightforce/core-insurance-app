using System.Globalization;
using System.Text;
using System.Text.Json;
using CoreIns.Modules.Data.Contracts.Api;
using CoreIns.Modules.Data.Contracts.Events;
using CoreIns.Modules.Data.Persistence;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Modules.Data.Services.Marts;

/// <summary>The sign-off state of a run, derived from the append-only adjustment and sign-off rows (BO-DAT-04).</summary>
internal sealed record MartState(
    IReadOnlyList<MartAdjustmentRow> Adjustments, string AdjustmentsHash, string BoundHash, MartSignOffRow? SignOff, bool SignedOff)
{
    /// <summary>Sum of the adjustment lines per figure key.</summary>
    public IReadOnlyDictionary<string, decimal> Sums { get; } = Adjustments.GroupBy(a => a.FigureKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount), StringComparer.Ordinal);
}

/// <summary>
/// Adjust, sign-off and export of mart runs (BO-DAT-04). The stored figures are never changed: an adjustment is a separate line with reason and
/// evidence, and the adjusted value is the figure plus the sum of its lines. A sign-off binds to the run's snapshot hash plus the adjustments
/// hash, so a later adjustment invalidates it; the person who ran or adjusted the run cannot sign it off. Export writes signed-off figures only.
/// </summary>
internal sealed class MartGovernance(DataDbContext db, IClock clock, IHostEnvironment environment, IEventPublisher events)
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The state of a run now.</summary>
    public static async Task<MartState> StateAsync(DataDbContext db, MartRunRow run, CancellationToken ct)
    {
        var adjustments = await db.MartAdjustments.AsNoTracking().Where(a => a.RunId == run.RunId).ToListAsync(ct).ConfigureAwait(false);
        adjustments = [.. adjustments.OrderBy(a => a.CreatedAt).ThenBy(a => a.AdjustmentId)];
        var adjustmentsHash = AdjustmentsHash(adjustments);
        var bound = BoundHash(run.SnapshotHash, adjustmentsHash);
        var signOffs = await db.MartSignOffs.AsNoTracking().Where(s => s.RunId == run.RunId).ToListAsync(ct).ConfigureAwait(false);
        var latest = signOffs.OrderByDescending(s => s.SignedAt).ThenByDescending(s => s.SignOffId).FirstOrDefault();
        return new MartState(adjustments, adjustmentsHash, bound, latest, latest is not null && latest.BoundHash == bound);
    }

    public static string AdjustmentsHash(IEnumerable<MartAdjustmentRow> ordered) =>
        Sha256Hash.ComputeUtf8(string.Join('\n', ordered.Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.AdjustmentId:D}|{a.FigureKey}|{a.Amount:0.0000}|{a.Currency}|{a.CreatedBy}")))).Value;

    public static string BoundHash(string snapshotHash, string adjustmentsHash) => Sha256Hash.ComputeUtf8(snapshotHash + "|" + adjustmentsHash).Value;

    private static DomainError NotFound() => DomainError.Of(ModuleCode.DAT, "NOT-FOUND", "The mart run does not exist.");

    private static DomainError Invalid(string message) => DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, message);

    private async Task<MartRunRow?> LoadRunAsync(string legalEntity, Guid? runId, CancellationToken ct) =>
        runId is { } id ? await db.MartRuns.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == id && r.LegalEntity == legalEntity, ct).ConfigureAwait(false) : null;

    // ---- adjust ------------------------------------------------------------------------------------------------------------

    public async Task<Result<MartAdjustResponse>> AdjustAsync(string legalEntity, string actor, MartAdjustRequest request, CancellationToken ct)
    {
        var reason = request.Reason?.Trim();
        var evidence = request.Evidence?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 1000 || string.IsNullOrEmpty(evidence) || evidence.Length > 500)
        {
            return Invalid("reason (up to 1000 characters) and evidence (up to 500 characters) are required.");
        }

        if (request.Amount is not { } amount || amount.Amount == 0m || amount.Currency.Code != "EUR" || amount.Amount != decimal.Round(amount.Amount, 2, MidpointRounding.ToZero))
        {
            return Invalid("amount must be a non-zero EUR amount in cents.");
        }

        if (await LoadRunAsync(legalEntity, request.RunId, ct).ConfigureAwait(false) is not { } run)
        {
            return NotFound();
        }

        var figure = await db.MartFigures.AsNoTracking().FirstOrDefaultAsync(f => f.RunId == run.RunId && f.FigureKey == request.FigureKey, ct).ConfigureAwait(false);
        if (figure is null)
        {
            return DomainError.Of(ModuleCode.DAT, "NOT-FOUND", "The figure does not exist in this run.");
        }

        if (figure.Unit != MartSql.Eur)
        {
            return Invalid("Only money (EUR) figures can be adjusted.");
        }

        var before = await StateAsync(db, run, ct).ConfigureAwait(false);
        var line = new MartAdjustmentRow
        {
            AdjustmentId = Guid.CreateVersion7(), RunId = run.RunId, FigureKey = figure.FigureKey, Amount = amount.Amount, Currency = "EUR",
            Reason = reason, EvidenceRef = evidence, CreatedBy = actor, CreatedAt = clock.Now,
        };
        db.MartAdjustments.Add(line);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var after = await StateAsync(db, run, ct).ConfigureAwait(false);
        return new MartAdjustResponse
        {
            AdjustmentId = line.AdjustmentId,
            RunId = run.RunId,
            FigureKey = figure.FigureKey,
            AdjustedValue = Fixed(figure.Value + after.Sums.GetValueOrDefault(figure.FigureKey)),
            AdjustmentsHash = Sha256Hash.Parse(after.AdjustmentsHash),
            SignOffInvalidated = before.SignedOff,
        };
    }

    // ---- sign-off ----------------------------------------------------------------------------------------------------------

    public async Task<Result<MartSignOffResponse>> SignOffAsync(string legalEntity, IReadOnlyList<string> actors, MartSignOffRequest request, CancellationToken ct)
    {
        var comment = request.Comment?.Trim() ?? string.Empty;
        if (comment.Length > 1000)
        {
            return Invalid("comment must be at most 1000 characters.");
        }

        if (await LoadRunAsync(legalEntity, request.RunId, ct).ConfigureAwait(false) is not { } run)
        {
            return NotFound();
        }

        if (RefuseUnverified(run) is { } refusal)
        {
            return refusal;
        }

        var state = await StateAsync(db, run, ct).ConfigureAwait(false);
        if (state.SignedOff)
        {
            return Invalid("The run is already signed off for its current adjustments.");
        }

        // Segregation of duties: whoever ran the mart or added an adjustment cannot sign it off (DAT-ERR-023).
        var preparers = state.Adjustments.Select(a => a.CreatedBy).Append(run.CreatedBy).ToHashSet(StringComparer.Ordinal);
        if (actors.Any(preparers.Contains))
        {
            return DomainError.Of(ModuleCode.DAT, "023", "The signer ran or adjusted this run; a second person must sign it off.");
        }

        var row = new MartSignOffRow
        {
            SignOffId = Guid.CreateVersion7(), RunId = run.RunId, SnapshotHash = run.SnapshotHash, AdjustmentsHash = state.AdjustmentsHash, BoundHash = state.BoundHash,
            Comment = comment, SignedBy = actors[0], SignedAt = clock.Now,
        };
        db.MartSignOffs.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        PublishRegulatory(run, state);
        return new MartSignOffResponse { SignOffId = row.SignOffId, RunId = run.RunId, Status = "SIGNED_OFF", BoundHash = Sha256Hash.Parse(row.BoundHash), SignedBy = row.SignedBy };
    }

    /// <summary>A regulatory mart (legalStatus not NotRegulatory) is handed off with RegulatoryMartPublished; the management marts are not.</summary>
    private void PublishRegulatory(MartRunRow run, MartState state)
    {
        if (run.LegalStatus == MartLegalStatus.NotRegulatory)
        {
            return;
        }

        var valid = DateOnly.FromDateTime(run.ValidAt.ToUtcDateTime());
        var period = AnalyticsReader.TryPeriod(run.Period.Length == 0 ? null : run.Period, out var from, out var to) && from is { } f
            ? new DateRange(new BusinessDate(DateOnly.FromDateTime(f)), to is { } t ? new BusinessDate(DateOnly.FromDateTime(t)) : null)
            : DateRange.Open(new BusinessDate(valid.Year, 1, 1));
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(RegulatoryMartPublishedV1.Descriptor),
            "MartRun",
            run.RunId.ToString("D"),
            new RegulatoryMartPublishedV1
            {
                Mart = run.MartCode, LegalEntity = run.LegalEntity, Period = period, Regime = "UNVERIFIED", TaxonomyVersion = "0.0", RunId = run.RunId,
                ReconciliationStatus = "UNRECONCILED", InputsHash = Sha256Hash.Parse(state.BoundHash),
            },
            BusinessKeys.Empty.With("martRunId", run.RunId.ToString("D")).With("runId", run.RunId.ToString("D"))));
    }

    /// <summary>D-USR-19: a regulatory layout is Unverified; it is not signed off or exported in Production.</summary>
    private DomainError? RefuseUnverified(MartRunRow run) =>
        environment.IsProduction() && run.LegalStatus != MartLegalStatus.NotRegulatory
            ? DomainError.Of(ModuleCode.DAT, "LAYOUT-UNVERIFIED", "The regulatory layout of this mart is Unverified and cannot be signed off or exported in Production.")
            : null;

    // ---- export ------------------------------------------------------------------------------------------------------------

    public async Task<Result<MartExportResponse>> ExportAsync(string legalEntity, MartExportRequest request, CancellationToken ct)
    {
        if (await LoadRunAsync(legalEntity, request.RunId, ct).ConfigureAwait(false) is not { } run)
        {
            return NotFound();
        }

        if (RefuseUnverified(run) is { } refusal)
        {
            return refusal;
        }

        var state = await StateAsync(db, run, ct).ConfigureAwait(false);
        if (!state.SignedOff || state.SignOff is not { } signOff)
        {
            return DomainError.Of(ModuleCode.DAT, "024", "The run is not signed off (or an adjustment came after the sign-off).");
        }

        var figures = await db.MartFigures.AsNoTracking().Where(f => f.RunId == run.RunId).ToListAsync(ct).ConfigureAwait(false);
        figures = [.. figures.OrderBy(f => f.FigureKey, StringComparer.Ordinal)];
        var json = request.Format == MartExportRequest.FormatValue.Json;
        var content = json ? ToJson(run, state, signOff, figures) : ToCsv(run, state, signOff, figures);
        var extension = json ? "json" : "csv";
        return new MartExportResponse
        {
            FileName = $"{run.MartCode.ToLowerInvariant()}-{run.RunId:N}.{extension}",
            ContentType = json ? "application/json" : "text/csv",
            Content = content,
            PackageRef = $"mart-run/{run.RunId:D}/{extension}",
            ManifestHash = Sha256Hash.ComputeUtf8(content),
            SnapshotHash = Sha256Hash.Parse(run.SnapshotHash),
            LegalStatus = run.LegalStatus,
        };
    }

    private static string Fixed(decimal value) => value.ToString("0.0000", CultureInfo.InvariantCulture);

    private static IEnumerable<(string Key, string Value)> Header(MartRunRow run, MartState state, MartSignOffRow signOff) =>
    [
        ("format", "coreins-mart-export/1"),
        ("runId", run.RunId.ToString("D")),
        ("mart", run.MartCode),
        ("legalEntity", run.LegalEntity),
        ("period", run.Period),
        ("productCode", run.ProductCode),
        ("validAt", AnalyticsReader.Iso(run.ValidAt.ToUtcDateTime())),
        ("knownAt", AnalyticsReader.Iso(run.KnownAt.ToUtcDateTime())),
        ("legalStatus", run.LegalStatus),
        ("snapshotHash", run.SnapshotHash),
        ("adjustmentsHash", state.AdjustmentsHash),
        ("boundHash", state.BoundHash),
        ("signedBy", signOff.SignedBy),
        ("signedAt", AnalyticsReader.Iso(signOff.SignedAt.ToUtcDateTime())),
    ];

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

    private static string ToCsv(MartRunRow run, MartState state, MartSignOffRow signOff, List<MartFigureRow> figures)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in Header(run, state, signOff))
        {
            sb.Append("# ").Append(key).Append(": ").Append(value).Append('\n');
        }

        sb.Append("figureKey,metric,productCode,month,dimension,unit,storedValue,adjustment,adjustedValue,sourceCount\n");
        foreach (var f in figures)
        {
            var adjustment = state.Sums.GetValueOrDefault(f.FigureKey);
            sb.Append(Csv(f.FigureKey)).Append(',').Append(Csv(f.Metric)).Append(',').Append(Csv(f.ProductCode)).Append(',').Append(Csv(f.PeriodMonth)).Append(',')
                .Append(Csv(f.Dimension)).Append(',').Append(Csv(f.Unit)).Append(',').Append(Fixed(f.Value)).Append(',').Append(Fixed(adjustment)).Append(',')
                .Append(Fixed(f.Value + adjustment)).Append(',').Append(f.SourceCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return sb.ToString();
    }

    private static string ToJson(MartRunRow run, MartState state, MartSignOffRow signOff, List<MartFigureRow> figures)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteStartObject("header");
            foreach (var (key, value) in Header(run, state, signOff))
            {
                w.WriteString(key, value);
            }

            w.WriteEndObject();
            w.WriteStartArray("figures");
            foreach (var f in figures)
            {
                var adjustment = state.Sums.GetValueOrDefault(f.FigureKey);
                w.WriteStartObject();
                w.WriteString("figureKey", f.FigureKey);
                w.WriteString("metric", f.Metric);
                w.WriteString("productCode", f.ProductCode);
                w.WriteString("month", f.PeriodMonth);
                w.WriteString("dimension", f.Dimension);
                w.WriteString("unit", f.Unit);
                w.WriteString("storedValue", Fixed(f.Value));
                w.WriteString("adjustment", Fixed(adjustment));
                w.WriteString("adjustedValue", Fixed(f.Value + adjustment));
                w.WriteNumber("sourceCount", f.SourceCount);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}

/// <summary>Legal-status values of a mart definition.</summary>
internal static class MartLegalStatus
{
    public const string NotRegulatory = "NotRegulatory";
}
