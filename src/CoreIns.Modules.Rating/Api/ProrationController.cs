using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Modules.Rating.Contracts.Servicing;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Rating.Api;

/// <summary>Permission of the proration operation (the contract's <c>x-permission</c>).</summary>
internal static class ProrationPermissions
{
    public const string Prorate = "rat.Proration.prorate";
}

/// <summary>
/// REST facade of <c>rat.Proration.prorate</c> (a pure query, POST because of the body). Binds the generated contract, calls the
/// in-process engine and maps failures to problem details. The convention is read from the product artefact, never chosen by the caller.
/// </summary>
[ApiController]
[Route("api/rat/v1")]
internal sealed class ProrationController : ControllerBase
{
    /// <summary>rat.Proration.prorate.</summary>
    [HttpPost("proration/prorate")]
    [SkipIdempotency]
    [Authorize(Policy = ProrationPermissions.Prorate)]
    public async Task<IResult> ProrateAsync(
        [FromBody] ProrationProrateRequest request, [FromServices] IRatingProrationEngine engine, CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await engine.ProrateAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (DomainException ex)
        {
            return HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(ex.Error, HttpContext);
        }
    }
}
