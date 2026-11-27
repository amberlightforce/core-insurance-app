using CoreIns.Modules.Policy.Commands.Renewal;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Policy.Api;

/// <summary>Permission names of the renewal run operations (the operations' <c>x-permission</c>).</summary>
internal static class RenewalRunPermissions
{
    public const string Start = "pol.RenewalRun.start";
    public const string Pause = "pol.RenewalRun.pause";
    public const string RetryItem = "pol.RenewalRun.retryItem";
}

/// <summary>REST facade of <c>pol.RenewalRun.*</c> (BO-POL-10): thin, commands through the pipeline. The batch itself runs on the worker role.</summary>
[ApiController]
[Route("api/pol/v1/renewal-runs")]
internal sealed class RenewalRunController : ControllerBase
{
    /// <summary>pol.RenewalRun.start: select the terms in the window and queue them (or resume a paused run).</summary>
    [HttpPost("start")]
    [Authorize(Policy = RenewalRunPermissions.Start)]
    public async Task<IResult> StartAsync(
        [FromBody] RenewalRunStartRequest request, [FromServices] ICommandHandler<StartRenewalRun, RenewalRunStartResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new StartRenewalRun(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pol.RenewalRun.pause.</summary>
    [HttpPost("pause")]
    [Authorize(Policy = RenewalRunPermissions.Pause)]
    public async Task<IResult> PauseAsync(
        [FromBody] RenewalRunPauseRequest request, [FromServices] ICommandHandler<PauseRenewalRun, RenewalRunPauseResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new PauseRenewalRun(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pol.RenewalRun.retryItem.</summary>
    [HttpPost("retry-item")]
    [Authorize(Policy = RenewalRunPermissions.RetryItem)]
    public async Task<IResult> RetryItemAsync(
        [FromBody] RenewalRunRetryItemRequest request, [FromServices] ICommandHandler<RetryRenewalRunItem, RenewalRunRetryItemResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RetryRenewalRunItem(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
