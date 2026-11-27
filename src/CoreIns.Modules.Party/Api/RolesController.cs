using CoreIns.Modules.Party.Commands;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Modules.Party.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Party.Api;

/// <summary>REST facade of <c>pty.PartyRole.*</c> (BO-PTY-03, REQ-PTY-002).</summary>
[ApiController]
[Route("api/pty/v1/party-roles")]
internal sealed class PartyRolesController : ControllerBase
{
    /// <summary>pty.PartyRole.assign.</summary>
    [HttpPost("assign")]
    [Authorize(Policy = "pty.PartyRole.assign")]
    public async Task<IResult> AssignAsync(
        [FromBody] PartyRoleAssignRequest request, [FromServices] ICommandHandler<AssignPartyRole, PartyRoleAssignResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AssignPartyRole(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.PartyRole.end.</summary>
    [HttpPost("end")]
    [Authorize(Policy = "pty.PartyRole.end")]
    public async Task<IResult> EndAsync(
        [FromBody] PartyRoleEndRequest request, [FromServices] ICommandHandler<EndPartyRole, PartyRoleEndResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new EndPartyRole(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.PartyRole.query: the roles of one party as known at <c>knownAt</c> and valid on <c>validAt</c> (both default to now).</summary>
    [HttpGet("query")]
    [Authorize(Policy = "pty.PartyRole.query")]
    public async Task<IResult> QueryAsync(
        [FromQuery] Guid partyId, [FromQuery] string? roleType, [FromQuery] string? validAt, [FromQuery] string? knownAt, [FromQuery] int? limit,
        [FromServices] PartyDbContext db, [FromServices] PartyProtection protection, [FromServices] RequestContext context, [FromServices] IClock clock,
        CancellationToken cancellationToken)
    {
        if (partyId == Guid.Empty || limit is < 1 or > 200 || !PartiesController.TryTime(clock, validAt, knownAt, out var at))
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.PTY, "VALIDATION", "partyId is required, limit within 1..200, validAt a date or instant, knownAt an instant."), HttpContext);
        }

        var legalEntity = protection.Current(context);
        var party = new PartyId(partyId);
        var rows = await db.Roles.AsNoTracking()
            .Where(r => r.LegalEntityId == legalEntity && r.PartyId == party && (roleType == null || r.RoleType == roleType)
                && r.RecordedFrom <= at.KnownAt && (r.RecordedTo == null || r.RecordedTo > at.KnownAt))
            .OrderBy(r => r.ValidFrom).ThenBy(r => r.RoleId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Where(r => new DateRange(r.ValidFrom, r.ValidTo).Contains(at.ValidAt)).Take(limit ?? 25).Select(RoleSupport.ToView).ToList();
        return Results.Ok(new PartyRoleQueryPage { Items = page, NextCursor = null, Limit = limit ?? 25 });
    }
}

/// <summary>REST facade of <c>pty.Relationship.*</c> (BO-PTY-03, REQ-PTY-104).</summary>
[ApiController]
[Route("api/pty/v1/relationships")]
internal sealed class RelationshipsController : ControllerBase
{
    /// <summary>pty.Relationship.add.</summary>
    [HttpPost("add")]
    [Authorize(Policy = "pty.Relationship.add")]
    public async Task<IResult> AddAsync(
        [FromBody] RelationshipAddRequest request, [FromServices] ICommandHandler<AddRelationship, RelationshipAddResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AddRelationship(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.Relationship.end.</summary>
    [HttpPost("end")]
    [Authorize(Policy = "pty.Relationship.end")]
    public async Task<IResult> EndAsync(
        [FromBody] RelationshipEndRequest request, [FromServices] ICommandHandler<EndRelationship, RelationshipEndResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new EndRelationship(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.Relationship.query: every relationship a party is on either side of, as known at <c>knownAt</c> and valid on <c>validAt</c>.</summary>
    [HttpGet("query")]
    [Authorize(Policy = "pty.Relationship.query")]
    public async Task<IResult> QueryAsync(
        [FromQuery] Guid partyId, [FromQuery] string? relationshipType, [FromQuery] string? validAt, [FromQuery] string? knownAt, [FromQuery] int? limit,
        [FromServices] PartyDbContext db, [FromServices] PartyProtection protection, [FromServices] RequestContext context, [FromServices] IClock clock,
        CancellationToken cancellationToken)
    {
        if (partyId == Guid.Empty || limit is < 1 or > 200 || !PartiesController.TryTime(clock, validAt, knownAt, out var at))
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.PTY, "VALIDATION", "partyId is required, limit within 1..200, validAt a date or instant, knownAt an instant."), HttpContext);
        }

        var legalEntity = protection.Current(context);
        var party = new PartyId(partyId);
        var rows = await db.Relationships.AsNoTracking()
            .Where(r => r.LegalEntityId == legalEntity && (r.FromPartyId == party || r.ToPartyId == party)
                && (relationshipType == null || r.RelationshipType == relationshipType)
                && r.RecordedFrom <= at.KnownAt && (r.RecordedTo == null || r.RecordedTo > at.KnownAt))
            .OrderBy(r => r.ValidFrom).ThenBy(r => r.RelationshipId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Where(r => new DateRange(r.ValidFrom, r.ValidTo).Contains(at.ValidAt)).Take(limit ?? 25).Select(RoleSupport.ToView).ToList();
        return Results.Ok(new RelationshipQueryPage { Items = page, NextCursor = null, Limit = limit ?? 25 });
    }
}
