using CoreIns.Modules.Channels.Commands;
using CoreIns.Modules.Channels.Contracts.Api;
using CoreIns.Modules.Channels.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Channels.Api;

/// <summary>
/// REST facade of the customer self-service commands (BO-CHN-03): change preview/confirm/refer, renewal accept/decline, cancellation request and
/// contact update. Every handler resolves the caller through the CHN scope (no link or not-owned: 404) and asks the permission matrix before acting.
/// </summary>
[ApiController]
[Route("api/chn/v1")]
internal sealed class PortalServicingController(PortalContactReader contacts) : ControllerBase
{
    /// <summary>chn.Change.preview.</summary>
    [HttpPost("changes/preview")]
    [Authorize(Policy = "chn.Change.preview")]
    public async Task<IResult> PreviewAsync(
        [FromBody] ChangePreviewRequest request, [FromServices] ICommandHandler<PreviewChange, ChangePreviewResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new PreviewChange(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>chn.Change.confirm.</summary>
    [HttpPost("changes/confirm")]
    [Authorize(Policy = "chn.Change.confirm")]
    public async Task<IResult> ConfirmAsync(
        [FromBody] ChangeConfirmRequest request, [FromServices] ICommandHandler<ConfirmChange, ChangeConfirmResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ConfirmChange(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>chn.Change.refer.</summary>
    [HttpPost("changes/refer")]
    [Authorize(Policy = "chn.Change.refer")]
    public async Task<IResult> ReferAsync(
        [FromBody] ChangeReferRequest request, [FromServices] ICommandHandler<ReferChange, ChangeReferResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReferChange(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>chn.PortalRenewal.accept.</summary>
    [HttpPost("portal/renewals/accept")]
    [Authorize(Policy = "chn.PortalRenewal.accept")]
    public async Task<IResult> AcceptRenewalAsync(
        [FromBody] PortalRenewalAcceptRequest request, [FromServices] ICommandHandler<AcceptPortalRenewal, PortalRenewalAcceptResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AcceptPortalRenewal(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>chn.PortalRenewal.decline.</summary>
    [HttpPost("portal/renewals/decline")]
    [Authorize(Policy = "chn.PortalRenewal.decline")]
    public async Task<IResult> DeclineRenewalAsync(
        [FromBody] PortalRenewalDeclineRequest request, [FromServices] ICommandHandler<DeclinePortalRenewal, PortalRenewalDeclineResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DeclinePortalRenewal(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>chn.PortalCancellation.submit.</summary>
    [HttpPost("portal/cancellations/request")]
    [Authorize(Policy = "chn.PortalCancellation.submit")]
    public async Task<IResult> RequestCancellationAsync(
        [FromBody] PortalCancellationSubmitRequest request, [FromServices] ICommandHandler<RequestPortalCancellation, PortalCancellationSubmitResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RequestPortalCancellation(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>chn.PortalContact.get.</summary>
    [HttpGet("portal/contact")]
    [Authorize(Policy = "chn.PortalContact.get")]
    public async Task<IResult> ContactAsync(CancellationToken cancellationToken) =>
        await contacts.GetAsync(cancellationToken).ConfigureAwait(false) is { } body ? Results.Ok(body) : PortalProblems.NotFound(HttpContext);

    /// <summary>chn.PortalContact.update.</summary>
    [HttpPost("portal/contact/update")]
    [Authorize(Policy = "chn.PortalContact.update")]
    public async Task<IResult> UpdateContactAsync(
        [FromBody] PortalContactUpdateRequest request, [FromServices] ICommandHandler<UpdatePortalContact, PortalContactUpdateResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new UpdatePortalContact(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
