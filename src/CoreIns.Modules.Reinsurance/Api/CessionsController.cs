using CoreIns.Modules.Reinsurance.Cession;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
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

    /// <summary>ri.Cession.preview: the dry run of the cession intake for a policy, term or hypothetical lines.</summary>
    [HttpPost("api/ri/v1/cessions/preview")]
    [Authorize(Policy = CessionPermissions.Preview)]
    public async Task<IResult> PreviewAsync([FromBody] CessionPreviewRequest request, [FromServices] CessionPreviewService service, CancellationToken cancellationToken) =>
        (await service.PreviewAsync(request, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Cession.rerun: replays one policy or term and writes only the deltas.</summary>
    [HttpPost("api/ri/v1/cessions/rerun")]
    [Authorize(Policy = CessionPermissions.Rerun)]
    public async Task<IResult> RerunAsync(
        [FromBody] CessionRerunRequest request, [FromServices] ICommandHandler<RerunCession, CessionRerunResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RerunCession(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.CessionException.assign.</summary>
    [HttpPost("api/ri/v1/cession-exceptions/assign")]
    [Authorize(Policy = CessionPermissions.ExceptionAssign)]
    public async Task<IResult> AssignAsync(
        [FromBody] CessionExceptionAssignRequest request, [FromServices] ICommandHandler<AssignCessionException, CessionExceptionAssignResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AssignCessionException(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.CessionException.resolve.</summary>
    [HttpPost("api/ri/v1/cession-exceptions/resolve")]
    [Authorize(Policy = CessionPermissions.ExceptionResolve)]
    public async Task<IResult> ResolveAsync(
        [FromBody] CessionExceptionResolveRequest request, [FromServices] ICommandHandler<ResolveCessionException, CessionExceptionResolveResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ResolveCessionException(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.CessionException.rerun.</summary>
    [HttpPost("api/ri/v1/cession-exceptions/rerun")]
    [Authorize(Policy = CessionPermissions.ExceptionRerun)]
    public async Task<IResult> RerunExceptionAsync(
        [FromBody] CessionExceptionRerunRequest request, [FromServices] ICommandHandler<RerunCessionException, CessionExceptionRerunResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RerunCessionException(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}

/// <summary>Permission names of the cession writes (BO-RI-07), granted in <c>permissions/ri.json</c>.</summary>
internal static class CessionPermissions
{
    public const string Preview = "ri.Cession.preview";
    public const string Rerun = "ri.Cession.rerun";
    public const string ExceptionAssign = "ri.CessionException.assign";
    public const string ExceptionResolve = "ri.CessionException.resolve";
    public const string ExceptionRerun = "ri.CessionException.rerun";
}
