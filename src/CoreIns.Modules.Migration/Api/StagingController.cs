using CoreIns.Modules.Migration.Commands;
using CoreIns.Modules.Migration.Services.Staging;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Migration.Api;

/// <summary>REST facade of legacy staging (BO-MIG-02; contracts/openapi/mig.yaml mig.Staging.*).</summary>
[ApiController]
[Route("api/mig/v1/staging")]
internal sealed class StagingController : ControllerBase
{
    [HttpPost("load")]
    [Authorize(Policy = "mig.Staging.load")]
    public async Task<IResult> LoadAsync([FromBody] StagingLoadBody body, [FromServices] ICommandHandler<LoadStaging, StagingSnapshotView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new LoadStaging(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpGet("snapshots")]
    [Authorize(Policy = "mig.Staging.snapshots")]
    public async Task<IResult> SnapshotsAsync([FromQuery] string? source, [FromServices] LegacyStaging staging, CancellationToken cancellationToken) =>
        (await staging.SnapshotsAsync(source, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
