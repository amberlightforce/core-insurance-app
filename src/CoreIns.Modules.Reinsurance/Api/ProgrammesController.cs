using CoreIns.Modules.Reinsurance.Cession;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Registry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Reinsurance.Api;

/// <summary>
/// REST facade of the programme and participation reads (<c>ri.Programme.list / get</c>, <c>ri.Participation.list</c>; contracts/openapi/ri.yaml):
/// thin, over <see cref="RiReadModels"/>; keyset paging with a capped page size. The programme writes are not built.
/// </summary>
[ApiController]
internal sealed class ProgrammesController : ControllerBase
{
    /// <summary>ri.Programme.list.</summary>
    [HttpGet("api/ri/v1/programmes")]
    [Authorize(Policy = ReinsurancePermissions.ProgrammeList)]
    public async Task<IResult> ListAsync([FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] RiReadModels reader, CancellationToken cancellationToken)
    {
        var page = await reader.ListProgrammesAsync(cursor, limit, cancellationToken).ConfigureAwait(false);
        return page.IsFailure
            ? RiHttp.Problem(page.Error!, HttpContext)
            : Results.Ok(new ProgrammeListPage { Items = page.Value.Items, NextCursor = page.Value.Next, Limit = page.Value.Limit });
    }

    /// <summary>ri.Programme.get.</summary>
    [HttpGet("api/ri/v1/programmes/{id}")]
    [Authorize(Policy = ReinsurancePermissions.ProgrammeGet)]
    public async Task<IResult> GetAsync(string id, [FromServices] RiReadModels reader, CancellationToken cancellationToken)
    {
        var view = await reader.GetProgrammeAsync(id, cancellationToken).ConfigureAwait(false);
        return view is null ? RiHttp.Problem(RiErrors.NotFound(), HttpContext) : Results.Ok(view);
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
