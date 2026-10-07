using CoreIns.Modules.Party.Commands;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Party.Api;

/// <summary>Permission names (the operations' <c>x-permission</c>), granted to roles in <c>Platform:Permissions</c>.</summary>
internal static class PartyPermissions
{
    public const string Create = "pty.Party.create";
    public const string Get = "pty.Party.get";
    public const string Search = "pty.Party.search";
    public const string RevealP2 = "pty.Party.revealP2";
    public const string IntermediaryCreate = "pty.Intermediary.create";
    public const string IntermediaryUpdate = "pty.Intermediary.update";
    public const string ProducerCodeValidate = "pty.ProducerCode.validate";
    public const string ProducerCodeSearch = "pty.ProducerCode.search";
}

/// <summary>
/// REST facade of <c>pty.Party.*</c> (contracts/openapi/pty.yaml). Controllers stay thin: bind the contract DTO, send the
/// command through the platform pipeline (or run the query), map the <see cref="Result{T}"/> to HTTP / Problem Details.
/// The Idempotency-Key of commands is enforced by the platform middleware and the pipeline.
/// </summary>
[ApiController]
[Route("api/pty/v1/parties")]
internal sealed class PartiesController : ControllerBase
{
    /// <summary>pty.Party.create → 201 with Location.</summary>
    [HttpPost]
    [Authorize(Policy = PartyPermissions.Create)]
    public async Task<IResult> CreateAsync(
        [FromBody] PartyCreateRequest request, [FromServices] ICommandHandler<CreateParty, PartyCreateResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateParty(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/pty/v1/parties/{result.Value.Party.PartyId.Value}", result.Value)
            : HttpResults.Problem(result.Error, HttpContext);
    }

    /// <summary>pty.Party.get (by id, or by number with <c>partyNumber</c>); P2 masked unless <c>revealPurpose</c> is given.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = PartyPermissions.Get)]
    public async Task<IResult> GetAsync(
        string id, [FromQuery] string? validAt, [FromQuery] string? knownAt, [FromQuery] string? partyNumber, [FromQuery] string? revealPurpose,
        CancellationToken cancellationToken)
    {
        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        if (!TryTime(services.GetRequiredService<IClock>(), validAt, knownAt, out var at))
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.PTY, "VALIDATION", "validAt must be a date or instant, knownAt an instant."), HttpContext);
        }

        Guid? partyId = Guid.TryParse(id, out var guid) ? guid : null;
        var number = partyId is null ? partyNumber ?? id : partyNumber;
        if (revealPurpose is not null)
        {
            context.Reason = revealPurpose;
            var reveal = await services.GetRequiredService<ICommandHandler<RevealParty, PartyGetResponse>>()
                .HandleAsync(new RevealParty(partyId, number, at, revealPurpose), cancellationToken).ConfigureAwait(false);
            return reveal.ToHttpResult(HttpContext);
        }

        var protection = services.GetRequiredService<Domain.PartyProtection>();
        var view = await services.GetRequiredService<PartyReader>()
            .GetAsync(protection.Current(context), context.LegalEntity!.Value, partyId, number, at, revealP2: false, cancellationToken).ConfigureAwait(false);
        return view is null
            ? HttpResults.Problem(DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The party does not exist."), HttpContext)
            : Results.Ok(new PartyGetResponse { Party = view });
    }

    /// <summary>pty.Party.search.</summary>
    [HttpGet("search")]
    [Authorize(Policy = PartyPermissions.Search)]
    public async Task<IResult> SearchAsync(
        [FromQuery] string? criteria, [FromQuery] string? name, [FromQuery] string? identifierScheme, [FromQuery] string? identifierValue,
        [FromQuery] string? partyNumber, [FromQuery] int? limit, [FromQuery] string? cursor, [FromServices] PartySearch search,
        CancellationToken cancellationToken)
    {
        var offset = PartySearch.DecodeCursor(cursor);
        if (offset is null || limit is < 1 or > 200)
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.PTY, "VALIDATION", "cursor is malformed or limit is outside 1..200."), HttpContext);
        }

        var result = await search.SearchAsync(new PartySearchCriteria(criteria, name, identifierScheme, identifierValue, partyNumber, limit ?? 25, offset.Value), cancellationToken)
            .ConfigureAwait(false);
        return result.ToHttpResult(HttpContext);
    }

    /// <summary>Parses <c>validAt</c> (date or instant) and <c>knownAt</c> (instant); both default to now.</summary>
    internal static bool TryTime(IClock clock, string? validAt, string? knownAt, out TimePoint at)
    {
        var now = clock.Now;
        var valid = new BusinessDate(DateOnly.FromDateTime(now.ToUtcDateTime()));
        var known = now;
        at = default;
        if (validAt is not null)
        {
            if (Instant.TryParse(validAt, out var instant))
            {
                valid = new BusinessDate(DateOnly.FromDateTime(instant.ToUtcDateTime()));
            }
            else if (DateOnly.TryParseExact(validAt, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
            {
                valid = new BusinessDate(date);
            }
            else
            {
                return false;
            }
        }

        if (knownAt is not null && !Instant.TryParse(knownAt, out known))
        {
            return false;
        }

        at = new TimePoint(valid, known);
        return true;
    }
}

/// <summary>Problem Details from a domain error.</summary>
internal static class HttpResults
{
    public static IResult Problem(DomainError error, HttpContext http) =>
        http.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(error, http);
}
