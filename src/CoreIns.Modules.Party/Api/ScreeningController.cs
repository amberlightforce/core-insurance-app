using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Party.Api;

/// <summary>
/// REST facade of <c>pty.Screening.screen</c> over the bound <see cref="IPartyScreeningService"/>: the D-SL2-05 stub
/// outside Production, the fail-closed service (PTY-ERR-NOT-AVAILABLE) in Production. Screening input travels in the
/// body only (D-SLC-05).
/// </summary>
[ApiController]
[Route("api/pty/v1/screening")]
internal sealed class ScreeningController : ControllerBase
{
    /// <summary>pty.Screening.screen.</summary>
    [HttpPost("screen")]
    [Authorize(Policy = "pty.Screening.screen")]
    public async Task<IResult> ScreenAsync(
        [FromBody] ScreeningScreenRequest body, [FromServices] IPartyScreeningService screening, CancellationToken cancellationToken)
    {
        var context = HttpContext.RequestServices.GetRequiredService<RequestContext>();
        var key = context.IdempotencyKey ?? IdempotencyKey.New();
        return Results.Ok(await screening.ScreenAsync(body, new CommandOptions(key), cancellationToken).ConfigureAwait(false));
    }
}
