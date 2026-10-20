using CoreIns.Modules.Reinsurance.Cession;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Reinsurance.Api;

/// <summary>
/// REST facade of the cession reads (<c>ri.Cession.listByPolicy / get</c>, <c>ri.CessionException.list</c>; contracts/openapi/ri.yaml):
/// thin, over <see cref="RiReadModels"/>; keyset paging with a capped page size. The writes (preview, rerun, assign, resolve) are not built.
/// </summary>
[ApiController]
internal sealed class CessionsController : ControllerBase
{
    /// <summary>ri.Cession.listByPolicy.</summary>
    [HttpGet("api/ri/v1/cessions/list-by-policy")]
    [Authorize(Policy = ReinsurancePermissions.CessionListByPolicy)]
    public async Task<IResult> ListByPolicyAsync(
        [FromQuery] Guid? policy, [FromQuery] Guid? term, [FromQuery] string? knownAt, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] RiReadModels reader, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(knownAt) && !Instant.TryParse(knownAt, out _))
        {
            return RiHttp.Problem(RiErrors.Validation("knownAt", "KNOWN_AT", "knownAt must be an instant."), HttpContext);
        }

        Instant? at = !string.IsNullOrWhiteSpace(knownAt) && Instant.TryParse(knownAt, out var parsed) ? parsed : null;
        var page = await reader.ListByPolicyAsync(policy ?? Guid.Empty, term, at, cursor, limit, cancellationToken).ConfigureAwait(false);
        return page.IsFailure
            ? RiHttp.Problem(page.Error!, HttpContext)
            : Results.Ok(new CessionListByPolicyPage { Items = page.Value.Items, NextCursor = page.Value.Next, Limit = page.Value.Limit });
    }

    /// <summary>ri.Cession.get.</summary>
    [HttpGet("api/ri/v1/cessions/{id}")]
    [Authorize(Policy = ReinsurancePermissions.CessionGet)]
    public async Task<IResult> GetAsync(string id, [FromServices] RiReadModels reader, CancellationToken cancellationToken)
    {
        var view = Guid.TryParse(id, out var cessionId) ? await reader.GetCessionAsync(cessionId, cancellationToken).ConfigureAwait(false) : null;
        return view is null ? RiHttp.Problem(RiErrors.NotFound(), HttpContext) : Results.Ok(view);
    }

    /// <summary>ri.CessionException.list.</summary>
    [HttpGet("api/ri/v1/cession-exceptions")]
    [Authorize(Policy = ReinsurancePermissions.CessionExceptionList)]
    public async Task<IResult> ListExceptionsAsync([FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] RiReadModels reader, CancellationToken cancellationToken)
    {
        var page = await reader.ListExceptionsAsync(cursor, limit, cancellationToken).ConfigureAwait(false);
        return page.IsFailure
            ? RiHttp.Problem(page.Error!, HttpContext)
            : Results.Ok(new CessionExceptionListPage { Items = page.Value.Items, NextCursor = page.Value.Next, Limit = page.Value.Limit });
    }
}
