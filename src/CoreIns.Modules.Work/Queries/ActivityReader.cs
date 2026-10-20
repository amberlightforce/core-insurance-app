using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Work.Commands;
using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Modules.Work.Domain;
using CoreIns.Modules.Work.Persistence;
using CoreIns.Platform.Context;
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
internal sealed class ActivityReader(WorkDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
{
    private const int DefaultLimit = 25;
    private const int MaxLimit = 200;

    private static readonly string[] States = [ActivityRow.Open, ActivityRow.Completed, ActivityRow.Skipped, ActivityRow.Cancelled];

    /// <summary>
    /// Lists activities. <c>filters</c> is a comma list of <c>key:value</c>: <c>scope</c> (<c>mine</c> default, <c>queue</c> = unassigned in my
    /// queues, <c>team</c> = everything in my queues), <c>state</c> (default OPEN), <c>queue</c>, <c>pattern</c>, <c>overdue</c> (true). An unknown key
    /// or value is refused.
    /// </summary>
    public async Task<Result<ActivityListPage>> ListAsync(Guid? id, string? cursor, int? limit, string? filters, CancellationToken cancellationToken)
    {
        // TODO(hardening): keyset cursor; an offset cursor can skip or repeat rows while the inbox changes.
        var skip = 0;
        if (limit is < 1 or > MaxLimit || (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out skip) || skip < 0)))
        {
            return WorkErrors.Invalid($"cursor is malformed or limit is outside 1..{MaxLimit}.");
        }

        var parsed = ParseFilters(filters);
        if (parsed.IsFailure)
        {
            return parsed.Error!;
        }

        var actor = WorkActor.From(context);
        var query = VisibleTo(actor, parsed.Value, id);
        if (query.IsFailure)
        {
            return query.Error!;
        }

        var take = limit ?? DefaultLimit;
        var rows = await Ordered(query.Value).Skip(skip).Take(take + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new ActivityListPage
        {
            Items = [.. rows.Take(take).Select(r => new ActivityListItem { Activity = ActivityView.From(r, actor).ToElement() })],
            NextCursor = rows.Count > take ? (skip + take).ToString(CultureInfo.InvariantCulture) : null,
            Limit = take,
        };
    }

    /// <summary>One activity the asker may see; anything else is not found.</summary>
    public async Task<Result<ActivityGetPage>> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var activityId))
        {
            return WorkErrors.NotFound();
        }

        var actor = WorkActor.From(context);
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
