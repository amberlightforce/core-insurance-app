using CoreIns.Modules.Migration.Commands;
using CoreIns.Modules.Migration.Services.SignOff;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Migration.Api;

/// <summary>REST facade of the wave sign-off (BO-MIG-11; contracts/openapi/mig.yaml mig.Signoff.*).</summary>
[ApiController]
[Route("api/mig/v1/signoff")]
internal sealed class SignOffController : ControllerBase
{
    [HttpPost("waive")]
    [Authorize(Policy = "mig.Signoff.waive")]
    public async Task<IResult> WaiveAsync([FromBody] SignOffWaiveBody body, [FromServices] ICommandHandler<WaiveVariances, SignOffWaiveView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new WaiveVariances(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("request")]
    [Authorize(Policy = "mig.Signoff.request")]
    public async Task<IResult> RequestAsync([FromBody] SignOffRequestBody body, [FromServices] ICommandHandler<RequestSignOff, SignOffStatusView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RequestSignOff(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("approve")]
    [Authorize(Policy = "mig.Signoff.approve")]
    public async Task<IResult> ApproveAsync([FromBody] SignOffDecisionBody body, [FromServices] ICommandHandler<ApproveSignOff, SignOffStatusView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ApproveSignOff(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("reject")]
    [Authorize(Policy = "mig.Signoff.reject")]
    public async Task<IResult> RejectAsync([FromBody] SignOffDecisionBody body, [FromServices] ICommandHandler<RejectSignOff, SignOffStatusView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RejectSignOff(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpGet("status")]
    [Authorize(Policy = "mig.Signoff.status")]
    public async Task<IResult> StatusAsync([FromQuery] string? scope, [FromServices] SignOffService service, CancellationToken cancellationToken) =>
        (await service.StatusAsync(scope, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
