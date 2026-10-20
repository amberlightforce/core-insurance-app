using CoreIns.Modules.Work.Commands;
using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Modules.Work.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
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
    public const string GetNextPermission = "wrk.Queue.getNext";

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
}
