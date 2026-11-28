using CoreIns.Modules.Policy.Commands.Cancellation;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Policy.Api;

/// <summary>REST facade of <c>pol.Cancellation.create</c> (contracts/openapi/pol.yaml): thin, the command runs through the pipeline.</summary>
[ApiController]
[Route("api/pol/v1")]
internal sealed class CancellationController : ControllerBase
{
    /// <summary>Permission of the operation (its <c>x-permission</c>), granted to roles in <c>Platform:Permissions</c>.</summary>
    public const string CreatePermission = "pol.Cancellation.create";

    public const string SchedulePermission = "pol.Cancellation.schedule";

    public const string RescindPermission = "pol.Cancellation.rescind";

    /// <summary>pol.Cancellation.create: with <c>?dryRun=true</c> the preview only, nothing is written.</summary>
    [HttpPost("cancellations")]
    [Authorize(Policy = CreatePermission)]
    public async Task<IResult> CreateAsync(
        [FromBody] CancellationCreateRequest request, [FromServices] ICommandHandler<CancelPolicy, CancellationCreateResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CancelPolicy(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pol.Cancellation.schedule (BO-POL-06): a cancellation at a future effective time; <c>?dryRun=true</c> returns the preview only.</summary>
    [HttpPost("cancellations/schedule")]
    [Authorize(Policy = SchedulePermission)]
    public async Task<IResult> ScheduleAsync(
        [FromBody] CancellationScheduleRequest request, [FromServices] ICommandHandler<ScheduleCancellation, CancellationScheduleResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ScheduleCancellation(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pol.Cancellation.rescind (BO-POL-06): withdraws a scheduled cancellation before its effective time.</summary>
    [HttpPost("cancellations/rescind")]
    [Authorize(Policy = RescindPermission)]
    public async Task<IResult> RescindAsync(
        [FromBody] CancellationRescindRequest request, [FromServices] ICommandHandler<RescindCancellation, CancellationRescindResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RescindCancellation(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
