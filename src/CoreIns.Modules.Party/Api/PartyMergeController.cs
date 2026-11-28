using CoreIns.Modules.Party.Commands;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Party.Api;

/// <summary>
/// REST facade of duplicate detection, merge and unmerge (BO-PTY-05, REQ-PTY-007): <c>pty.Party.duplicates</c>, <c>merge</c>, <c>approveMerge</c>,
/// <c>unmerge</c>, <c>mergeList</c> and <c>resolveSurvivor</c>. Literal routes sit beside <c>PartiesController</c>'s <c>{id}</c> routes and win over them.
/// </summary>
[ApiController]
[Route("api/pty/v1/parties")]
internal sealed class PartyMergeController : ControllerBase
{
    /// <summary>pty.Party.duplicates: the queue of suspected duplicate pairs with score and reasons.</summary>
    [HttpGet("duplicates")]
    [Authorize(Policy = "pty.Party.duplicates")]
    public async Task<IResult> DuplicatesAsync(
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] int? minScore, [FromQuery] Guid? partyId, [FromServices] DuplicateFinder finder,
        CancellationToken cancellationToken)
    {
        var offset = PartySearch.DecodeCursor(cursor);
        if (offset is null || limit is < 1 or > 200 || minScore is < 0 or > 100)
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.PTY, "VALIDATION", "cursor is malformed, limit is outside 1..200 or minScore outside 0..100."), HttpContext);
        }

        var size = limit ?? 25;
        var (page, hasMore) = await finder.FindAsync(partyId, minScore ?? 0, offset.Value, size, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new DuplicateCandidatePage { Items = page, NextCursor = hasMore ? PartySearch.EncodeCursor(offset.Value + size) : null, Limit = size });
    }

    /// <summary>pty.Party.merge: the maker's request; opens the PLT approval.</summary>
    [HttpPost("merge")]
    [Authorize(Policy = "pty.Party.merge")]
    public async Task<IResult> MergeAsync(
        [FromBody] PartyMergeRequest request, [FromServices] ICommandHandler<RequestPartyMerge, PartyMergeResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RequestPartyMerge(request), cancellationToken).ConfigureAwait(false);
        return result.ToHttpResult(HttpContext);
    }

    /// <summary>pty.Party.approveMerge: the checker's decision; approval executes the merge.</summary>
    [HttpPost("merge/approve")]
    [Authorize(Policy = "pty.Party.approveMerge")]
    public async Task<IResult> ApproveAsync(
        [FromBody] PartyMergeApproveRequest request, [FromServices] ICommandHandler<DecidePartyMerge, PartyMergeApproveResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DecidePartyMerge(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.Party.unmerge: reverses an executed merge inside its window.</summary>
    [HttpPost("unmerge")]
    [Authorize(Policy = "pty.Party.unmerge")]
    public async Task<IResult> UnmergeAsync(
        [FromBody] PartyUnmergeRequest request, [FromServices] ICommandHandler<UnmergeParty, PartyUnmergeResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new UnmergeParty(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.Party.mergeList: merge records, newest first.</summary>
    [HttpGet("merges")]
    [Authorize(Policy = "pty.Party.mergeList")]
    public async Task<IResult> MergesAsync(
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] string? status, [FromQuery] Guid? partyId, [FromQuery] Guid? mergeId,
        [FromServices] PartyMergeQueries queries, [FromServices] PartyProtection protection, [FromServices] RequestContext context, CancellationToken cancellationToken)
    {
        var offset = PartySearch.DecodeCursor(cursor);
        string[] statuses = [MergeStatus.Pending, MergeStatus.Executed, MergeStatus.Rejected, MergeStatus.Unmerged];
        if (offset is null || limit is < 1 or > 200 || (status is not null && !statuses.Contains(status)))
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.PTY, "VALIDATION", "cursor is malformed, limit is outside 1..200 or status is unknown."), HttpContext);
        }

        var size = limit ?? 25;
        var (items, hasMore) = await queries.ListAsync(protection.Current(context), status, partyId, mergeId, offset.Value, size, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new PartyMergePage { Items = items, NextCursor = hasMore ? PartySearch.EncodeCursor(offset.Value + size) : null, Limit = size });
    }

    /// <summary>pty.Party.resolveSurvivor: the party that survives the merge chain of {id}.</summary>
    [HttpGet("{id:guid}/survivor")]
    [Authorize(Policy = "pty.Party.get")]
    public async Task<IResult> SurvivorAsync(
        Guid id, [FromServices] PartyMergeQueries queries, [FromServices] PartyProtection protection, [FromServices] RequestContext context, CancellationToken cancellationToken)
    {
        var survivor = await queries.ResolveSurvivorAsync(protection.Current(context), id, cancellationToken).ConfigureAwait(false);
        return survivor is null
            ? HttpResults.Problem(DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The party does not exist."), HttpContext)
            : Results.Ok(survivor);
    }
}
