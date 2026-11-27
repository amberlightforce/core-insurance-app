using CoreIns.Modules.Migration.Commands;
using CoreIns.Modules.Migration.Services.Waves;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Migration.Api;

/// <summary>REST facade of the renewal-conversion waves (BO-MIG-07; contracts/openapi/mig.yaml mig.Wave.*).</summary>
[ApiController]
[Route("api/mig/v1/waves")]
internal sealed class WaveController : ControllerBase
{
    [HttpPost("plan")]
    [Authorize(Policy = "mig.Wave.plan")]
    public async Task<IResult> PlanAsync([FromBody] WavePlanBody body, [FromServices] ICommandHandler<PlanWave, WaveView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new PlanWave(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "mig.Wave.get")]
    public async Task<IResult> GetAsync(Guid id, [FromQuery] string? state, [FromQuery] int? limit, [FromServices] WaveService service, CancellationToken cancellationToken) =>
        (await service.GetAsync(id, state, limit, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("convert")]
    [Authorize(Policy = "mig.Wave.convert")]
    public async Task<IResult> ConvertAsync([FromBody] WaveConvertBody body, [FromServices] ICommandHandler<ConvertWave, WaveView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ConvertWave(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("adjust")]
    [Authorize(Policy = "mig.Wave.adjust")]
    public async Task<IResult> AdjustAsync([FromBody] WaveAdjustBody body, [FromServices] ICommandHandler<AdjustWave, WaveView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AdjustWave(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("close")]
    [Authorize(Policy = "mig.Wave.close")]
    public async Task<IResult> CloseAsync([FromBody] WaveCloseBody body, [FromServices] ICommandHandler<CloseWave, WaveView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CloseWave(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
