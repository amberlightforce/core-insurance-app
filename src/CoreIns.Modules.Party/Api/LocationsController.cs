using CoreIns.Modules.Party.Commands;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Party.Api;

/// <summary>REST facade of <c>pty.Location.*</c> (BO-PTY-02): addresses and contact points of a party (REQ-PTY-012, -074, -081..085).</summary>
[ApiController]
[Route("api/pty/v1/locations")]
internal sealed class LocationsController : ControllerBase
{
    /// <summary>pty.Location.add.</summary>
    [HttpPost("add")]
    [Authorize(Policy = "pty.Location.add")]
    public async Task<IResult> AddAsync(
        [FromBody] LocationAddRequest request, [FromServices] ICommandHandler<AddLocation, LocationAddResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AddLocation(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.Location.update (PATCH): a new version of the current address or contact point.</summary>
    [HttpPatch("{id}")]
    [Authorize(Policy = "pty.Location.update")]
    public async Task<IResult> UpdateAsync(
        string id, [FromBody] LocationUpdateRequest request, [FromServices] ICommandHandler<UpdateLocation, LocationUpdateResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new UpdateLocation(id, request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.Location.remove: ends the location; the history stays.</summary>
    [HttpPost("remove")]
    [Authorize(Policy = "pty.Location.remove")]
    public async Task<IResult> RemoveAsync(
        [FromBody] LocationRemoveRequest request, [FromServices] ICommandHandler<RemoveLocation, LocationRemoveResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RemoveLocation(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
