using System.Text.Json;
using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Modules.Work.Domain;
using CoreIns.Modules.Work.Persistence;
using CoreIns.Modules.Work.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using FluentValidation;

namespace CoreIns.Modules.Work.Commands;

/// <summary>Reads the loosely typed members of the wrk request contracts (<see cref="JsonElement"/>) as the plain values they carry.</summary>
internal static class Loose
{
    /// <summary>A string, or the <c>code</c> / <c>text</c> / <c>id</c> member of an object; null when absent or any other shape.</summary>
    public static string? Text(JsonElement? element)
    {
        if (element is not { } value)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "code", "text", "id" })
            {
                if (value.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.String)
                {
                    return inner.GetString();
                }
            }
        }

        return null;
    }
}

/// <summary><c>wrk.Activity.create</c>: an activity from a pattern.</summary>
internal sealed record CreateActivity(ActivityCreateRequest Request) : ICommand<ActivityCreateResponse>;

internal sealed class CreateActivityValidator : AbstractValidator<CreateActivity>
{
    public CreateActivityValidator()
    {
        RuleFor(c => c.Request.PatternCode).NotEmpty().MaximumLength(128).WithErrorCode("PATTERN_REQUIRED");
        RuleFor(c => c.Request.PrimaryObject).NotNull().WithErrorCode("PRIMARY_OBJECT_REQUIRED");
        RuleFor(c => c.Request.CauseRef).MaximumLength(200).WithErrorCode("CAUSE_TOO_LONG");
    }
}

internal sealed class CreateActivityHandler(ActivityEngine engine) : ICommandHandler<CreateActivity, ActivityCreateResponse>
{
    public async Task<Result<ActivityCreateResponse>> HandleAsync(CreateActivity command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var created = await engine.CreateAsync(
            new CreateActivityInput(request.PatternCode, request.PrimaryObject, request.SecondaryObjects ?? [], request.RaisingModule, request.CauseRef,
                request.SuggestedAssignee, request.AmountBand, request.Overrides),
            returnExistingOnDuplicate: false, cancellationToken).ConfigureAwait(false);
        return created.Map(row => new ActivityCreateResponse
        {
            ActivityId = ActivityId.From(row.ActivityId),
            Number = row.Number,
            TargetAt = row.TargetAt,
            EscalationAt = row.EscalationAt,
            Assignment = new ActivityCreateResponse.AssignmentDetail
            {
                AssigneeType = row.AssigneeType == ActivityRow.User
                    ? ActivityCreateResponse.AssignmentDetail.AssigneeTypeValue.User
                    : ActivityCreateResponse.AssignmentDetail.AssigneeTypeValue.Queue,
                AssigneeId = row.AssigneeId,
            },
        });
    }
}

/// <summary>Audit facts of every activity command: the activity (or the pattern's primary object on create).</summary>
internal sealed class CreateActivityAuditor : ICommandAuditor<CreateActivity, ActivityCreateResponse>
{
    public CommandAuditFacts Describe(CreateActivity command, Result<ActivityCreateResponse>? result) =>
        new()
        {
            ObjectRef = result is { IsSuccess: true } ok ? ObjectRef.For(ModuleCode.WRK, "Activity", ok.Value.ActivityId) : command.Request.PrimaryObject,
            ObjectNumber = result is { IsSuccess: true } done ? done.Value.Number : null,
        };
}

/// <summary><c>wrk.Activity.assign</c>: claim ("me") or assign.</summary>
internal sealed record AssignActivity(ActivityAssignRequest Request) : ICommand<ActivityAssignResponse>;

internal sealed class AssignActivityValidator : AbstractValidator<AssignActivity>
{
    public AssignActivityValidator() => RuleFor(c => c.Request.Id).NotNull().NotEqual(Guid.Empty).WithErrorCode("ACTIVITY_REQUIRED");
}

internal sealed class AssignActivityHandler(ActivityEngine engine, WorkCatalogue catalogue) : ICommandHandler<AssignActivity, ActivityAssignResponse>
{
    public async Task<Result<ActivityAssignResponse>> HandleAsync(AssignActivity command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var target = ParseTarget(request);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var assigned = await engine.AssignAsync(request.Id!.Value, target.Value, cancellationToken).ConfigureAwait(false);
        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        return assigned.Map(row => new ActivityAssignResponse { Assignment = ActivityView.From(row, actor).ToElement() });
    }

    /// <summary>"me" (default) is a claim; <c>{type: USER|QUEUE, id}</c> names a target. "rules" routes by the assignment rules.</summary>
    private static Result<AssignTarget?> ParseTarget(ActivityAssignRequest request)
    {
        if (request.Target is { ValueKind: JsonValueKind.Object } target)
        {
            var type = target.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var id = target.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
            if (type is null || id is null)
            {
                return WorkErrors.Of(WorkErrors.TargetIneligible, "target is {type: USER|QUEUE, id}.");
            }

            return Result.Success<AssignTarget?>(new AssignTarget(type.ToUpperInvariant(), id));
        }

        var rulesOrMe = Loose.Text(request.RulesOrMe);
        if (rulesOrMe is null or "me" or "ME")
        {
            return Result.Success<AssignTarget?>(null);
        }

        if (string.Equals(rulesOrMe, "rules", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Success<AssignTarget?>(new AssignTarget(AssignTarget.Rules, "rules"));
        }

        return WorkErrors.Of(WorkErrors.TargetIneligible, "rulesOrMe is \"me\" or \"rules\".");
    }
}

internal sealed class AssignActivityAuditor : ICommandAuditor<AssignActivity, ActivityAssignResponse>
{
    public CommandAuditFacts Describe(AssignActivity command, Result<ActivityAssignResponse>? result) =>
        new() { ObjectRef = ObjectRef.For(ModuleCode.WRK, "Activity", ActivityId.From(command.Request.Id ?? Guid.Empty)) };
}

/// <summary><c>wrk.Activity.complete</c>.</summary>
internal sealed record CompleteActivity(ActivityCompleteRequest Request) : ICommand<ActivityCompleteResponse>;

internal sealed class CompleteActivityValidator : AbstractValidator<CompleteActivity>
{
    public CompleteActivityValidator() => RuleFor(c => c.Request.Id).NotNull().NotEqual(Guid.Empty).WithErrorCode("ACTIVITY_REQUIRED");
}

internal sealed class CompleteActivityHandler(ActivityEngine engine, WorkCatalogue catalogue) : ICommandHandler<CompleteActivity, ActivityCompleteResponse>
{
    public async Task<Result<ActivityCompleteResponse>> HandleAsync(CompleteActivity command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var closed = await engine.CloseAsync(request.Id!.Value, ActivityRow.Completed, Loose.Text(request.Outcome), Loose.Text(request.Note), Loose.Text(request.Reason), cancellationToken).ConfigureAwait(false);
        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        return closed.Map(row => new ActivityCompleteResponse { Activity = ActivityView.From(row, actor).ToElement() });
    }
}

internal sealed class CompleteActivityAuditor : ICommandAuditor<CompleteActivity, ActivityCompleteResponse>
{
    public CommandAuditFacts Describe(CompleteActivity command, Result<ActivityCompleteResponse>? result) =>
        new() { ObjectRef = ObjectRef.For(ModuleCode.WRK, "Activity", ActivityId.From(command.Request.Id ?? Guid.Empty)) };
}

/// <summary><c>wrk.Activity.skip</c>.</summary>
internal sealed record SkipActivity(ActivitySkipRequest Request) : ICommand<ActivitySkipResponse>;

internal sealed class SkipActivityValidator : AbstractValidator<SkipActivity>
{
    public SkipActivityValidator() => RuleFor(c => c.Request.Id).NotNull().NotEqual(Guid.Empty).WithErrorCode("ACTIVITY_REQUIRED");
}

internal sealed class SkipActivityHandler(ActivityEngine engine, WorkCatalogue catalogue) : ICommandHandler<SkipActivity, ActivitySkipResponse>
{
    public async Task<Result<ActivitySkipResponse>> HandleAsync(SkipActivity command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var closed = await engine.CloseAsync(request.Id!.Value, ActivityRow.Skipped, Loose.Text(request.Outcome), Loose.Text(request.Note), Loose.Text(request.Reason), cancellationToken).ConfigureAwait(false);
        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        return closed.Map(row => new ActivitySkipResponse { Activity = ActivityView.From(row, actor).ToElement() });
    }
}

internal sealed class SkipActivityAuditor : ICommandAuditor<SkipActivity, ActivitySkipResponse>
{
    public CommandAuditFacts Describe(SkipActivity command, Result<ActivitySkipResponse>? result) =>
        new() { ObjectRef = ObjectRef.For(ModuleCode.WRK, "Activity", ActivityId.From(command.Request.Id ?? Guid.Empty)) };
}

/// <summary><c>wrk.Activity.cancel</c>.</summary>
internal sealed record CancelActivity(ActivityCancelRequest Request) : ICommand<ActivityCancelResponse>;

internal sealed class CancelActivityValidator : AbstractValidator<CancelActivity>
{
    public CancelActivityValidator() => RuleFor(c => c.Request.Id).NotNull().NotEqual(Guid.Empty).WithErrorCode("ACTIVITY_REQUIRED");
}

internal sealed class CancelActivityHandler(ActivityEngine engine, WorkCatalogue catalogue) : ICommandHandler<CancelActivity, ActivityCancelResponse>
{
    public async Task<Result<ActivityCancelResponse>> HandleAsync(CancelActivity command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var closed = await engine.CloseAsync(request.Id!.Value, ActivityRow.Cancelled, Loose.Text(request.Outcome), Loose.Text(request.Note), Loose.Text(request.Reason), cancellationToken).ConfigureAwait(false);
        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        return closed.Map(row => new ActivityCancelResponse { Activity = ActivityView.From(row, actor).ToElement() });
    }
}

internal sealed class CancelActivityAuditor : ICommandAuditor<CancelActivity, ActivityCancelResponse>
{
    public CommandAuditFacts Describe(CancelActivity command, Result<ActivityCancelResponse>? result) =>
        new() { ObjectRef = ObjectRef.For(ModuleCode.WRK, "Activity", ActivityId.From(command.Request.Id ?? Guid.Empty)) };
}

/// <summary><c>wrk.Queue.getNext</c>: take the most urgent open activity of my queues.</summary>
internal sealed record GetNextActivity(QueueGetNextRequest Request) : ICommand<QueueGetNextResponse>;

internal sealed class GetNextActivityHandler(ActivityEngine engine, WorkCatalogue catalogue) : ICommandHandler<GetNextActivity, QueueGetNextResponse>
{
    public async Task<Result<QueueGetNextResponse>> HandleAsync(GetNextActivity command, CancellationToken cancellationToken)
    {
        string? queue = null;
        if (command.Request.QueueId is { } queueId)
        {
            queue = QueueIds.CodeOf(await catalogue.GetAsync(cancellationToken).ConfigureAwait(false), queueId);
            if (queue is null)
            {
                return WorkErrors.Of(WorkErrors.QueueEmpty, "No open activity is waiting in the queue.");
            }
        }

        var claimed = await engine.ClaimNextAsync(queue, cancellationToken).ConfigureAwait(false);
        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        return claimed.Map(row => new QueueGetNextResponse { AssignedActivity = ActivityView.From(row, actor).ToElement() });
    }
}

internal sealed class GetNextActivityAuditor : ICommandAuditor<GetNextActivity, QueueGetNextResponse>
{
    public CommandAuditFacts Describe(GetNextActivity command, Result<QueueGetNextResponse>? result) =>
        new() { ObjectRef = new ObjectRef(ModuleCode.WRK, "Queue", command.Request.QueueId is { } q ? q.Value.ToString("D") : "all") };
}

/// <summary><c>wrk.Activity.update</c>: edit title, priority, due date or note under optimistic concurrency.</summary>
internal sealed record UpdateActivity(ActivityUpdateRequest Request) : ICommand<ActivityUpdateResponse>;

internal sealed class UpdateActivityValidator : AbstractValidator<UpdateActivity>
{
    public UpdateActivityValidator() => RuleFor(c => c.Request.Id).NotNull().NotEqual(Guid.Empty).WithErrorCode("ACTIVITY_REQUIRED");
}

internal sealed class UpdateActivityHandler(ActivityEngine engine, WorkCatalogue catalogue) : ICommandHandler<UpdateActivity, ActivityUpdateResponse>
{
    private const int MaxTitle = 200;
    private const int MaxText = 2000;

    public async Task<Result<ActivityUpdateResponse>> HandleAsync(UpdateActivity command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var changes = ParseFields(request.Fields);
        if (changes.IsFailure)
        {
            return changes.Error!;
        }

        if (request.RecordVersion is not { ValueKind: JsonValueKind.Number } version || !version.TryGetInt32(out var recordVersion) || recordVersion < 1)
        {
            return WorkErrors.Invalid("recordVersion is the current version of the activity (a positive whole number).");
        }

        // TODO(hardening): keep the change history (old and new values, reason) on the activity; today the audit record is all there is.
        if (Loose.Text(request.Reason) is { Length: > MaxText })
        {
            return WorkErrors.Invalid($"reason has at most {MaxText} characters.");
        }

        var updated = await engine.UpdateAsync(request.Id!.Value, changes.Value, recordVersion, cancellationToken).ConfigureAwait(false);
        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        return updated.Map(row => new ActivityUpdateResponse { Activity = ActivityView.From(row, actor).ToElement() });
    }

    private static Result<ActivityFieldChanges> ParseFields(JsonElement? fields)
    {
        if (fields is not { ValueKind: JsonValueKind.Object } body)
        {
            return WorkErrors.Invalid("fields is an object with title, priority, dueAt or note.");
        }

        string? title = null;
        string? priority = null;
        Instant? dueAt = null;
        var noteSet = false;
        string? note = null;
        var any = false;
        foreach (var property in body.EnumerateObject())
        {
            any = true;
            var text = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            switch (property.Name)
            {
                case "title" when text is { } t && !string.IsNullOrWhiteSpace(t) && t.Trim().Length <= MaxTitle && !t.Any(char.IsControl):
                    title = t.Trim();
                    break;
                case "priority" when text is "NORMAL" or "HIGH":
                    priority = text;
                    break;
                case "dueAt" when Instant.TryParse(text, out var parsed):
                    dueAt = parsed;
                    break;
                case "note" when property.Value.ValueKind == JsonValueKind.Null || text is { Length: <= MaxText }:
                    noteSet = true;
                    note = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    break;
                default:
                    return WorkErrors.Invalid($"field '{property.Name}' is not editable or its value is not valid (title, priority NORMAL or HIGH, dueAt instant, note).");
            }
        }

        return any ? new ActivityFieldChanges(title, priority, dueAt, noteSet, note) : WorkErrors.Invalid("fields names nothing to change.");
    }
}

internal sealed class UpdateActivityAuditor : ICommandAuditor<UpdateActivity, ActivityUpdateResponse>
{
    public CommandAuditFacts Describe(UpdateActivity command, Result<ActivityUpdateResponse>? result) =>
        new() { ObjectRef = ObjectRef.For(ModuleCode.WRK, "Activity", ActivityId.From(command.Request.Id ?? Guid.Empty)) };
}

/// <summary><c>wrk.Activity.bulkReassign</c>: reassign up to 100 activities to one target or by the assignment rules; one result per activity.</summary>
internal sealed record BulkReassignActivities(ActivityBulkReassignRequest Request) : ICommand<ActivityBulkReassignResponse>;

internal sealed class BulkReassignActivitiesValidator : AbstractValidator<BulkReassignActivities>
{
    public BulkReassignActivitiesValidator() => RuleFor(c => c.Request.Selection).NotNull().WithErrorCode("SELECTION_REQUIRED");
}

/// <summary>One activity's outcome of a bulk reassignment.</summary>
internal sealed record BulkItemResult(Guid Id, string Outcome, string? Code, string? Detail, int? RecordVersion, AssigneeView? Assignee);

internal sealed class BulkReassignActivitiesHandler(ActivityEngine engine, ActivityReader reader, WorkCatalogue catalogue)
    : ICommandHandler<BulkReassignActivities, ActivityBulkReassignResponse>
{
    /// <summary>The most activities one call moves.</summary>
    public const int MaxItems = 100;

    private const int MaxReason = 2000;

    /// <summary>Event reason code of every assignment made here.</summary>
    private const string ReasonCode = "BULK_REASSIGN";

    public async Task<Result<ActivityBulkReassignResponse>> HandleAsync(BulkReassignActivities command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        if (!actor.IsManager)
        {
            return WorkErrors.Of(WorkErrors.Permission, "Only a supervisor can reassign work in bulk.");
        }

        if (request.IncludeParticipants is { ValueKind: JsonValueKind.True })
        {
            // TODO(hardening): move the participants along once wrk participants exist.
            return WorkErrors.Of(WorkErrors.NotAvailable, "includeParticipants is not available yet.");
        }

        var reason = Loose.Text(request.Reason)?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > MaxReason)
        {
            return WorkErrors.Of(WorkErrors.NoteRequired, $"A reason of at most {MaxReason} characters is required.");
        }

        var target = ParseTarget(request);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var ids = await SelectAsync(request.Selection, cancellationToken).ConfigureAwait(false);
        if (ids.IsFailure)
        {
            return ids.Error!;
        }

        var results = new List<BulkItemResult>(ids.Value.Count);
        foreach (var id in ids.Value)
        {
            var moved = await engine.AssignAsync(id, target.Value, cancellationToken, ReasonCode).ConfigureAwait(false);
            results.Add(moved.IsSuccess
                ? new BulkItemResult(id, "REASSIGNED", null, null, moved.Value.RecordVersion, new AssigneeView(moved.Value.AssigneeType, moved.Value.AssigneeId))
                : new BulkItemResult(id, "FAILED", moved.Error!.Code.ToString(), moved.Error.Detail, null, null));
        }

        var failed = results.Count(r => r.Outcome == "FAILED");
        return new ActivityBulkReassignResponse
        {
            // The job runs inside the call, so there is no job id; a dry run (Dry-Run header) returns the same per-activity preview, rolled back.
            Results = JsonSerializer.SerializeToElement(
                new { requested = results.Count, reassigned = results.Count - failed, failed, results }, SharedKernelJson.Options),
        };
    }

    private static Result<AssignTarget> ParseTarget(ActivityBulkReassignRequest request)
    {
        var rules = request.Rules is { ValueKind: JsonValueKind.True } || string.Equals(Loose.Text(request.Rules), "rules", StringComparison.OrdinalIgnoreCase);
        var hasTarget = request.Target is { ValueKind: JsonValueKind.Object };
        if (rules == hasTarget)
        {
            return WorkErrors.Invalid("Give exactly one of target ({type: USER or QUEUE, id}) or rules (true).");
        }

        if (rules)
        {
            return new AssignTarget(AssignTarget.Rules, "rules");
        }

        var target = request.Target!.Value;
        var type = target.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()?.ToUpperInvariant() : null;
        var id = target.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
        return type is ActivityRow.User or ActivityRow.Queue && !string.IsNullOrWhiteSpace(id)
            ? new AssignTarget(type, id)
            : WorkErrors.Of(WorkErrors.TargetIneligible, "target is {type: USER or QUEUE, id}.");
    }

    /// <summary>The distinct activity ids: <c>{ids: [...]}</c> or <c>{filter: "scope:team,queue:X"}</c>, at most <see cref="MaxItems"/>.</summary>
    private async Task<Result<List<Guid>>> SelectAsync(JsonElement? selection, CancellationToken cancellationToken)
    {
        if (selection is not { ValueKind: JsonValueKind.Object } body)
        {
            return WorkErrors.Invalid("selection is {ids: [...]} or {filter: text}.");
        }

        var hasIds = body.TryGetProperty("ids", out var idsElement);
        var hasFilter = body.TryGetProperty("filter", out var filterElement);
        if (hasIds == hasFilter)
        {
            return WorkErrors.Invalid("selection names exactly one of ids or filter.");
        }

        List<Guid> ids;
        if (hasIds)
        {
            if (idsElement.ValueKind != JsonValueKind.Array)
            {
                return WorkErrors.Invalid("ids is an array of activity ids.");
            }

            ids = [];
            foreach (var item in idsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var id))
                {
                    return WorkErrors.Invalid("ids holds activity ids (uuid).");
                }

                if (!ids.Contains(id))
                {
                    ids.Add(id);
                }

                if (ids.Count > MaxItems)
                {
                    break;
                }
            }
        }
        else
        {
            if (filterElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(filterElement.GetString()))
            {
                return WorkErrors.Invalid("filter is the list filter text.");
            }

            var selected = await reader.SelectIdsAsync(filterElement.GetString()!, MaxItems, cancellationToken).ConfigureAwait(false);
            if (selected.IsFailure)
            {
                return selected.Error!;
            }

            ids = selected.Value;
        }

        if (ids.Count > MaxItems)
        {
            return WorkErrors.Of(WorkErrors.LimitExceeded, $"At most {MaxItems} activities can be reassigned in one call.");
        }

        return ids.Count == 0 ? WorkErrors.Invalid("selection matches no activity.") : ids;
    }
}

internal sealed class BulkReassignActivitiesAuditor : ICommandAuditor<BulkReassignActivities, ActivityBulkReassignResponse>
{
    public CommandAuditFacts Describe(BulkReassignActivities command, Result<ActivityBulkReassignResponse>? result) =>
        new() { ObjectRef = new ObjectRef(ModuleCode.WRK, "Activity", "bulk") };
}
