using CoreIns.SharedKernel.Results;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Commands;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Party.Api;

/// <summary>
/// REST facade of sanctions screening: <c>screen</c> and <c>bulk</c> over the bound <see cref="IPartyScreeningService"/> (the engine over the
/// registered provider; fail closed with PTY-ERR-NOT-AVAILABLE in Production without an adapter), and the case queue (<c>caseGet</c>,
/// <c>caseDecide</c>, BO-PTY-07). Screening input travels in the body only (D-SLC-05).
/// </summary>
[ApiController]
[Route("api/pty/v1/screening")]
internal sealed class ScreeningController : ControllerBase
{
    /// <summary>pty.Screening.screen.</summary>
    [HttpPost("screen")]
    [Authorize(Policy = "pty.Screening.screen")]
    public async Task<IResult> ScreenAsync(
        [FromBody] ScreeningScreenRequest body, [FromServices] IPartyScreeningService screening, CancellationToken cancellationToken)
    {
        var context = HttpContext.RequestServices.GetRequiredService<RequestContext>();
        var key = context.IdempotencyKey ?? IdempotencyKey.New();
        return Results.Ok(await screening.ScreenAsync(body, new CommandOptions(key), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>pty.Screening.bulk: screens the listed parties; every hit opens a case.</summary>
    [HttpPost("bulk")]
    [Authorize(Policy = "pty.Screening.bulk")]
    public async Task<IResult> BulkAsync(
        [FromBody] ScreeningBulkRequest body, [FromServices] IPartyScreeningService screening, CancellationToken cancellationToken)
    {
        var context = HttpContext.RequestServices.GetRequiredService<RequestContext>();
        var key = context.IdempotencyKey ?? IdempotencyKey.New();
        return Results.Accepted(null, await screening.BulkAsync(body, new CommandOptions(key), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>pty.Screening.caseDecide: propose, approve or reject a decision on a case (maker-checker).</summary>
    [HttpPost("case-decide")]
    [Authorize(Policy = "pty.Screening.caseDecide")]
    public async Task<IResult> CaseDecideAsync(
        [FromBody] ScreeningCaseDecideRequest body, [FromServices] ICommandHandler<DecideScreeningCase, ScreeningCaseDecideResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DecideScreeningCase(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.Screening.caseGet: the case queue, newest first.</summary>
    [HttpGet("case-get")]
    [Authorize(Policy = "pty.Screening.caseGet")]
    public async Task<IResult> CaseGetAsync(
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] string? status, [FromQuery] Guid? partyId, [FromQuery] Guid? caseId,
        [FromServices] ScreeningCaseQueries queries, [FromServices] PartyProtection protection, [FromServices] RequestContext context, CancellationToken cancellationToken)
    {
        var offset = PartySearch.DecodeCursor(cursor);
        string[] statuses = ["OPEN", "PENDING_APPROVAL", "CLEARED", "TRUE_MATCH"];
        if (offset is null || limit is < 1 or > 200 || (status is not null && !statuses.Contains(status)))
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.PTY, "VALIDATION", "cursor is malformed, limit is outside 1..200 or status is unknown."), HttpContext);
        }

        var size = limit ?? 25;
        var (items, hasMore) = await queries.ListAsync(protection.Current(context), status, partyId, caseId, offset.Value, size, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new ScreeningCaseGetResponse { Items = items, NextCursor = hasMore ? PartySearch.EncodeCursor(offset.Value + size) : null, Limit = size });
    }
}
