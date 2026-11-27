using CoreIns.Modules.Policy.Queries;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Policy.Api;

/// <summary>REST facade of the BO-POL-02 read APIs: <c>pol.Policy.getMany</c>, <c>pol.StatusModel.get</c>, <c>pol.EffectiveDate.limits</c>, <c>pol.Segment.changes</c>.</summary>
[ApiController]
[Route("api/pol/v1")]
internal sealed class PolicyReadsController : ControllerBase
{
    /// <summary>pol.Policy.getMany: at most 50 ids (comma-separated or repeated) as of validAt / knownAt.</summary>
    [HttpGet("policies/get-many")]
    [Authorize(Policy = "pol.Policy.getMany")]
    public async Task<IResult> GetManyAsync(
        [FromQuery] string[]? ids, [FromQuery] string? validAt, [FromQuery] string? knownAt, [FromServices] PolicyReads reads, CancellationToken cancellationToken)
    {
        if (!PolicyQueryContext.TryTime(HttpContext.RequestServices, validAt, knownAt, out var valid, out var known))
        {
            return HttpResults.Problem(PolicyQueryContext.BadTime(), HttpContext);
        }

        var all = (ids ?? []).SelectMany(i => i.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        var result = await reads.GetManyAsync(all, valid, known, cancellationToken).ConfigureAwait(false);
        return result.ToHttpResult(HttpContext);
    }

    /// <summary>pol.StatusModel.get.</summary>
    [HttpGet("status-model/get")]
    [Authorize(Policy = "pol.StatusModel.get")]
    public IActionResult GetStatusModel() => Ok(PolicyReads.StatusModel());

    /// <summary>pol.EffectiveDate.limits.</summary>
    [HttpGet("effective-date/limits")]
    [Authorize(Policy = "pol.EffectiveDate.limits")]
    public async Task<IResult> LimitsAsync(
        [FromQuery] string? policyId, [FromQuery] string? txnType, [FromQuery] string? role, [FromQuery] string? channel, [FromServices] PolicyReads reads, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(policyId, out var id))
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.POL, "VALIDATION", "policyId must be a UUID."), HttpContext);
        }

        var result = await reads.LimitsAsync(id, txnType, role, cancellationToken).ConfigureAwait(false);
        return result.ToHttpResult(HttpContext);
    }

    /// <summary>pol.Segment.changes.</summary>
    [HttpGet("segments/changes")]
    [Authorize(Policy = "pol.Segment.changes")]
    public async Task<IResult> SegmentChangesAsync(
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] string? since, [FromQuery] string? sinceTransactionId, [FromServices] PolicyReads reads, CancellationToken cancellationToken)
    {
        Instant? sinceAt = null;
        Guid? sinceTx = null;
        if (since is not null)
        {
            if (!Instant.TryParse(since, out var parsed))
            {
                return HttpResults.Problem(DomainError.Of(ModuleCode.POL, "VALIDATION", "since must be an instant."), HttpContext);
            }

            sinceAt = parsed;
        }

        if (sinceTransactionId is not null)
        {
            if (!Guid.TryParse(sinceTransactionId, out var tx))
            {
                return HttpResults.Problem(DomainError.Of(ModuleCode.POL, "VALIDATION", "sinceTransactionId must be a UUID."), HttpContext);
            }

            sinceTx = tx;
        }

        var result = await reads.SegmentChangesAsync(cursor, limit, sinceAt, sinceTx, cancellationToken).ConfigureAwait(false);
        return result.ToHttpResult(HttpContext);
    }
}
