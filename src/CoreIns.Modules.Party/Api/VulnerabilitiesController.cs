using CoreIns.Modules.Party.Commands;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Results;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Party.Api;

/// <summary>REST facade of <c>pty.Vulnerability.query</c> / <c>record</c> / <c>remove</c> (REQ-PTY-011, NX-HD-02).</summary>
[ApiController]
[Route("api/pty/v1/vulnerabilities")]
internal sealed class VulnerabilitiesController : ControllerBase
{
    /// <summary>pty.Vulnerability.query: the state of one party; an unknown party yields an empty page.</summary>
    [HttpGet("query")]
    [Authorize(Policy = PartyPermissions.VulnerabilityQuery)]
    public async Task<IResult> QueryAsync(
        [FromQuery] Guid partyId, [FromQuery] int? limit, [FromServices] VulnerabilityQueries queries, [FromServices] PartyProtection protection,
        [FromServices] RequestContext context, CancellationToken cancellationToken)
    {
        if (partyId == Guid.Empty || limit is < 1 or > 200)
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.PTY, "VALIDATION", "partyId is required and limit must be within 1..200."), HttpContext);
        }

        var item = await queries.GetAsync(protection.Current(context), new PartyId(partyId), cancellationToken).ConfigureAwait(false);
        return Results.Ok(new VulnerabilityQueryPage { Items = item is null ? [] : [item], NextCursor = null, Limit = limit ?? 25 });
    }

    /// <summary>pty.Vulnerability.record.</summary>
    [HttpPost("record")]
    [Authorize(Policy = PartyPermissions.VulnerabilityRecord)]
    public async Task<IResult> RecordAsync(
        [FromBody] VulnerabilityRecordRequest request, [FromServices] ICommandHandler<RecordVulnerability, VulnerabilityRecordResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RecordVulnerability(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.Vulnerability.remove.</summary>
    [HttpPost("remove")]
    [Authorize(Policy = PartyPermissions.VulnerabilityRemove)]
    public async Task<IResult> RemoveAsync(
        [FromBody] VulnerabilityRemoveRequest request, [FromServices] ICommandHandler<RemoveVulnerability, VulnerabilityRemoveResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RemoveVulnerability(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
