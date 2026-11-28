using CoreIns.Modules.Party.Commands;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Party.Api;

/// <summary>REST facade of <c>pty.AgencyUser.*</c> (BO-PTY-12, REQ-PTY-243..252): delegated agency administration. Agency scope is enforced in the handlers.</summary>
[ApiController]
[Route("api/pty/v1/agency-users")]
internal sealed class AgencyAdminController : ControllerBase
{
    /// <summary>pty.AgencyUser.create.</summary>
    [HttpPost]
    [Authorize(Policy = "pty.AgencyUser.create")]
    public async Task<IResult> CreateAsync(
        [FromBody] AgencyUserCreateRequest request, [FromServices] ICommandHandler<CreateAgencyUser, AgencyUserResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CreateAgencyUser(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.AgencyUser.list: the users of one agency.</summary>
    [HttpGet]
    [Authorize(Policy = "pty.AgencyUser.list")]
    public async Task<IResult> ListAsync([FromQuery] Guid intermediaryId, [FromServices] AgencyAdminService service, CancellationToken cancellationToken)
    {
        if (intermediaryId == Guid.Empty)
        {
            return HttpResults.Problem(AgencyAdminService.NotFound(), HttpContext);
        }

        var agency = new IntermediaryId(intermediaryId);
        if (await service.AuthorizeAsync(agency, true, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return HttpResults.Problem(denied, HttpContext);
        }

        var found = await service.AgencyAsync(agency, true, cancellationToken).ConfigureAwait(false);
        return found.IsFailure ? HttpResults.Problem(found.Error, HttpContext) : Results.Ok(await service.ListAsync(agency, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>pty.AgencyUser.changeRole.</summary>
    [HttpPost("change-role")]
    [Authorize(Policy = "pty.AgencyUser.changeRole")]
    public async Task<IResult> ChangeRoleAsync(
        [FromBody] AgencyUserChangeRoleRequest request, [FromServices] ICommandHandler<ChangeAgencyUserRole, AgencyUserResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ChangeAgencyUserRole(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.AgencyUser.grantAccess.</summary>
    [HttpPost("grant-access")]
    [Authorize(Policy = "pty.AgencyUser.grantAccess")]
    public async Task<IResult> GrantAccessAsync(
        [FromBody] AgencyUserGrantAccessRequest request, [FromServices] ICommandHandler<GrantAgencyAccess, AgencyUserResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new GrantAgencyAccess(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.AgencyUser.revokeAccess.</summary>
    [HttpPost("revoke-access")]
    [Authorize(Policy = "pty.AgencyUser.revokeAccess")]
    public async Task<IResult> RevokeAccessAsync(
        [FromBody] AgencyUserRevokeAccessRequest request, [FromServices] ICommandHandler<RevokeAgencyAccess, AgencyUserResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RevokeAgencyAccess(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.AgencyUser.suspend.</summary>
    [HttpPost("suspend")]
    [Authorize(Policy = "pty.AgencyUser.suspend")]
    public async Task<IResult> SuspendAsync(
        [FromBody] AgencyUserSuspendRequest request, [FromServices] ICommandHandler<SuspendAgencyUser, AgencyUserResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SuspendAgencyUser(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.AgencyUser.reactivate.</summary>
    [HttpPost("reactivate")]
    [Authorize(Policy = "pty.AgencyUser.reactivate")]
    public async Task<IResult> ReactivateAsync(
        [FromBody] AgencyUserReactivateRequest request, [FromServices] ICommandHandler<ReactivateAgencyUser, AgencyUserResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReactivateAgencyUser(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.AgencyUser.removeProducer.</summary>
    [HttpPost("remove-producer")]
    [Authorize(Policy = "pty.AgencyUser.removeProducer")]
    public async Task<IResult> RemoveProducerAsync(
        [FromBody] AgencyUserRemoveProducerRequest request, [FromServices] ICommandHandler<RemoveAgencyProducer, AgencyUserRemoveProducerResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RemoveAgencyProducer(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
