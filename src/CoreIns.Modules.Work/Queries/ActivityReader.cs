using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Work.Commands;
using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Modules.Work.Domain;
using CoreIns.Modules.Work.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Paging;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Work.Queries;

/// <summary>An assignee on the wire.</summary>
internal sealed record AssigneeView(string Type, string Id);

/// <summary>An activity as the staff inbox sees it (the shape inside <c>activity</c> of the loosely typed contract responses).</summary>
internal sealed record ActivityView
{
    public required Guid Id { get; init; }

    public required string Number { get; init; }

    public required string PatternCode { get; init; }

    public required string Title { get; init; }

    public required string Category { get; init; }

    public required string Priority { get; init; }

    public required string State { get; init; }

    public required ObjectRef PrimaryObject { get; init; }

    public required IReadOnlyList<ObjectRef> SecondaryObjects { get; init; }

    public required AssigneeView Assignee { get; init; }

    public required string QueueCode { get; init; }

    public required Guid QueueId { get; init; }

    public Instant? TargetAt { get; init; }

    public Instant? EscalationAt { get; init; }

    /// <summary>True once the target passed (open) or the activity closed after it.</summary>
    public required bool SlaBreached { get; init; }

    public required Instant CreatedAt { get; init; }

    public required string CreatedBy { get; init; }

    public Instant? ClosedAt { get; init; }

    public string? Outcome { get; init; }

    public string? Note { get; init; }

    public required int RecordVersion { get; init; }

    /// <summary>True when the asking user is the assignee.</summary>
    public required bool AssignedToMe { get; init; }

    /// <summary>True when the asking user could take it from the queue now.</summary>
    public required bool ClaimableByMe { get; init; }

    public static ActivityView From(ActivityRow row, WorkActor actor)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(actor);
        var mine = ActivityEngine.IsMine(row, actor);
        return new ActivityView
        {
            Id = row.ActivityId,
            Number = row.Number,
            PatternCode = row.PatternCode,
            Title = row.Title,
            Category = row.Category,
            Priority = row.Priority,
            State = row.State,
            PrimaryObject = new ObjectRef(Enum.Parse<ModuleCode>(row.PrimaryModule), row.PrimaryType, row.PrimaryId),
            SecondaryObjects = ParseRefs(row.SecondaryRefs),
            Assignee = new AssigneeView(row.AssigneeType, row.AssigneeId),
            QueueCode = row.QueueCode,
            QueueId = QueueIds.For(row.QueueCode).Value,
            TargetAt = row.TargetAt,
            EscalationAt = row.EscalationAt,
            SlaBreached = row.BreachedAt is not null,
            CreatedAt = row.CreatedAt,
            CreatedBy = row.CreatedBy,
            ClosedAt = row.ClosedAt,
            Outcome = row.Outcome,
            Note = row.Note,
            RecordVersion = row.RecordVersion,
            AssignedToMe = mine,
            ClaimableByMe = row.State == ActivityRow.Open && row.AssigneeType == ActivityRow.Queue && actor.UserId is not null && actor.CanSee(row.QueueCode),
        };
    }

    public JsonElement ToElement() => JsonSerializer.SerializeToElement(this, SharedKernelJson.Options);

    private static List<ObjectRef> ParseRefs(string json)
    {
        var refs = new List<ObjectRef>();
        using var document = JsonDocument.Parse(json);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            refs.Add(new ObjectRef(Enum.Parse<ModuleCode>(item.GetProperty("module").GetString()!), item.GetProperty("type").GetString()!, item.GetProperty("id").GetString()!));
        }

        return refs;
    }
}

/// <summary>Reads activities for the staff inbox: my tasks, the team queue, one activity, and the gate query.</summary>
internal sealed class ActivityReader(WorkDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, WorkCatalogue catalogue, IClock clock)
{
    private const int DefaultLimit = 25;

    private static readonly string[] States = [ActivityRow.Open, ActivityRow.Completed, ActivityRow.Skipped, ActivityRow.Cancelled];

    /// <summary>
    /// Lists activities. <c>filters</c> is a comma list of <c>key:value</c>: <c>scope</c> (<c>mine</c> default, <c>queue</c> = unassigned in my
    /// queues, <c>team</c> = everything in my queues), <c>state</c> (default OPEN), <c>queue</c>, <c>pattern</c>, <c>overdue</c> (true). An unknown key
    /// or value is refused.
    /// </summary>
    public async Task<Result<ActivityListPage>> ListAsync(Guid? id, string? cursor, int? limit, string? filters, CancellationToken cancellationToken)
    {
        if (!KeysetPaging.TryTake(limit, DefaultLimit, out var take))
        {
            return WorkErrors.Invalid($"limit is outside 1..{KeysetPaging.MaxPageSize}.");
        }

        if (!TryDecodeCursor(cursor, out var after))
        {
            return WorkErrors.Invalid("cursor is malformed.");
        }

        var parsed = ParseFilters(filters);
        if (parsed.IsFailure)
        {
            return parsed.Error!;
        }

        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        var query = VisibleTo(actor, parsed.Value, id);
        if (query.IsFailure)
        {
            return query.Error!;
        }

        var filtered = query.Value;
        if (after is { } key)
        {
            filtered = AfterKey(filtered, key);
        }

        var rows = await Ordered(filtered).Take(take + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var (page, next) = KeysetPaging.Slice(rows, take, CursorOf);
        return new ActivityListPage
        {
            Items = [.. page.Select(r => new ActivityListItem { Activity = ActivityView.From(r, actor).ToElement() })],
            NextCursor = next,
            Limit = take,
        };
    }

    /// <summary>
    /// The ids a bulk operation selects with a filter (same grammar and visibility as the list): at most <paramref name="max"/> + 1, so the
    /// caller can tell the selection is too large.
    /// </summary>
    public async Task<Result<List<Guid>>> SelectIdsAsync(string filters, int max, CancellationToken cancellationToken)
    {
        var parsed = ParseFilters(filters);
        if (parsed.IsFailure)
        {
            return parsed.Error!;
        }

        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        var query = VisibleTo(actor, parsed.Value, null);
        if (query.IsFailure)
        {
            return query.Error!;
        }

        return await Ordered(query.Value).Take(max + 1).Select(a => a.ActivityId).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>wrk.Team.summary</c>: per visible queue the open, due today (UTC day), breached and unassigned counts, and the open and breached
    /// counts per assignee. <paramref name="groups"/> limits the queues (comma list of codes); <paramref name="grouping"/> is <c>queue</c>.
    /// TODO(hardening): "due today" in the legal entity's time zone; assignee display names from the directory.
    /// </summary>
    public async Task<Result<TeamSummaryResponse>> TeamSummaryAsync(string? groups, string? grouping, CancellationToken cancellationToken)
    {
        if (grouping is not null && grouping != "queue")
        {
            return WorkErrors.Invalid("grouping is 'queue'.");
        }

        var wanted = groups?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (wanted is { Length: > 50 } || (wanted?.Any(g => g.Length > 64) ?? false))
        {
            return WorkErrors.Invalid("groups is a comma list of at most 50 queue codes.");
        }

        var snapshot = await catalogue.GetAsync(cancellationToken).ConfigureAwait(false);
        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        var queues = actor.VisibleQueues.Where(q => wanted is null || wanted.Contains(q, StringComparer.Ordinal)).ToList();
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var now = clock.Now.ToUtcDateTime();
        var dayStart = Instant.FromUtcDateTime(now.Date);
        var dayEnd = Instant.FromUtcDateTime(now.Date.AddTicks(TimeSpan.TicksPerDay));
        var open = db.Activities.AsNoTracking().Where(a => a.LegalEntityId == legalEntity && a.State == ActivityRow.Open && queues.Contains(a.QueueCode));
        var perQueue = await open.GroupBy(a => a.QueueCode).Select(g => new
        {
            Queue = g.Key,
            Open = g.Count(),
            DueToday = g.Count(a => a.TargetAt != null && a.TargetAt >= dayStart && a.TargetAt < dayEnd),
            Breached = g.Count(a => a.BreachedAt != null),
            Unassigned = g.Count(a => a.AssigneeType == ActivityRow.Queue),
        }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var perAssignee = await open.Where(a => a.AssigneeType == ActivityRow.User).GroupBy(a => new { a.QueueCode, a.AssigneeId })
            .Select(g => new { g.Key.QueueCode, g.Key.AssigneeId, Open = g.Count(), Breached = g.Count(a => a.BreachedAt != null) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var rows = queues.Select(code =>
        {
            var q = perQueue.FirstOrDefault(p => p.Queue == code);
            return new
            {
                queueCode = code,
                title = snapshot.Queue(code)?.Title ?? code,
                open = q?.Open ?? 0,
                dueToday = q?.DueToday ?? 0,
                breached = q?.Breached ?? 0,
                unassigned = q?.Unassigned ?? 0,
                assignees = perAssignee.Where(p => p.QueueCode == code).OrderByDescending(p => p.Open).ThenBy(p => p.AssigneeId, StringComparer.Ordinal)
                    .Select(p => new { userId = p.AssigneeId, open = p.Open, breached = p.Breached }).ToList(),
            };
        }).ToList();
        var counts = new
        {
            asOf = clock.Now,
            totals = new { open = rows.Sum(r => r.open), dueToday = rows.Sum(r => r.dueToday), breached = rows.Sum(r => r.breached), unassigned = rows.Sum(r => r.unassigned) },
            queues = rows,
        };
        return new TeamSummaryResponse { Counts = JsonSerializer.SerializeToElement(counts, SharedKernelJson.Options) };
    }

    private static string CursorOf(ActivityRow row) => KeysetPaging.Encode(
        KeysetPaging.Part(row.Priority == "HIGH" ? 0 : 1), row.TargetAt is null ? "1" : "0", KeysetPaging.Part(row.TargetAt?.ToUtcDateTime().Ticks ?? 0),
        KeysetPaging.Part(row.CreatedAt.ToUtcDateTime().Ticks), KeysetPaging.Part(row.ActivityId));

    /// <summary>The inbox sort key of the last row served: priority rank, "no target" flag, target, created, id.</summary>
    private sealed record SortKey(int Rank, bool NoTarget, Instant? TargetAt, Instant CreatedAt, Guid Id);

    private static bool TryDecodeCursor(string? cursor, out SortKey? key)
    {
        key = null;
        if (!KeysetPaging.TryDecode(cursor, 5, out var parts))
        {
            return false;
        }

        if (parts is null)
        {
            return true;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var rank) || rank > 1 || parts[1] is not ("0" or "1")
            || !KeysetPaging.TryLong(parts[2], out var target) || !KeysetPaging.TryLong(parts[3], out var created) || !Guid.TryParse(parts[4], out var id)
            || !InRange(created) || (parts[1] == "0" && !InRange(target)))
        {
            return false;
        }

        var noTarget = parts[1] == "1";
        key = new SortKey(
            rank, noTarget, noTarget ? null : Instant.FromUtcDateTime(new DateTime(target, DateTimeKind.Utc)),
            Instant.FromUtcDateTime(new DateTime(created, DateTimeKind.Utc)), id);
        return true;

        static bool InRange(long ticks) => ticks is >= 0 and <= 3_155_378_975_999_999_999;
    }

    /// <summary>The rows strictly after the key in <see cref="Ordered"/> order.</summary>
    private static IQueryable<ActivityRow> AfterKey(IQueryable<ActivityRow> query, SortKey key)
    {
        var rank = key.Rank;
        var created = key.CreatedAt;
        var id = key.Id;
        if (key.NoTarget)
        {
            // Rows without a target sort last within a priority: only those can follow.
            return query.Where(a => (a.Priority == "HIGH" ? 0 : 1) > rank
                || ((a.Priority == "HIGH" ? 0 : 1) == rank && a.TargetAt == null && (a.CreatedAt > created || (a.CreatedAt == created && a.ActivityId.CompareTo(id) > 0))));
        }

        var target = key.TargetAt!.Value;
        return query.Where(a => (a.Priority == "HIGH" ? 0 : 1) > rank
            || ((a.Priority == "HIGH" ? 0 : 1) == rank
                && (a.TargetAt == null
                    || a.TargetAt > target
                    || (a.TargetAt == target && (a.CreatedAt > created || (a.CreatedAt == created && a.ActivityId.CompareTo(id) > 0))))));
    }

    /// <summary>One activity the asker may see; anything else is not found.</summary>
    public async Task<Result<ActivityGetPage>> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var activityId))
        {
            return WorkErrors.NotFound();
        }

        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var row = await db.Activities.AsNoTracking().SingleOrDefaultAsync(a => a.ActivityId == activityId && a.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (row is null || !ActivityEngine.IsVisible(row, actor))
        {
            return WorkErrors.NotFound();
        }

        return new ActivityGetPage { Items = [new ActivityGetItem { Activity = ActivityView.From(row, actor).ToElement() }], NextCursor = null, Limit = 1 };
    }

    /// <summary>The gate query: open gate-blocking activities on an object (<c>MOD/Type/id</c>).</summary>
    public async Task<Result<ActivityBlockingStatusResponse>> BlockingStatusAsync(string? objectRef, CancellationToken cancellationToken)
    {
        var parts = objectRef?.Split('/', 3);
        if (parts is not { Length: 3 } || !Enum.TryParse<ModuleCode>(parts[0], ignoreCase: false, out var module) || parts[1].Length is 0 or > 128 || parts[2].Length is 0 or > 200)
        {
            return WorkErrors.Invalid("objectRef is MOD/Type/id.");
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var moduleCode = module.ToString();
        var open = await db.Activities.AsNoTracking()
            .Where(a => a.LegalEntityId == legalEntity && a.State == ActivityRow.Open && a.GateBlocking
                && a.PrimaryModule == moduleCode && a.PrimaryType == parts[1] && a.PrimaryId == parts[2])
            .OrderBy(a => a.CreatedAt).Select(a => new { id = a.ActivityId, number = a.Number, patternCode = a.PatternCode }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new ActivityBlockingStatusResponse { OpenGateBlockingActivities = JsonSerializer.SerializeToElement(open, SharedKernelJson.Options) };
    }

    private static IOrderedQueryable<ActivityRow> Ordered(IQueryable<ActivityRow> query) =>
        query.OrderBy(a => a.Priority == "HIGH" ? 0 : 1).ThenBy(a => a.TargetAt == null).ThenBy(a => a.TargetAt).ThenBy(a => a.CreatedAt).ThenBy(a => a.ActivityId);

    private Result<IQueryable<ActivityRow>> VisibleTo(WorkActor actor, InboxFilters filters, Guid? id)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var me = actor.UserId ?? "\u0000none"; // a service has no inbox: the sentinel matches no row
        var queues = actor.VisibleQueues;
        IQueryable<ActivityRow> query = db.Activities.AsNoTracking().Where(a => a.LegalEntityId == legalEntity);
        if (id is { } one)
        {
            query = query.Where(a => a.ActivityId == one);
        }

        if (filters.Queue is not null && !queues.Contains(filters.Queue, StringComparer.Ordinal))
        {
            // A queue the asker cannot see reads as an empty one.
            return Result.Success(query.Where(_ => false));
        }

        query = filters.Scope switch
        {
            "queue" => query.Where(a => a.AssigneeType == ActivityRow.Queue && queues.Contains(a.QueueCode)),
            "team" => query.Where(a => queues.Contains(a.QueueCode)),
            _ => query.Where(a => a.AssigneeType == ActivityRow.User && a.AssigneeId == me),
        };
        if (filters.Queue is not null)
        {
            query = query.Where(a => a.QueueCode == filters.Queue);
        }

        if (filters.State is not null)
        {
            query = query.Where(a => a.State == filters.State);
        }

        if (filters.Pattern is not null)
        {
            query = query.Where(a => a.PatternCode == filters.Pattern);
        }

        if (filters.Overdue)
        {
            query = query.Where(a => a.BreachedAt != null);
        }

        return Result.Success(query);
    }

    private static Result<InboxFilters> ParseFilters(string? filters)
    {
        var scope = "mine";
        string? state = ActivityRow.Open;
        string? queue = null;
        string? pattern = null;
        var overdue = false;
        if (!string.IsNullOrWhiteSpace(filters))
        {
            foreach (var token in filters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var colon = token.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0 || colon == token.Length - 1)
                {
                    return WorkErrors.Invalid($"filter '{token}' is not key:value.");
                }

                var (key, value) = (token[..colon], token[(colon + 1)..]);
                switch (key)
                {
                    case "scope" when value is "mine" or "queue" or "team":
                        scope = value;
                        break;
                    case "state" when value == "ALL":
                        state = null;
                        break;
                    case "state" when States.Contains(value):
                        state = value;
                        break;
                    case "queue" when value.Length <= 64:
                        queue = value;
                        break;
                    case "pattern" when value.Length <= 128:
                        pattern = value;
                        break;
                    case "overdue" when value is "true" or "false":
                        overdue = value == "true";
                        break;
                    default:
                        return WorkErrors.Invalid($"filter '{token}' is not supported.");
                }
            }
        }

        return new InboxFilters(scope, state, queue, pattern, overdue);
    }

    private sealed record InboxFilters(string Scope, string? State, string? Queue, string? Pattern, bool Overdue);
}
