using System.Text.Json;
using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Claims.Api;

/// <summary>REST facade of <c>clm.Exposure.update|close|reopen</c> (BO-CLM-01); <c>create</c> stays with the claims controller.</summary>
[ApiController]
[Route("api/clm/v1/exposures")]
internal sealed class ExposuresController : ControllerBase
{
    /// <summary>clm.Exposure.update (PATCH).</summary>
    [HttpPatch("{id}")]
    [Authorize(Policy = ClaimsPermissions.ExposureUpdate)]
    public async Task<IResult> UpdateAsync(
        string id, [FromBody] ExposureUpdateRequest request, [FromServices] ICommandHandler<UpdateExposure, ExposureUpdateResponse> handler, CancellationToken cancellationToken) =>
        Guid.TryParse(id, out var exposureId)
            ? (await handler.HandleAsync(new UpdateExposure(new ExposureId(exposureId), request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext)
            : HttpResults.Problem(ClaimSupport.NotFound("exposure"), HttpContext);

    /// <summary>clm.Exposure.close; CLM-ERR-CLOSE-GUARD carries <c>errors[]</c> { code, exposureIds[] } (D-SL4-14).</summary>
    [HttpPost("close")]
    [Authorize(Policy = ClaimsPermissions.ExposureClose)]
    public async Task<IResult> CloseAsync(
        [FromBody] ExposureCloseRequest request, [FromServices] ICommandHandler<CloseExposure, ExposureCloseResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CloseExposure(request), cancellationToken).ConfigureAwait(false);
        if (result.IsFailure && result.Error!.Code.Value == "CLM-ERR-CLOSE-GUARD"
            && result.Error.Metadata.TryGetValue("closeGuardErrors", out var json))
        {
            var problem = HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>().Create(result.Error, HttpContext);
            problem.Extensions.Remove("closeGuardErrors");
            problem.Extensions["errors"] = JsonSerializer.Deserialize<CloseGuardError[]>(json, CoreIns.SharedKernel.Json.SharedKernelJson.Options);
            return Results.Problem(problem);
        }

        return result.ToHttpResult(HttpContext);
    }

    /// <summary>clm.Exposure.reopen.</summary>
    [HttpPost("reopen")]
    [Authorize(Policy = ClaimsPermissions.ExposureReopen)]
    public async Task<IResult> ReopenAsync(
        [FromBody] ExposureReopenRequest request, [FromServices] ICommandHandler<ReopenExposure, ExposureReopenResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReopenExposure(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
