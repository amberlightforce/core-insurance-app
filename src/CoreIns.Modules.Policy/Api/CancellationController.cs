using CoreIns.Modules.Policy.Commands.Cancellation;
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

    /// <summary>pol.Cancellation.create: with <c>?dryRun=true</c> the preview only, nothing is written.</summary>
    [HttpPost("cancellations")]
    [Authorize(Policy = CreatePermission)]
    public async Task<IResult> CreateAsync(
        [FromBody] CancellationRequest request, [FromServices] ICommandHandler<CancelPolicy, CancellationResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CancelPolicy(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
