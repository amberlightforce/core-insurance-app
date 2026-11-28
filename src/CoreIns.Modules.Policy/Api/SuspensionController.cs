using CoreIns.Modules.Policy.Commands.Suspension;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Policy.Api;

/// <summary>REST facade of <c>pol.Suspension.create</c> and <c>pol.Reactivation.create</c> (contracts/openapi/pol.yaml): thin, commands through the pipeline.</summary>
[ApiController]
[Route("api/pol/v1")]
internal sealed class SuspensionController : ControllerBase
{
    public const string SuspendPermission = "pol.Suspension.create";

    public const string ReactivatePermission = "pol.Reactivation.create";

    /// <summary>pol.Suspension.create → 201, Location = the job; <c>?dryRun=true</c> runs and rolls back.</summary>
    [HttpPost("suspensions")]
    [Authorize(Policy = SuspendPermission)]
    public async Task<IResult> SuspendAsync(
        [FromBody] SuspensionCreateRequest request, [FromServices] ICommandHandler<SuspendPolicy, SuspensionCreateResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new SuspendPolicy(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/pol/v1/jobs/{result.Value.JobId!.Value.Value}", result.Value)
            : HttpResults.Problem(result.Error!, HttpContext);
    }

    /// <summary>pol.Reactivation.create → 201, Location = the job.</summary>
    [HttpPost("reactivations")]
    [Authorize(Policy = ReactivatePermission)]
    public async Task<IResult> ReactivateAsync(
        [FromBody] ReactivationCreateRequest request, [FromServices] ICommandHandler<ReactivatePolicy, ReactivationCreateResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ReactivatePolicy(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/pol/v1/jobs/{result.Value.JobId!.Value.Value}", result.Value)
            : HttpResults.Problem(result.Error!, HttpContext);
    }
}
