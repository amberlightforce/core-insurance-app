using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Claims.Api;

/// <summary>REST facade of <c>clm.ClaimTracking.*</c> and <c>clm.CatEvent.*</c> (BO-CLM-08): reads from the readers, commands through the pipeline.</summary>
[ApiController]
[Route("api/clm/v1")]
internal sealed class TrackingController : ControllerBase
{
    /// <summary>clm.ClaimTracking.get: the id is the claim id or the claim number.</summary>
    [HttpGet("claim-trackings/{id}")]
    [Authorize(Policy = "clm.ClaimTracking.get")]
    public async Task<IResult> GetAsync(string id, [FromServices] ClaimTrackingReader reader, CancellationToken cancellationToken)
    {
        var view = await reader.GetAsync(ClaimsQueryContext.LegalEntity(HttpContext.RequestServices), id, cancellationToken).ConfigureAwait(false);
        return view is null ? HttpResults.Problem(ClaimSupport.NotFound("claim"), HttpContext) : Results.Ok(new ClaimTrackingGetResponse { TrackingView = view });
    }

    /// <summary>clm.ClaimTracking.list: the watch list (open claims by age, oldest first).</summary>
    [HttpGet("claim-trackings")]
    [Authorize(Policy = "clm.ClaimTracking.list")]
    public async Task<IResult> ListAsync(
        [FromQuery] string? claim, [FromQuery] string? party, [FromQuery] string? policy, [FromQuery] string? status, [FromQuery] string? catCode,
        [FromQuery] bool? largeLoss, [FromQuery] int? minAgeDays, [FromQuery] int? limit, [FromQuery] string? cursor,
        [FromServices] ClaimTrackingReader reader, CancellationToken cancellationToken)
    {
        var upper = status?.ToUpperInvariant() ?? ClaimStates.Open;
        if (!ClaimTrackingReader.TryPage(limit, out var take) || (minAgeDays is < 0) || upper is not (ClaimStates.Open or ClaimStates.Closed or ClaimStates.Draft)
            || (party is not null && !Guid.TryParse(party, out _)) || cursor is { Length: > 128 })
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.CLM, "VALIDATION", "limit must be 1..200, status OPEN or CLOSED, party a UUID, minAgeDays at least 0."), HttpContext);
        }

        try
        {
            var page = await reader.ListAsync(
                ClaimsQueryContext.LegalEntity(HttpContext.RequestServices), new TrackingFilter(upper, claim, party, policy, catCode, largeLoss, minAgeDays), cursor, take, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(page);
        }
        catch (FormatException)
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.CLM, "VALIDATION", "The cursor is not valid."), HttpContext);
        }
    }

    /// <summary>clm.CatEvent.list.</summary>
    [HttpGet("cat-events")]
    [Authorize(Policy = "clm.CatEvent.list")]
    public async Task<IResult> ListEventsAsync([FromQuery] string? status, [FromServices] CatEventReader reader, CancellationToken cancellationToken)
    {
        var upper = status?.ToUpperInvariant();
        return upper is not (null or "OPEN" or "CLOSED")
            ? HttpResults.Problem(DomainError.Of(ModuleCode.CLM, "VALIDATION", "status must be OPEN or CLOSED."), HttpContext)
            : Results.Ok(await reader.ListAsync(ClaimsQueryContext.LegalEntity(HttpContext.RequestServices), upper, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>clm.CatEvent.aggregate: totals of the tagged claims by currency and line of business.</summary>
    [HttpGet("cat-events/aggregate")]
    [Authorize(Policy = "clm.CatEvent.aggregate")]
    public async Task<IResult> AggregateAsync(
        [FromQuery] string? validAt, [FromQuery] string? catCode, [FromServices] CatEventReader reader, CancellationToken cancellationToken)
    {
        ValidAt? at = null;
        if (validAt is not null)
        {
            if (CoreIns.SharedKernel.Instant.TryParse(validAt, out var instant))
            {
                at = ValidAt.From(instant);
            }
            else if (CoreIns.SharedKernel.BusinessDate.TryParse(validAt, out var date))
            {
                at = ValidAt.From(date);
            }
            else
            {
                return HttpResults.Problem(DomainError.Of(ModuleCode.CLM, "VALIDATION", "validAt must be a date or an RFC 3339 UTC instant."), HttpContext);
            }
        }

        return Results.Ok(await reader.AggregateAsync(ClaimsQueryContext.LegalEntity(HttpContext.RequestServices), catCode, at, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>clm.CatEvent.declare → 201.</summary>
    [HttpPost("cat-events")]
    [Authorize(Policy = "clm.CatEvent.declare")]
    public async Task<IResult> DeclareAsync(
        [FromBody] CatEventDeclareRequest request, [FromServices] ICommandHandler<DeclareCatEvent, CatEventDeclareResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new DeclareCatEvent(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Created("/api/clm/v1/cat-events", result.Value) : HttpResults.Problem(result.Error!, HttpContext);
    }

    /// <summary>clm.CatEvent.close.</summary>
    [HttpPost("cat-events/close")]
    [Authorize(Policy = "clm.CatEvent.close")]
    public async Task<IResult> CloseAsync(
        [FromBody] CatEventCloseRequest request, [FromServices] ICommandHandler<CloseCatEvent, CatEventCloseResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CloseCatEvent(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
