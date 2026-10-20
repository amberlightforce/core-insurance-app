using System.Text.Json;
using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Modules.Work.Domain;
using CoreIns.Modules.Work.Persistence;
using CoreIns.Modules.Work.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel.Identifiers;
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
        // TODO(hardening): pattern overrides, suggestedAssignee and amountBand are ignored; the pattern's queue and SLA always apply.
        var created = await engine.CreateAsync(
            new CreateActivityInput(request.PatternCode, request.PrimaryObject, request.SecondaryObjects ?? [], request.RaisingModule, request.CauseRef),
            returnExistingOnDuplicate: false, cancellationToken).ConfigureAwait(false);
        return created.Map(row => new ActivityCreateResponse
        {
            ActivityId = ActivityId.From(row.ActivityId),
            Number = row.Number,
            TargetAt = row.TargetAt,
            EscalationAt = row.EscalationAt,
            Assignment = new ActivityCreateResponse.AssignmentDetail
            {
                AssigneeType = ActivityCreateResponse.AssignmentDetail.AssigneeTypeValue.Queue,
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

internal sealed class AssignActivityHandler(ActivityEngine engine, RequestContext context) : ICommandHandler<AssignActivity, ActivityAssignResponse>
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
        var actor = WorkActor.From(context);
        return assigned.Map(row => new ActivityAssignResponse { Assignment = ActivityView.From(row, actor).ToElement() });
    }

    /// <summary>"me" (default) is a claim; <c>{type: USER|QUEUE, id}</c> names a target. Assignment rules are not built: refused.</summary>
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

        // TODO(hardening): assignment by rules (wrk.Assignment.simulate / REQ-WRK assignment rules) is not built.
        return WorkErrors.Of(WorkErrors.TargetIneligible, "Only \"me\" or an explicit target is supported.");
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

internal sealed class CompleteActivityHandler(ActivityEngine engine, RequestContext context) : ICommandHandler<CompleteActivity, ActivityCompleteResponse>
{
    public async Task<Result<ActivityCompleteResponse>> HandleAsync(CompleteActivity command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var closed = await engine.CloseAsync(request.Id!.Value, ActivityRow.Completed, Loose.Text(request.Outcome), Loose.Text(request.Note), Loose.Text(request.Reason), cancellationToken).ConfigureAwait(false);
        var actor = WorkActor.From(context);
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

internal sealed class SkipActivityHandler(ActivityEngine engine, RequestContext context) : ICommandHandler<SkipActivity, ActivitySkipResponse>
{
    public async Task<Result<ActivitySkipResponse>> HandleAsync(SkipActivity command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var closed = await engine.CloseAsync(request.Id!.Value, ActivityRow.Skipped, Loose.Text(request.Outcome), Loose.Text(request.Note), Loose.Text(request.Reason), cancellationToken).ConfigureAwait(false);
        var actor = WorkActor.From(context);
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

internal sealed class CancelActivityHandler(ActivityEngine engine, RequestContext context) : ICommandHandler<CancelActivity, ActivityCancelResponse>
{
    public async Task<Result<ActivityCancelResponse>> HandleAsync(CancelActivity command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var closed = await engine.CloseAsync(request.Id!.Value, ActivityRow.Cancelled, Loose.Text(request.Outcome), Loose.Text(request.Note), Loose.Text(request.Reason), cancellationToken).ConfigureAwait(false);
        var actor = WorkActor.From(context);
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

internal sealed class GetNextActivityHandler(ActivityEngine engine, RequestContext context) : ICommandHandler<GetNextActivity, QueueGetNextResponse>
{
    public async Task<Result<QueueGetNextResponse>> HandleAsync(GetNextActivity command, CancellationToken cancellationToken)
    {
        string? queue = null;
        if (command.Request.QueueId is { } queueId)
        {
            queue = QueueIds.CodeOf(queueId);
            if (queue is null)
            {
                return WorkErrors.Of(WorkErrors.QueueEmpty, "No open activity is waiting in the queue.");
            }
        }

        var claimed = await engine.ClaimNextAsync(queue, cancellationToken).ConfigureAwait(false);
        var actor = WorkActor.From(context);
        return claimed.Map(row => new QueueGetNextResponse { AssignedActivity = ActivityView.From(row, actor).ToElement() });
    }
}

internal sealed class GetNextActivityAuditor : ICommandAuditor<GetNextActivity, QueueGetNextResponse>
{
    public CommandAuditFacts Describe(GetNextActivity command, Result<QueueGetNextResponse>? result) =>
        new() { ObjectRef = new ObjectRef(ModuleCode.WRK, "Queue", command.Request.QueueId is { } q ? q.Value.ToString("D") : "all") };
}
