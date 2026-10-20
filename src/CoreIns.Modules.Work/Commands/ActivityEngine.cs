using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Work.Contracts.Events;
using CoreIns.Modules.Work.Domain;
using CoreIns.Modules.Work.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Work.Commands;

/// <summary>What creates an activity: a pattern, the primary object, the module that raises it and why.</summary>
internal sealed record CreateActivityInput(
    string PatternCode, ObjectRef Primary, IReadOnlyList<ObjectRef> Secondary, ModuleCode Raising, string? CauseRef);

/// <summary>Who an assignment names.</summary>
/// <param name="Type">USER or QUEUE.</param>
/// <param name="Id">User id or queue code.</param>
internal sealed record AssignTarget(string Type, string Id);

/// <summary>
/// The Work core's rules (PRD-13 subset a staff inbox needs): create from a pattern, claim and assign, complete / skip / cancel, with
/// the SLA clock and the events. One scoped instance per unit of work; every mutation runs inside the caller's transaction (command
/// pipeline, event handler or sweeper) so the row and its events commit together.
/// </summary>
internal sealed class ActivityEngine(WorkDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IEventPublisher events)
{
    private const int MaxSecondaryObjects = 20;
    private const int MaxFieldLength = 2000;

    private LegalEntityId LegalEntity =>
        legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    /// <summary>Creates an activity in its pattern's queue. A second open activity with the same pattern, object and cause is a duplicate.</summary>
    public async Task<Result<ActivityRow>> CreateAsync(CreateActivityInput input, bool returnExistingOnDuplicate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var pattern = WorkCatalog.Pattern(input.PatternCode);
        if (pattern is null || !pattern.RaisingModules.Contains(input.Raising))
        {
            // Fail closed: an unknown pattern, or a module that may not raise it, never creates work.
            return WorkErrors.Of(WorkErrors.PatternNotApplicable, $"The pattern '{input.PatternCode}' is not available to {input.Raising}.");
        }

        if (input.Secondary.Count > MaxSecondaryObjects)
        {
            return WorkErrors.Of(WorkErrors.LimitExceeded, $"At most {MaxSecondaryObjects} secondary objects are allowed.");
        }

        if (input.CauseRef is { Length: > 200 } || (input.CauseRef is not null && input.CauseRef.Any(char.IsControl)))
        {
            return WorkErrors.Invalid("causeRef has at most 200 characters and no control characters.");
        }

        var legalEntity = LegalEntity;
        var cause = input.CauseRef?.Trim() ?? string.Empty;
        var existing = await db.Activities.AsNoTracking().FirstOrDefaultAsync(a =>
            a.LegalEntityId == legalEntity && a.State == ActivityRow.Open && a.PatternCode == pattern.Code
            && a.PrimaryModule == input.Primary.Module.ToString() && a.PrimaryType == input.Primary.Type && a.PrimaryId == input.Primary.Id && a.CauseRef == cause,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (returnExistingOnDuplicate)
            {
                return existing;
            }

            return WorkErrors.Of(WorkErrors.Duplicate, $"The open activity {existing.Number} already exists for this object and cause.");
        }

        var now = clock.Now;
        var id = Guid.CreateVersion7();
        var row = new ActivityRow
        {
            ActivityId = id,
            LegalEntityId = legalEntity,
            Number = $"ACT-{now.UtcDate.Value:yyyyMMdd}-{id.ToString("N", CultureInfo.InvariantCulture)[^8..].ToUpperInvariant()}",
            PatternCode = pattern.Code,
            PatternVersion = pattern.Version,
            Title = pattern.Title,
            Category = pattern.Category,
            Priority = pattern.Priority,
            SlaPolicy = pattern.SlaPolicy,
            GateBlocking = pattern.GateBlocking,
            State = ActivityRow.Open,
            PrimaryModule = input.Primary.Module.ToString(),
            PrimaryType = input.Primary.Type,
            PrimaryId = input.Primary.Id,
            SecondaryRefs = JsonSerializer.Serialize(input.Secondary.Select(s => new { module = s.Module.ToString(), type = s.Type, id = s.Id }), SharedKernelJson.Options),
            SubjectPartyId = PartyOf(input.Primary) ?? input.Secondary.Select(PartyOf).FirstOrDefault(p => p is not null),
            RaisingModule = input.Raising.ToString(),
            CauseRef = cause,
            AssigneeType = ActivityRow.Queue,
            AssigneeId = pattern.Queue,
            QueueCode = pattern.Queue,
            CreatedAt = now,
            CreatedBy = context.Actor.ToString(),
            TargetAt = now.Plus(TimeSpan.FromMinutes(pattern.TargetMinutes)),
            EscalationAt = now.Plus(TimeSpan.FromMinutes(pattern.EscalationMinutes)),
            UpdatedAt = now,
        };
        db.Activities.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        events.Publish(new OutgoingEvent(EventDescriptor.From(ActivityCreatedV1.Descriptor), "Activity", id.ToString("D"), new ActivityCreatedV1
        {
            ActivityNumber = ActivityNumber.Parse(row.Number),
            PatternCode = row.PatternCode,
            PatternVersion = row.PatternVersion,
            Category = row.Category,
            PrimaryObjectRef = input.Primary,
            SecondaryRefs = input.Secondary,
            Priority = row.Priority,
            TargetAt = row.TargetAt,
            EscalationAt = row.EscalationAt,
            CreatedByModule = input.Raising,
            CauseRef = input.CauseRef,
            Mandatory = true,
            GateBlocking = row.GateBlocking,
        }, Keys(id)));
        return row;
    }

    /// <summary>
    /// Assigns an activity. <paramref name="target"/> null means "me" (claim): allowed from the actor's own queue or when already
    /// theirs. Naming another user, or returning to a queue, is for the assignee or a manager.
    /// </summary>
    public async Task<Result<ActivityRow>> AssignAsync(Guid id, AssignTarget? target, CancellationToken cancellationToken)
    {
        var actor = WorkActor.From(context);
        var row = await LoadAsync(id, actor, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return WorkErrors.NotFound();
        }

        if (row.State != ActivityRow.Open)
        {
            return DomainError.Of(ModuleCode.WRK, PlatformErrors.InvalidStateTransition, "A closed activity cannot be assigned.");
        }

        var mine = IsMine(row, actor);
        if (target is null || (target.Type == ActivityRow.User && actor.UserId is not null && target.Id == actor.UserId))
        {
            if (actor.UserId is null)
            {
                return WorkErrors.Of(WorkErrors.Permission, "Only a signed-in user can take an activity.");
            }

            if (!mine && row.AssigneeType == ActivityRow.User && !actor.IsManager)
            {
                return WorkErrors.Of(WorkErrors.Permission, "The activity is assigned to someone else.");
            }

            Assign(row, ActivityRow.User, actor.UserId);
        }
        else if (target.Type == ActivityRow.User)
        {
            if (!actor.IsManager)
            {
                return WorkErrors.Of(WorkErrors.Permission, "Only a manager can assign an activity to someone else.");
            }

            if (target.Id is not { Length: >= 1 and <= 200 } || target.Id.Any(char.IsControl))
            {
                return WorkErrors.Of(WorkErrors.TargetIneligible, "The user id is not valid.");
            }

            // TODO(hardening): check that the target user exists, is active, and may see the activity's queue (needs the PLT user directory).
            Assign(row, ActivityRow.User, target.Id);
        }
        else if (target.Type == ActivityRow.Queue)
        {
            if (!mine && !actor.IsManager)
            {
                return WorkErrors.Of(WorkErrors.Permission, "Only the assignee or a manager can return an activity to a queue.");
            }

            if (WorkCatalog.Queue(target.Id) is null)
            {
                return WorkErrors.Of(WorkErrors.TargetIneligible, $"Queue '{target.Id}' does not exist.");
            }

            Assign(row, ActivityRow.Queue, target.Id);
            row.QueueCode = target.Id;
        }
        else
        {
            return WorkErrors.Of(WorkErrors.TargetIneligible, "An activity is assigned to a USER or a QUEUE.");
        }

        return await SaveAsync(row, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Completes, skips or cancels an open activity. Only the assignee may complete or skip it; the assignee or a manager may cancel it.</summary>
    public async Task<Result<ActivityRow>> CloseAsync(Guid id, string newState, string? outcome, string? note, string? reason, CancellationToken cancellationToken)
    {
        var actor = WorkActor.From(context);
        if (note is { Length: > MaxFieldLength } || reason is { Length: > MaxFieldLength } || outcome is { Length: > 64 })
        {
            return WorkErrors.Invalid($"outcome has at most 64 and note and reason at most {MaxFieldLength} characters.");
        }

        if (newState == ActivityRow.Completed && string.IsNullOrWhiteSpace(outcome))
        {
            return WorkErrors.Of(WorkErrors.OutcomeRequired, "Completing an activity needs an outcome code.");
        }

        if (newState != ActivityRow.Completed && string.IsNullOrWhiteSpace(reason))
        {
            return WorkErrors.Of(WorkErrors.NoteRequired, "Skipping or cancelling an activity needs a reason.");
        }

        var row = await LoadAsync(id, actor, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return WorkErrors.NotFound();
        }

        if (row.State != ActivityRow.Open)
        {
            return DomainError.Of(ModuleCode.WRK, PlatformErrors.InvalidStateTransition, "The activity is already closed.");
        }

        var allowed = IsMine(row, actor) || (newState == ActivityRow.Cancelled && actor.IsManager);
        if (!allowed)
        {
            // Unclaimed queue work must be claimed first, so a closed activity always names who decided it.
            return WorkErrors.Of(WorkErrors.Permission, "Take the activity before closing it; only the assignee closes it.");
        }

        var now = clock.Now;
        row.State = newState;
        row.ClosedAt = now;
        row.ClosedBy = context.Actor.ToString();
        row.Outcome = outcome?.Trim();
        row.Note = note?.Trim();
        row.CloseReason = reason?.Trim();
        row.UpdatedAt = now;
        row.RecordVersion++;
        var wasBreached = row.BreachedAt is not null;
        if (!wasBreached && row.TargetAt is { } target && now > target)
        {
            row.BreachedAt = now;
        }

        var saved = await SaveAsync(row, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved;
        }

        if (!wasBreached && row.BreachedAt is not null)
        {
            PublishBreach(row, now);
        }

        PublishClosed(row, now);
        return row;
    }

    /// <summary>Takes the most urgent open activity of the actor's visible queues (all, or the one named) and assigns it to them.</summary>
    public async Task<Result<ActivityRow>> ClaimNextAsync(string? queueCode, CancellationToken cancellationToken)
    {
        var actor = WorkActor.From(context);
        if (actor.UserId is null)
        {
            return WorkErrors.Of(WorkErrors.Permission, "Only a signed-in user can take an activity.");
        }

        var queues = actor.VisibleQueues;
        if (queueCode is not null && !queues.Contains(queueCode, StringComparer.Ordinal))
        {
            // Not visible reads the same as empty: the queue's existence is not disclosed.
            return WorkErrors.Of(WorkErrors.QueueEmpty, "No open activity is waiting in the queue.");
        }

        var legalEntity = LegalEntity;
        var scope = queueCode is null ? queues : [queueCode];
        var candidates = await db.Activities
            .Where(a => a.LegalEntityId == legalEntity && a.State == ActivityRow.Open && a.AssigneeType == ActivityRow.Queue && scope.Contains(a.QueueCode))
            .OrderBy(a => a.Priority == "HIGH" ? 0 : 1).ThenBy(a => a.TargetAt == null).ThenBy(a => a.TargetAt).ThenBy(a => a.CreatedAt).ThenBy(a => a.ActivityId)
            .Take(5).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in candidates)
        {
            Assign(row, ActivityRow.User, actor.UserId);
            var saved = await SaveAsync(row, cancellationToken).ConfigureAwait(false);
            if (saved.IsSuccess)
            {
                return saved;
            }

            // Somebody claimed it first: forget the stale copy and try the next one.
            db.Entry(row).State = EntityState.Detached;
        }

        return WorkErrors.Of(WorkErrors.QueueEmpty, "No open activity is waiting in the queue.");
    }

    /// <summary>Publishes SLABreached for an open activity whose target passed and marks it, once (the sweeper's unit of work).</summary>
    public async Task<bool> BreachAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await db.Activities.SingleOrDefaultAsync(a => a.ActivityId == id, cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        if (row is null || row.State != ActivityRow.Open || row.BreachedAt is not null || row.TargetAt is not { } target || now <= target)
        {
            return false;
        }

        row.BreachedAt = now;
        row.UpdatedAt = now;
        row.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        PublishBreach(row, now);
        return true;
    }

    private async Task<ActivityRow?> LoadAsync(Guid id, WorkActor actor, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var row = await db.Activities.SingleOrDefaultAsync(a => a.ActivityId == id && a.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        // An activity the actor may not see does not exist for them.
        return row is not null && IsVisible(row, actor) ? row : null;
    }

    internal static bool IsMine(ActivityRow row, WorkActor actor) =>
        actor.UserId is not null && row.AssigneeType == ActivityRow.User && string.Equals(row.AssigneeId, actor.UserId, StringComparison.Ordinal);

    internal static bool IsVisible(ActivityRow row, WorkActor actor) => IsMine(row, actor) || actor.CanSee(row.QueueCode);

    private void Assign(ActivityRow row, string type, string id)
    {
        row.AssigneeType = type;
        row.AssigneeId = id;
        row.UpdatedAt = clock.Now;
        row.RecordVersion++;
        // TODO(hardening): publish ActivityAssigned (needs wrk user/group ids; assignee ids are directory strings today).
    }

    private async Task<Result<ActivityRow>> SaveAsync(ActivityRow row, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return WorkErrors.Of(WorkErrors.Stale, "The activity changed meanwhile. Load the newer version and try again.");
        }

        return row;
    }

    private void PublishClosed(ActivityRow row, Instant now)
    {
        var id = row.ActivityId.ToString("D");
        var elapsed = Minutes(row.CreatedAt, now);
        var primary = new ObjectRef(Enum.Parse<ModuleCode>(row.PrimaryModule), row.PrimaryType, row.PrimaryId);
        switch (row.State)
        {
            case ActivityRow.Completed:
                events.Publish(new OutgoingEvent(EventDescriptor.From(ActivityCompletedV1.Descriptor), "Activity", id, new ActivityCompletedV1
                {
                    OutcomeCode = row.Outcome!,
                    ClosedAt = now,
                    ClosedBy = ToActor(context.Actor),
                    PrimaryObjectRef = primary,
                    PatternCode = row.PatternCode,
                    ElapsedMinutes = elapsed,
                    SlaStatus = row.BreachedAt is null ? "MET" : "BREACHED",
                }, Keys(row.ActivityId)));
                break;
            case ActivityRow.Skipped:
                events.Publish(new OutgoingEvent(EventDescriptor.From(ActivitySkippedV1.Descriptor), "Activity", id,
                    new ActivitySkippedV1 { ReasonCode = StaffDecision }, Keys(row.ActivityId)));
                break;
            case ActivityRow.Cancelled:
                events.Publish(new OutgoingEvent(EventDescriptor.From(ActivityCancelledV1.Descriptor), "Activity", id,
                    new ActivityCancelledV1 { ReasonCode = StaffDecision, Actor = ToActor(context.Actor) }, Keys(row.ActivityId)));
                break;
        }
    }

    private void PublishBreach(ActivityRow row, Instant now) =>
        events.Publish(new OutgoingEvent(EventDescriptor.From(SLABreachedV1.Descriptor), "Activity", row.ActivityId.ToString("D"), new SLABreachedV1
        {
            SlaPolicy = row.SlaPolicy,
            BreachMinutes = Minutes(row.TargetAt!.Value, now),
            PatternCode = row.PatternCode,
        }, Keys(row.ActivityId)));

    /// <summary>Whole minutes from <paramref name="from"/> to <paramref name="to"/>, never negative (integer ticks: no floating point).</summary>
    internal static int Minutes(Instant from, Instant to) => (int)Math.Clamp((to - from).Ticks / TimeSpan.TicksPerMinute, 0L, (long)int.MaxValue);

    private static BusinessKeys Keys(Guid activityId) => BusinessKeys.Empty.With("activityId", activityId.ToString("D"));

    /// <summary>The reason staff typed is free text and stays in the row; the event carries a code.</summary>
    private const string StaffDecision = "STAFF_DECISION";

    private static Actor ToActor(ActorRef actor) => new()
    {
        Kind = actor.Kind switch
        {
            ActorKind.User => Actor.KindValue.User,
            ActorKind.AiAgent => Actor.KindValue.AiAgent,
            _ => Actor.KindValue.Service,
        },
        Id = actor.Id,
    };

    private static Guid? PartyOf(ObjectRef reference) =>
        reference.Module == ModuleCode.PTY && string.Equals(reference.Type, "Party", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(reference.Id, out var party) ? party : null;
}
