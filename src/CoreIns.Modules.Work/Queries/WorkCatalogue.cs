using System.Text.Json;
using CoreIns.Modules.Work.Domain;
using CoreIns.Modules.Work.Persistence;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Work.Queries;

/// <summary>
/// The per host cache of the catalogue (BO-WRK-03). Entries live for <see cref="TimeToLiveMilliseconds"/>; the admin write on this host
/// drops them at once, other hosts catch up within the time to live.
/// </summary>
internal sealed class CatalogueCache
{
    public const long TimeToLiveMilliseconds = 30_000;

    private sealed record Entry(CatalogueSnapshot Snapshot, long LoadedAt);

    private Entry? _entry;

    public CatalogueSnapshot? TryGet()
    {
        var entry = Volatile.Read(ref _entry);
        return entry is not null && Environment.TickCount64 - entry.LoadedAt < TimeToLiveMilliseconds ? entry.Snapshot : null;
    }

    public void Set(CatalogueSnapshot snapshot) => Volatile.Write(ref _entry, new Entry(snapshot, Environment.TickCount64));

    public void Invalidate() => Volatile.Write(ref _entry, null);
}

/// <summary>Reads the queue and pattern tables through <see cref="CatalogueCache"/>.</summary>
internal sealed class WorkCatalogue(WorkDbContext db, CatalogueCache cache, RequestContext context)
{
    public async Task<CatalogueSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGet() is { } cached)
        {
            return cached;
        }

        var snapshot = await LoadAsync(db, cancellationToken).ConfigureAwait(false);
        cache.Set(snapshot);
        return snapshot;
    }

    /// <summary>The asking actor with the catalogue they are judged against.</summary>
    public async Task<WorkActor> ActorAsync(CancellationToken cancellationToken) =>
        WorkActor.From(context, await GetAsync(cancellationToken).ConfigureAwait(false));

    public static async Task<CatalogueSnapshot> LoadAsync(WorkDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var queues = await db.Queues.AsNoTracking().OrderBy(q => q.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        var patterns = await db.Patterns.AsNoTracking().OrderBy(p => p.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new CatalogueSnapshot([.. queues.Select(ToQueue)], [.. patterns.Select(ToPattern)]);
    }

    internal static WorkQueue ToQueue(QueueRow row) => new(row.Code, row.Title, Split(row.Roles));

    internal static ActivityPattern ToPattern(PatternRow row) => new(
        row.Code, row.Version, row.Title, row.Category, row.Priority, row.QueueCode, row.SlaPolicy, row.TargetMinutes, row.EscalationMinutes,
        row.GateBlocking, [.. Split(row.RaisingModules).Select(Enum.Parse<ModuleCode>)], row.SuggestedAssignee, ParseBands(row.AmountBandPercent), row.Active);

    internal static string? SerializeBands(IReadOnlyDictionary<string, int>? bands) =>
        bands is null or { Count: 0 } ? null : JsonSerializer.Serialize(bands.OrderBy(b => b.Key, StringComparer.Ordinal).ToDictionary(b => b.Key, b => b.Value));

    private static Dictionary<string, int>? ParseBands(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Dictionary<string, int>>(json);

    private static string[] Split(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
