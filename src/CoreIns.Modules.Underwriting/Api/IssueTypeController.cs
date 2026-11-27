using CoreIns.Modules.Underwriting.Commands;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Underwriting.Api;

/// <summary>REST facade of the issue-type catalogue (BO-UW-02).</summary>
[ApiController]
[Route("api/uw/v1/issue-types")]
internal sealed class IssueTypeController : ControllerBase
{
    /// <summary>uw.IssueType.search: the catalogue by code, optionally filtered by text and active.</summary>
    [HttpGet("search")]
    [Authorize(Policy = "uw.IssueType.search")]
    public async Task<IResult> SearchAsync(
        [FromQuery] string? q, [FromQuery] bool? activeOnly, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] IssueTypeQueries queries, CancellationToken cancellationToken)
    {
        var result = await queries.SearchAsync(q, activeOnly, cursor, limit, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.IssueType.get: one issue type by code.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = "uw.IssueType.get")]
    public async Task<IResult> GetAsync(string id, [FromServices] IssueTypeQueries queries, CancellationToken cancellationToken)
    {
        var result = await queries.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.IssueType.upsert: creates the issue type, or changes it with its recordVersion.</summary>
    [HttpPost("upsert")]
    [Authorize(Policy = "uw.IssueType.upsert")]
    public async Task<IResult> UpsertAsync(
        [FromBody] IssueTypeUpsertRequest request, [FromServices] ICommandHandler<UpsertIssueType, IssueTypeItem> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new UpsertIssueType(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    private static IResult Problem(DomainError error, HttpContext http) =>
        http.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(error, http);
}
