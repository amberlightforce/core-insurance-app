using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Claims.Api;

/// <summary>REST facade of <c>clm.Coverage.decide</c> (BO-CLM-03); <c>reverify</c> stays with the claims controller.</summary>
[ApiController]
[Route("api/clm/v1/coverage")]
internal sealed class CoverageController : ControllerBase
{
    /// <summary>clm.Coverage.decide: COVERED, DENIED or RESERVATION_OF_RIGHTS on one exposure (REQ-CLM-051).</summary>
    [HttpPost("decide")]
    [Authorize(Policy = ClaimsPermissions.CoverageDecide)]
    public async Task<IResult> DecideAsync(
        [FromBody] CoverageDecideRequest request, [FromServices] ICommandHandler<DecideCoverage, CoverageDecideResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DecideCoverage(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
