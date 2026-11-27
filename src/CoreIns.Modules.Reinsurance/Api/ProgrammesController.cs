using CoreIns.Modules.Reinsurance.Cession;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Reinsurance.Api;

/// <summary>
/// REST facade of the programme and participation reads (<c>ri.Programme.list / get</c>, <c>ri.Participation.list</c>; contracts/openapi/ri.yaml):
/// thin: programme commands go through the pipeline (BO-RI-01), reads through <see cref="ProgrammeReader"/> and <see cref="RiReadModels"/>; keyset paging with a capped page size.
/// </summary>
[ApiController]
internal sealed class ProgrammesController : ControllerBase
{
    /// <summary>ri.Programme.create → 201 (BO-RI-01).</summary>
    [HttpPost("api/ri/v1/programmes")]
    [Authorize(Policy = ReinsurancePermissions.ProgrammeCreate)]
    public async Task<IResult> CreateAsync(
        [FromBody] ProgrammeCreateRequest request, [FromServices] ICommandHandler<CreateProgramme, ProgrammeCreateResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateProgramme(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/ri/v1/programmes/{result.Value.Programme.ProgrammeId.Value:D}", result.Value)
            : RiHttp.Problem(result.Error!, HttpContext);
    }

    /// <summary>ri.Programme.update (PATCH): edits a Draft in place, or starts the next version of an approved programme.</summary>
    [HttpPatch("api/ri/v1/programmes/{id}")]
    [Authorize(Policy = ReinsurancePermissions.ProgrammeUpdate)]
    public async Task<IResult> UpdateAsync(
        string id, [FromBody] ProgrammeUpdateRequest request, [FromServices] ICommandHandler<UpdateProgramme, ProgrammeUpdateResponse> handler, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var programmeId) || programmeId == Guid.Empty)
        {
            return RiHttp.Problem(ProgrammeSupport.NotFound(), HttpContext);
        }

        return (await handler.HandleAsync(new UpdateProgramme(new RiProgrammeId(programmeId), request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }

    /// <summary>ri.Programme.renew: copies the programme (and its contracts, as Drafts) to the next year.</summary>
    [HttpPost("api/ri/v1/programmes/renew")]
    [Authorize(Policy = ReinsurancePermissions.ProgrammeRenew)]
    public async Task<IResult> RenewAsync(
        [FromBody] ProgrammeRenewRequest request, [FromServices] ICommandHandler<RenewProgramme, ProgrammeRenewResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RenewProgramme(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Programme.list.</summary>
    [HttpGet("api/ri/v1/programmes")]
    [Authorize(Policy = ReinsurancePermissions.ProgrammeList)]
    public async Task<IResult> ListAsync([FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] ProgrammeReader reader, CancellationToken cancellationToken)
    {
        var page = await reader.ListAsync(cursor, limit, cancellationToken).ConfigureAwait(false);
        return page.IsFailure
            ? RiHttp.Problem(page.Error!, HttpContext)
            : Results.Ok(new ProgrammeListPage { Items = page.Value.Items, NextCursor = page.Value.Next, Limit = page.Value.Limit });
    }

    /// <summary>ri.Programme.get.</summary>
    [HttpGet("api/ri/v1/programmes/{id}")]
    [Authorize(Policy = ReinsurancePermissions.ProgrammeGet)]
    public async Task<IResult> GetAsync(string id, [FromServices] ProgrammeReader reader, CancellationToken cancellationToken)
    {
        var view = Guid.TryParse(id, out var programmeId) ? await reader.GetAsync(programmeId, cancellationToken).ConfigureAwait(false) : null;
        return view is null ? RiHttp.Problem(ProgrammeSupport.NotFound(), HttpContext) : Results.Ok(new ProgrammeGetResponse { Programme = view });
    }

    /// <summary>ri.Participation.list (optionally for one contract).</summary>
    [HttpGet("api/ri/v1/participations")]
    [Authorize(Policy = ReinsurancePermissions.ParticipationList)]
    public async Task<IResult> ListParticipationsAsync(
        [FromQuery] Guid? contractId, [FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] RiReadModels reader, CancellationToken cancellationToken)
    {
        var page = await reader.ListParticipationsAsync(contractId, cursor, limit, cancellationToken).ConfigureAwait(false);
        return page.IsFailure
            ? RiHttp.Problem(page.Error!, HttpContext)
            : Results.Ok(new ParticipationListPage { Items = page.Value.Items, NextCursor = page.Value.Next, Limit = page.Value.Limit });
    }
}
