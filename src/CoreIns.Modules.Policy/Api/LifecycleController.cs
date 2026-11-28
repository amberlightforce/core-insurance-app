using CoreIns.Modules.Policy.Commands.Reinstatement;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Policy.Api;

/// <summary>REST facade of <c>pol.Reinstatement.create</c> and <c>pol.Rewrite.create</c> (contracts/openapi/pol.yaml): thin, commands through the pipeline.</summary>
[ApiController]
[Route("api/pol/v1")]
internal sealed class LifecycleController : ControllerBase
{
    public const string ReinstatePermission = "pol.Reinstatement.create";

    public const string RewritePermission = "pol.Rewrite.create";

    /// <summary>pol.Reinstatement.create → 201, Location = the job; <c>?dryRun=true</c> runs and rolls back.</summary>
    [HttpPost("reinstatements")]
    [Authorize(Policy = ReinstatePermission)]
    public async Task<IResult> ReinstateAsync(
        [FromBody] ReinstatementCreateRequest request, [FromServices] ICommandHandler<ReinstatePolicy, ReinstatementCreateResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ReinstatePolicy(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/pol/v1/jobs/{result.Value.JobId!.Value.Value}", result.Value)
            : HttpResults.Problem(result.Error!, HttpContext);
    }

    /// <summary>pol.Rewrite.create → 201, Location = the new Draft job (quote and bind it with the job commands).</summary>
    [HttpPost("rewrites")]
    [Authorize(Policy = RewritePermission)]
    public async Task<IResult> RewriteAsync(
        [FromBody] RewriteCreateRequest request, [FromServices] ICommandHandler<RewritePolicy, RewriteCreateResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RewritePolicy(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/pol/v1/jobs/{result.Value.JobId!.Value.Value}", result.Value)
            : HttpResults.Problem(result.Error!, HttpContext);
    }
}
