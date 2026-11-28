using CoreIns.Modules.Work.Commands;
using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Modules.Work.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Results;
using CoreIns.Modules.Work.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Work.Api;

/// <summary>REST facade of <c>wrk.Activity.*</c> and <c>wrk.Queue.getNext</c> (contracts/openapi/wrk.yaml): thin, the staff inbox subset.</summary>
[ApiController]
[Route("api/wrk/v1")]
internal sealed class ActivityController : ControllerBase
{
    public const string ListPermission = "wrk.Activity.list";
    public const string GetPermission = "wrk.Activity.get";
    public const string CreatePermission = "wrk.Activity.create";
    public const string AssignPermission = "wrk.Activity.assign";
    public const string CompletePermission = "wrk.Activity.complete";
    public const string SkipPermission = "wrk.Activity.skip";
    public const string CancelPermission = "wrk.Activity.cancel";
    public const string BlockingPermission = "wrk.Activity.blockingStatus";
    public const string UpdatePermission = "wrk.Activity.update";
    public const string BulkReassignPermission = "wrk.Activity.bulkReassign";
    public const string TeamSummaryPermission = "wrk.Team.summary";
    public const string GetNextPermission = "wrk.Queue.getNext";
    public const string SimulatePermission = "wrk.Assignment.simulate";
    public const string PatternListPermission = "wrk.Pattern.list";
    public const string PatternUpsertPermission = "wrk.Pattern.upsert";

    /// <summary>wrk.Activity.list: my tasks (default), the unassigned queue or the team's work.</summary>
    [HttpGet("activities")]
    [Authorize(Policy = ListPermission)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] Guid? id, [FromQuery] string? filters,
        [FromServices] ActivityReader reader, CancellationToken cancellationToken) =>
        (await reader.ListAsync(id, cursor, limit, filters, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.Activity.get.</summary>
    [HttpGet("activities/{id}")]
    [Authorize(Policy = GetPermission)]
    public async Task<IResult> GetAsync(string id, [FromServices] ActivityReader reader, CancellationToken cancellationToken) =>
        (await reader.GetAsync(id, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.Activity.update: edit title, priority, due date or note (optimistic record version).</summary>
    [HttpPatch("activities/{id}")]
    [Authorize(Policy = UpdatePermission)]
    public async Task<IResult> UpdateAsync(
        string id, [FromBody] ActivityUpdateRequest request, [FromServices] ICommandHandler<UpdateActivity, ActivityUpdateResponse> handler, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var activityId))
        {
            Result<ActivityUpdateResponse> missing = WorkErrors.NotFound();
            return missing.ToHttpResult(HttpContext);
        }

        return (await handler.HandleAsync(new UpdateActivity(request with { Id = activityId }), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }

    /// <summary>wrk.Activity.bulkReassign: up to 100 activities, one result each (supervisors).</summary>
    [HttpPost("activities/bulk-reassign")]
    [Authorize(Policy = BulkReassignPermission)]
    public async Task<IResult> BulkReassignAsync(
        [FromBody] ActivityBulkReassignRequest request, [FromServices] ICommandHandler<BulkReassignActivities, ActivityBulkReassignResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new BulkReassignActivities(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.Team.summary: counts per queue and assignee (supervisors).</summary>
    [HttpGet("teams/summary")]
    [Authorize(Policy = TeamSummaryPermission)]
    public async Task<IResult> TeamSummaryAsync(
        [FromQuery] string? groups, [FromQuery] string? grouping, [FromServices] ActivityReader reader, CancellationToken cancellationToken) =>
        (await reader.TeamSummaryAsync(groups, grouping, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.Activity.blockingStatus.</summary>
    [HttpGet("activities/blocking-status")]
    [Authorize(Policy = BlockingPermission)]
    public async Task<IResult> BlockingStatusAsync([FromQuery] string? objectRef, [FromServices] ActivityReader reader, CancellationToken cancellationToken) =>
        (await reader.BlockingStatusAsync(objectRef, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.Activity.create.</summary>
    [HttpPost("activities")]
    [Authorize(Policy = CreatePermission)]
    public async Task<IResult> CreateAsync(
        [FromBody] ActivityCreateRequest request, [FromServices] ICommandHandler<CreateActivity, ActivityCreateResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CreateActivity(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.Activity.assign.</summary>
    [HttpPost("activities/assign")]
    [Authorize(Policy = AssignPermission)]
    public async Task<IResult> AssignAsync(
        [FromBody] ActivityAssignRequest request, [FromServices] ICommandHandler<AssignActivity, ActivityAssignResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AssignActivity(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.Activity.complete.</summary>
    [HttpPost("activities/complete")]
    [Authorize(Policy = CompletePermission)]
    public async Task<IResult> CompleteAsync(
        [FromBody] ActivityCompleteRequest request, [FromServices] ICommandHandler<CompleteActivity, ActivityCompleteResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CompleteActivity(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.Activity.skip.</summary>
    [HttpPost("activities/skip")]
    [Authorize(Policy = SkipPermission)]
    public async Task<IResult> SkipAsync(
        [FromBody] ActivitySkipRequest request, [FromServices] ICommandHandler<SkipActivity, ActivitySkipResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SkipActivity(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.Activity.cancel.</summary>
    [HttpPost("activities/cancel")]
    [Authorize(Policy = CancelPermission)]
    public async Task<IResult> CancelAsync(
        [FromBody] ActivityCancelRequest request, [FromServices] ICommandHandler<CancelActivity, ActivityCancelResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CancelActivity(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.Queue.getNext: take the most urgent open activity of my queues.</summary>
    [HttpPost("queues/get-next")]
    [Authorize(Policy = GetNextPermission)]
    public async Task<IResult> GetNextAsync(
        [FromBody] QueueGetNextRequest request, [FromServices] ICommandHandler<GetNextActivity, QueueGetNextResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new GetNextActivity(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.Assignment.simulate: where the assignment rules would route an activity of a pattern (read only).</summary>
    [HttpPost("assignments/simulate")]
    [Authorize(Policy = SimulatePermission)]
    public async Task<IResult> SimulateAsync(
        [FromBody] AssignmentSimulateRequest request, [FromServices] AssignmentSimulator simulator, CancellationToken cancellationToken) =>
        (await simulator.SimulateAsync(request, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.Pattern.list: the queue and pattern catalogue (Platform.Admin).</summary>
    [HttpGet("admin/patterns")]
    [Authorize(Policy = PatternListPermission)]
    public async Task<IResult> ListPatternsAsync([FromServices] PatternCatalogueReader reader, CancellationToken cancellationToken) =>
        (await reader.ListAsync(cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.Pattern.upsert: create or replace queues and patterns by code (Platform.Admin).</summary>
    [HttpPut("admin/patterns")]
    [Authorize(Policy = PatternUpsertPermission)]
    public async Task<IResult> UpsertPatternsAsync(
        [FromBody] PatternUpsertRequest request, [FromServices] ICommandHandler<UpsertPatterns, PatternCatalogue> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new UpsertPatterns(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
