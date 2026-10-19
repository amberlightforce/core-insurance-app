using CoreIns.Modules.Migration.Commands;
using CoreIns.Modules.Migration.Domain;
using CoreIns.Modules.Migration.Services;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Migration.Api;

/// <summary>Permission names (<c>x-permission</c>) of the migration framework operations.</summary>
internal static class MigrationPermissions
{
    public const string XrefRegister = "mig.Xref.register";
    public const string XrefResolve = "mig.Xref.resolve";
    public const string XrefList = "mig.Xref.list";
    public const string RoutingResolve = "mig.Routing.resolve";
    public const string RoutingOverride = "mig.Routing.override";
    public const string BatchCreate = "mig.Batch.create";
    public const string BatchStart = "mig.Batch.start";
    public const string BatchApprove = "mig.Batch.approve";
    public const string BatchPause = "mig.Batch.pause";
    public const string BatchResume = "mig.Batch.resume";
    public const string BatchReverse = "mig.Batch.reverse";
    public const string BatchGet = "mig.Batch.get";
    public const string BatchOutcomes = "mig.Batch.outcomes";
}

/// <summary>REST facade of the migration framework (contracts/openapi/mig.yaml paths; create and approve are SL6-MIG-FRAME additions).</summary>
[ApiController]
[Route("api/mig/v1")]
internal sealed class MigrationController : ControllerBase
{
    [HttpPost("xrefs/register")]
    [Authorize(Policy = MigrationPermissions.XrefRegister)]
    public async Task<IResult> RegisterXrefAsync([FromBody] XrefRegisterBody body, [FromServices] ICommandHandler<RegisterXref, XrefView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RegisterXref(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("xrefs/resolve")]
    [Authorize(Policy = MigrationPermissions.XrefResolve)]
    public async Task<IResult> ResolveXrefAsync([FromBody] XrefResolveBody body, [FromServices] MigrationReader reader, CancellationToken cancellationToken) =>
        (await reader.ResolveXrefAsync(body, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpGet("xrefs")]
    [Authorize(Policy = MigrationPermissions.XrefList)]
    public async Task<IActionResult> ListXrefsAsync(
        [FromQuery] string? objectType, [FromQuery] Guid? batchId, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] MigrationReader reader, CancellationToken cancellationToken) =>
        Ok(await reader.ListXrefsAsync(objectType, batchId, cursor, limit, cancellationToken).ConfigureAwait(false));

    [HttpPost("routing/resolve")]
    [Authorize(Policy = MigrationPermissions.RoutingResolve)]
    public async Task<IResult> ResolveRouteAsync([FromBody] RoutingResolveBody body, [FromServices] MigrationReader reader, [FromServices] IClock clock, CancellationToken cancellationToken)
    {
        var services = HttpContext.RequestServices;
        BusinessDate date;
        if (body.Date is null)
        {
            var now = clock.Now.ToDateTimeOffset();
            date = new BusinessDate(DateOnly.FromDateTime(now.UtcDateTime));
        }
        else if (DateOnly.TryParse(body.Date, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            date = new BusinessDate(parsed);
        }
        else
        {
            return Results.BadRequest("date must be yyyy-MM-dd.");
        }

        _ = services;
        var resolved = await reader.ResolveRouteAsync(body.ObjectType, body.Key, date, cancellationToken).ConfigureAwait(false);
        return resolved.IsSuccess ? Results.Ok(MigrationRoutingService.ToResponse(resolved.Value, body.Key)) : resolved.ToHttpResult(HttpContext);
    }

    [HttpPost("routing/override")]
    [Authorize(Policy = MigrationPermissions.RoutingOverride)]
    public async Task<IResult> OverrideRouteAsync([FromBody] RoutingOverrideBody body, [FromServices] ICommandHandler<OverrideRouting, RoutingView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new OverrideRouting(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("batches")]
    [Authorize(Policy = MigrationPermissions.BatchCreate)]
    public async Task<IResult> CreateBatchAsync([FromBody] CreateBatchRequest body, [FromServices] ICommandHandler<CreateBatch, BatchResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CreateBatch(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("batches/start")]
    [Authorize(Policy = MigrationPermissions.BatchStart)]
    public async Task<IResult> StartBatchAsync([FromBody] StartBatchRequest body, [FromServices] ICommandHandler<StartBatch, BatchResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new StartBatch(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("batches/approve")]
    [Authorize(Policy = MigrationPermissions.BatchApprove)]
    public async Task<IResult> ApproveBatchAsync([FromBody] ApproveBatchRequest body, [FromServices] ICommandHandler<ApproveBatch, BatchResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ApproveBatch(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("batches/pause")]
    [Authorize(Policy = MigrationPermissions.BatchPause)]
    public async Task<IResult> PauseBatchAsync([FromBody] BatchIdRequest body, [FromServices] ICommandHandler<PauseBatch, BatchResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new PauseBatch(body.BatchId), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("batches/resume")]
    [Authorize(Policy = MigrationPermissions.BatchResume)]
    public async Task<IResult> ResumeBatchAsync([FromBody] BatchIdRequest body, [FromServices] ICommandHandler<ResumeBatch, BatchResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ResumeBatch(body.BatchId), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("batches/reverse")]
    [Authorize(Policy = MigrationPermissions.BatchReverse)]
    public async Task<IResult> ReverseBatchAsync([FromBody] ReverseBatchRequest body, [FromServices] ICommandHandler<ReverseBatch, BatchResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReverseBatch(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpGet("batches/outcomes")]
    [Authorize(Policy = MigrationPermissions.BatchOutcomes)]
    public async Task<IResult> OutcomesAsync(
        [FromQuery] Guid batchId, [FromQuery] string? outcome, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] MigrationReader reader, CancellationToken cancellationToken) =>
        (await reader.OutcomesAsync(batchId, outcome, cursor, limit, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpGet("batches/{id:guid}")]
    [Authorize(Policy = MigrationPermissions.BatchGet)]
    public async Task<IResult> GetBatchAsync(Guid id, [FromServices] MigrationReader reader, CancellationToken cancellationToken) =>
        (await reader.GetBatchAsync(id, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
