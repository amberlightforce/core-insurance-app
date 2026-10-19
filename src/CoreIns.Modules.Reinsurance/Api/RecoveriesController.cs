using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Recovery;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Reinsurance.Api;

/// <summary>
/// REST facade of <c>ri.Recovery.*</c> queries (contracts/openapi/ri.yaml, REQ-RI-136): thin, over <see cref="RecoveryReader"/>.
/// Reads are scoped to the caller's legal entity; <c>knownAt</c> answers "as recorded at that time".
/// </summary>
[ApiController]
[Route("api/ri/v1/recoveries")]
internal sealed class RecoveriesController : ControllerBase
{
    /// <summary>ri.Recovery.listByClaim.</summary>
    [HttpGet("list-by-claim")]
    [Authorize(Policy = ReinsurancePermissions.RecoveryListByClaim)]
    public async Task<IResult> ListByClaimAsync(
        [FromQuery] Guid claimId, [FromQuery] string? knownAt, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] RecoveryReader reader, CancellationToken cancellationToken)
    {
        if (!TryKnownAt(knownAt, out var at))
        {
            return RiHttp.Problem(RiErrors.Validation("knownAt", "KNOWN_AT", "knownAt must be an instant."), HttpContext);
        }

        var page = await reader.ByClaimAsync(claimId, at, cursor, limit, cancellationToken).ConfigureAwait(false);
        return page.IsFailure
            ? RiHttp.Problem(page.Error!, HttpContext)
            : Results.Ok(new RecoveryListByClaimPage { Items = [.. page.Value.Items.Select(RecoveryReader.ToClaimItem)], NextCursor = page.Value.Next, Limit = page.Value.Limit });
    }

    /// <summary>ri.Recovery.listByContract.</summary>
    [HttpGet("list-by-contract")]
    [Authorize(Policy = ReinsurancePermissions.RecoveryListByContract)]
    public async Task<IResult> ListByContractAsync(
        [FromQuery] Guid contractId, [FromQuery] Guid? claimId, [FromQuery] string? knownAt, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] RecoveryReader reader, CancellationToken cancellationToken)
    {
        if (!TryKnownAt(knownAt, out var at))
        {
            return RiHttp.Problem(RiErrors.Validation("knownAt", "KNOWN_AT", "knownAt must be an instant."), HttpContext);
        }

        var page = await reader.ByContractAsync(contractId, claimId, at, cursor, limit, cancellationToken).ConfigureAwait(false);
        return page.IsFailure
            ? RiHttp.Problem(page.Error!, HttpContext)
            : Results.Ok(new RecoveryListByContractPage { Items = [.. page.Value.Items.Select(RecoveryReader.ToContractItem)], NextCursor = page.Value.Next, Limit = page.Value.Limit });
    }

    /// <summary>ri.Recovery.trace: the calculation trace of one batch (REQ-RI-132).</summary>
    [HttpGet("trace")]
    [Authorize(Policy = ReinsurancePermissions.RecoveryTrace)]
    public async Task<IResult> TraceAsync([FromQuery] Guid batchId, [FromServices] RecoveryReader reader, CancellationToken cancellationToken)
    {
        var trace = await reader.TraceAsync(batchId, cancellationToken).ConfigureAwait(false);
        return trace is null ? RiHttp.Problem(RiErrors.NotFound(), HttpContext) : Results.Ok(trace);
    }

    private static bool TryKnownAt(string? text, out Instant? knownAt)
    {
        knownAt = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (Instant.TryParse(text, out var instant))
        {
            knownAt = instant;
            return true;
        }

        return false;
    }
}
