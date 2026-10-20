using CoreIns.Modules.Billing.Commands;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Modules.Billing.Api;

/// <summary>
/// Development-only trigger of the overdue sweep (SL6-E2E-07), outside the public contract like <c>/dev/clock</c>: the worker's
/// <c>DelinquencySweeper</c> runs <c>bil.Delinquency.sweep</c> on a timer, so a journey calls <c>POST /dev/bil/delinquency/sweep</c> after moving the dev clock.
/// Answers 404 in Production.
/// </summary>
[ApiController]
[Route("dev/bil/delinquency")]
internal sealed class DevDelinquencyController(IHostEnvironment environment) : ControllerBase
{
    /// <summary>Runs the sweep now; returns the number of cases opened.</summary>
    [HttpPost("sweep")]
    [Authorize(Policy = DisbursementPermissions.DevBank)]
    public async Task<IResult> SweepAsync([FromServices] ICommandHandler<SweepDelinquency, int> handler, CancellationToken cancellationToken)
    {
        if (environment.IsProduction())
        {
            return Results.NotFound();
        }

        var result = await handler.HandleAsync(new SweepDelinquency(), cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? result.ToHttpResult(HttpContext) : Results.Ok(new { opened = result.Value });
    }
}
