using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Recovery;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Reinsurance.Api;

/// <summary>
/// REST facade of the recovery operations (BO-RI-08; contracts/openapi/ri.yaml): full restatement of a contract year,
/// occurrence link / unlink, manual adjustment and the aggregate tracker. Thin: the commands go through the pipeline
/// (idempotency, dry run, audit); the authority of an adjustment is checked in the handler, never taken from the client.
/// </summary>
[ApiController]
internal sealed class RecoveryOpsController : ControllerBase
{
    /// <summary>ri.Recovery.recalculate → 202 with the batch (REQ-RI-003).</summary>
    [HttpPost("api/ri/v1/recoveries/recalculate")]
    [Authorize(Policy = ReinsurancePermissions.RecoveryRecalculate)]
    public async Task<IResult> RecalculateAsync(
        [FromBody] RecoveryRecalculateRequest request, [FromServices] ICommandHandler<RecalculateRecoveries, RecoveryRecalculateResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RecalculateRecoveries(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Json(result.Value, statusCode: StatusCodes.Status202Accepted) : RiHttp.Problem(result.Error!, HttpContext);
    }

    /// <summary>ri.Recovery.adjust (REQ-RI-135).</summary>
    [HttpPost("api/ri/v1/recoveries/adjust")]
    [Authorize(Policy = ReinsurancePermissions.RecoveryAdjust)]
    public async Task<IResult> AdjustAsync(
        [FromBody] RecoveryAdjustRequest request, [FromServices] ICommandHandler<AdjustRecovery, RecoveryAdjustResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AdjustRecovery(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Occurrence.link (REQ-RI-115).</summary>
    [HttpPost("api/ri/v1/occurrences/link")]
    [Authorize(Policy = ReinsurancePermissions.OccurrenceLink)]
    public async Task<IResult> LinkAsync(
        [FromBody] OccurrenceLinkRequest request, [FromServices] ICommandHandler<LinkOccurrence, OccurrenceLinkResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new LinkOccurrence(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Occurrence.unlink.</summary>
    [HttpPost("api/ri/v1/occurrences/unlink")]
    [Authorize(Policy = ReinsurancePermissions.OccurrenceUnlink)]
    public async Task<IResult> UnlinkAsync(
        [FromBody] OccurrenceUnlinkRequest request, [FromServices] ICommandHandler<UnlinkOccurrence, OccurrenceUnlinkResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new UnlinkOccurrence(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.AggregateTracker.get: the layer schedule of a contract year (REQ-RI-152).</summary>
    [HttpGet("api/ri/v1/aggregate-tracker/get")]
    [Authorize(Policy = ReinsurancePermissions.AggregateTrackerGet)]
    public async Task<IResult> TrackerAsync([FromQuery] int? year, [FromQuery] string? layer, [FromServices] AggregateTrackerReader reader, CancellationToken cancellationToken)
    {
        if (year is null or < 1900 or > 2200)
        {
            return RiHttp.Problem(Registry.RiErrors.Validation("year", "YEAR", "year must be a contract year."), HttpContext);
        }

        return Results.Ok(await reader.GetAsync(year.Value, layer, cancellationToken).ConfigureAwait(false));
    }
}
