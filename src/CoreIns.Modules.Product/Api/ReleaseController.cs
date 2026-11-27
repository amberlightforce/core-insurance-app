using CoreIns.Modules.Product.Commands;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Platform.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Product.Api;

/// <summary>REST facade of the release lifecycle, BO-PFC-04: submit, sign-off, schedule, cancel-schedule.</summary>
[ApiController]
[Route("api/pfc/v1/product-versions")]
internal sealed class ReleaseController : ControllerBase
{
    /// <summary>pfc.ProductVersion.submit: lint-clean draft to a PLT approval (dry run checks only).</summary>
    [HttpPost("submit")]
    [Authorize(Policy = ProductPermissions.Submit)]
    public async Task<IResult> SubmitAsync(
        [FromBody] ProductVersionSubmitRequest request,
        [FromServices] ICommandHandler<SubmitChangeSet, ProductVersionSubmitResponse> handler,
        CancellationToken cancellationToken) =>
        this.Respond(await handler.HandleAsync(new SubmitChangeSet(request), cancellationToken).ConfigureAwait(false));

    /// <summary>pfc.ProductVersion.signOff: the checker approves or returns (maker-checker, authority PFC.RELEASE).</summary>
    [HttpPost("sign-off")]
    [Authorize(Policy = ProductPermissions.SignOff)]
    public async Task<IResult> SignOffAsync(
        [FromBody] ProductVersionSignOffRequest request,
        [FromServices] ICommandHandler<SignOffRelease, ProductVersionSignOffResponse> handler,
        CancellationToken cancellationToken) =>
        this.Respond(await handler.HandleAsync(new SignOffRelease(request), cancellationToken).ConfigureAwait(false));

    /// <summary>pfc.ProductVersion.schedule: windows and activation date of an approved release (dry run validates).</summary>
    [HttpPost("schedule")]
    [Authorize(Policy = ProductPermissions.Schedule)]
    public async Task<IResult> ScheduleAsync(
        [FromBody] ProductVersionScheduleRequest request,
        [FromServices] ICommandHandler<ScheduleRelease, ProductVersionScheduleResponse> handler,
        CancellationToken cancellationToken) =>
        this.Respond(await handler.HandleAsync(new ScheduleRelease(request), cancellationToken).ConfigureAwait(false));

    /// <summary>pfc.ProductVersion.cancelSchedule: back out a scheduled release before its activation date.</summary>
    [HttpPost("cancel-schedule")]
    [Authorize(Policy = ProductPermissions.CancelSchedule)]
    public async Task<IResult> CancelScheduleAsync(
        [FromBody] ProductVersionCancelScheduleRequest request,
        [FromServices] ICommandHandler<CancelScheduledRelease, ProductVersionCancelScheduleResponse> handler,
        CancellationToken cancellationToken) =>
        this.Respond(await handler.HandleAsync(new CancelScheduledRelease(request), cancellationToken).ConfigureAwait(false));
}
