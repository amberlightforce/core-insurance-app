using CoreIns.Modules.Party.Commands;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Party.Api;

/// <summary>REST facade of <c>pty.Intermediary.create</c> / <c>update</c> (SL-0 subset of W2-PTY-04).</summary>
[ApiController]
[Route("api/pty/v1/intermediaries")]
internal sealed class IntermediariesController : ControllerBase
{
    /// <summary>pty.Intermediary.create → 201.</summary>
    [HttpPost]
    [Authorize(Policy = PartyPermissions.IntermediaryCreate)]
    public async Task<IResult> CreateAsync(
        [FromBody] IntermediaryCreateRequest request,
        [FromServices] ICommandHandler<CreateIntermediary, IntermediaryCreateResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateIntermediary(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/pty/v1/intermediaries/{result.Value.Intermediary.IntermediaryId.Value}", result.Value)
            : HttpResults.Problem(result.Error, HttpContext);
    }

    /// <summary>pty.Intermediary.update (activation).</summary>
    [HttpPatch("{id:guid}")]
    [Authorize(Policy = PartyPermissions.IntermediaryUpdate)]
    public async Task<IResult> UpdateAsync(
        Guid id,
        [FromBody] IntermediaryUpdateRequest request,
        [FromServices] ICommandHandler<UpdateIntermediary, IntermediaryUpdateResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new UpdateIntermediary(new IntermediaryId(id), request), cancellationToken).ConfigureAwait(false);
        return result.ToHttpResult(HttpContext);
    }
}

/// <summary>REST facade of <c>pty.ProducerCode.validate</c> / <c>search</c>.</summary>
[ApiController]
[Route("api/pty/v1/producer-codes")]
internal sealed class ProducerCodesController : ControllerBase
{
    /// <summary>pty.ProducerCode.validate: a pure read sent as POST (no Idempotency-Key, contract x-operation-kind query).</summary>
    [HttpPost("validate")]
    [SkipIdempotency]
    [Authorize(Policy = PartyPermissions.ProducerCodeValidate)]
    public async Task<IResult> ValidateAsync(
        [FromBody] ProducerCodeValidateRequest request,
        [FromQuery] string? validAt,
        [FromServices] IntermediaryQueries queries,
        [FromServices] IClock clock,
        CancellationToken cancellationToken)
    {
        if (!PartiesController.TryTime(clock, validAt, null, out var at))
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.PTY, "VALIDATION", "validAt must be a date or instant."), HttpContext);
        }

        return Results.Ok(await queries.ValidateAsync(request, at.ValidAt, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>pty.ProducerCode.search by code prefix or intermediary name.</summary>
    [HttpGet("search")]
    [Authorize(Policy = PartyPermissions.ProducerCodeSearch)]
    public async Task<IResult> SearchAsync(
        [FromQuery] string? code,
        [FromQuery] string? name,
        [FromQuery] int? limit,
        [FromQuery] string? cursor,
        [FromServices] IntermediaryQueries queries,
        [FromServices] NameForms names,
        CancellationToken cancellationToken)
    {
        var offset = PartySearch.DecodeCursor(cursor);
        if (offset is null || limit is < 1 or > 200)
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.PTY, "VALIDATION", "cursor is malformed or limit is outside 1..200."), HttpContext);
        }

        var size = limit ?? 25;
        var key = string.IsNullOrWhiteSpace(name) ? null : names.Key(name);
        var items = await queries.SearchAsync(string.IsNullOrWhiteSpace(code) ? null : code.Trim(), key, size + 1, offset.Value, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new ProducerCodeSearchPage
        {
            Items = [.. items.Take(size)],
            NextCursor = items.Count > size ? PartySearch.EncodeCursor(offset.Value + size) : null,
            Limit = size,
        });
    }
}
