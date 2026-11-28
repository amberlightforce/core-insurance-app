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

/// <summary>REST facade of <c>pty.Consent.*</c> (BO-PTY-06, REQ-PTY-005, REQ-PTY-150).</summary>
[ApiController]
[Route("api/pty/v1/consents")]
internal sealed class ConsentsController : ControllerBase
{
    /// <summary>pty.Consent.query: the current state of each purpose and channel of a party, or every record with <c>includeHistory</c>.</summary>
    [HttpGet("query")]
    [Authorize(Policy = "pty.Consent.query")]
    public async Task<IResult> QueryAsync(
        [FromQuery] Guid partyId, [FromQuery] string? purpose, [FromQuery] string? channel, [FromQuery] bool? includeHistory, [FromQuery] int? limit,
        [FromServices] PartyDbContext db, [FromServices] PartyProtection protection, [FromServices] RequestContext context, CancellationToken cancellationToken)
    {
        if (partyId == Guid.Empty || limit is < 1 or > 200)
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.PTY, "VALIDATION", "partyId is required and limit must be within 1..200."), HttpContext);
        }

        var legalEntity = protection.Current(context);
        var party = new PartyId(partyId);
        var rows = await db.Consents.AsNoTracking()
            .Where(r => r.LegalEntityId == legalEntity && r.PartyId == party && (purpose == null || r.Purpose == purpose) && (channel == null || r.Channel == channel))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<ConsentRow> selected = includeHistory == true
            ? rows.OrderByDescending(r => r.RecordedAt).ThenByDescending(r => r.ConsentId)
            : ConsentSupport.Current(rows);
        return Results.Ok(new ConsentQueryPage { Items = [.. selected.Take(limit ?? 25).Select(ConsentSupport.ToView)], NextCursor = null, Limit = limit ?? 25 });
    }

    /// <summary>pty.Consent.record.</summary>
    [HttpPost("record")]
    [Authorize(Policy = "pty.Consent.record")]
    public async Task<IResult> RecordAsync(
        [FromBody] ConsentRecordRequest request, [FromServices] ICommandHandler<RecordConsent, ConsentRecordResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RecordConsent(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.Consent.withdraw.</summary>
    [HttpPost("withdraw")]
    [Authorize(Policy = "pty.Consent.withdraw")]
    public async Task<IResult> WithdrawAsync(
        [FromBody] ConsentWithdrawRequest request, [FromServices] ICommandHandler<WithdrawConsent, ConsentWithdrawResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new WithdrawConsent(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}

/// <summary>REST facade of <c>pty.CommunicationPreference.*</c> (BO-PTY-06, REQ-PTY-005).</summary>
[ApiController]
[Route("api/pty/v1/communication-preferences")]
internal sealed class CommunicationPreferencesController : ControllerBase
{
    /// <summary>pty.CommunicationPreference.set.</summary>
    [HttpPost("set")]
    [Authorize(Policy = "pty.CommunicationPreference.set")]
    public async Task<IResult> SetAsync(
        [FromBody] CommunicationPreferenceSetRequest request, [FromServices] ICommandHandler<SetCommunicationPreference, CommunicationPreferenceSetResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SetCommunicationPreference(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.CommunicationPreference.resolve: the effective channel for a purpose on <c>validAt</c> (default today).</summary>
    [HttpPost("resolve")]
    [Authorize(Policy = "pty.CommunicationPreference.resolve")]
    public async Task<IResult> ResolveAsync(
        [FromBody] CommunicationPreferenceResolveRequest request, [FromQuery] string? validAt, [FromServices] CommunicationResolver resolver,
        [FromServices] PartyProtection protection, [FromServices] RequestContext context, [FromServices] IClock clock, CancellationToken cancellationToken)
    {
        if (request.PartyId == default || string.IsNullOrWhiteSpace(request.Purpose) || !PartiesController.TryTime(clock, validAt, null, out var at))
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.PTY, "VALIDATION", "partyId and purpose are required; validAt a date or instant."), HttpContext);
        }

        return (await resolver.ResolveAsync(protection.Current(context), request.PartyId, request.Purpose, at.ValidAt, cancellationToken).ConfigureAwait(false))
            .ToHttpResult(HttpContext);
    }
}
