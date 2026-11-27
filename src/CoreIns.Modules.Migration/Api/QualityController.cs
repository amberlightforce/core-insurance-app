using CoreIns.Modules.Migration.Commands;
using CoreIns.Modules.Migration.Services.Quality;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Migration.Api;

/// <summary>REST facade of data quality (BO-MIG-03; contracts/openapi/mig.yaml mig.Profile.run, mig.Dq.findings, mig.Dq.waive).</summary>
[ApiController]
[Route("api/mig/v1/quality")]
internal sealed class QualityController : ControllerBase
{
    [HttpPost("profile/run")]
    [Authorize(Policy = "mig.Profile.run")]
    public async Task<IResult> RunAsync([FromBody] ProfileRunBody body, [FromServices] ICommandHandler<RunProfile, DqRunView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RunProfile(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpGet("findings")]
    [Authorize(Policy = "mig.Dq.findings")]
    public async Task<IResult> FindingsAsync(
        [FromQuery] Guid? runId, [FromQuery] Guid? snapshotId, [FromQuery] string? ruleCode, [FromQuery] string? severity, [FromQuery] string? objectType,
        [FromQuery] bool? includeWaived, [FromQuery] int? limit, [FromQuery] int? offset, [FromServices] DataQualityService service, CancellationToken cancellationToken) =>
        (await service.FindingsAsync(runId, snapshotId, ruleCode, severity, objectType, includeWaived, limit, offset, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("waive")]
    [Authorize(Policy = "mig.Dq.waive")]
    public async Task<IResult> WaiveAsync([FromBody] DqWaiveBody body, [FromServices] ICommandHandler<WaiveFinding, DqWaiverView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new WaiveFinding(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
