using CoreIns.Modules.Party.Commands;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
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

namespace CoreIns.Modules.Party.Api;

/// <summary>REST facade of <c>pty.ProducerOfRecord.*</c> (BO-PTY-09, REQ-PTY-218). The route id of get is the policy id.</summary>
[ApiController]
[Route("api/pty/v1/producer-of-records")]
internal sealed class ProducerOfRecordController : ControllerBase
{
    /// <summary>pty.ProducerOfRecord.get: the producer of record of a policy on a date, with the whole history.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = "pty.ProducerOfRecord.get")]
    public async Task<IResult> GetAsync(
        string id, [FromQuery] string? date, [FromServices] ProducerOfRecordWriter writer, [FromServices] PartyProtection protection, [FromServices] RequestContext context,
        [FromServices] IClock clock, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var guid))
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.PTY, "VALIDATION", "The policy id is not a valid identifier."), HttpContext);
        }

        var on = RoleSupport.Today(clock);
        if (date is not null)
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
            {
                return HttpResults.Problem(DomainError.Of(ModuleCode.PTY, "VALIDATION", "The date must be yyyy-MM-dd."), HttpContext);
            }

            on = new BusinessDate(parsed);
        }

        return Results.Ok(await writer.GetAsync(protection.Current(context), new PolicyId(guid), on, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>pty.ProducerOfRecord.change.</summary>
    [HttpPost("change")]
    [Authorize(Policy = "pty.ProducerOfRecord.change")]
    public async Task<IResult> ChangeAsync(
        [FromBody] ProducerOfRecordChangeRequest request, [FromServices] ICommandHandler<ChangeProducerOfRecord, ProducerOfRecordChangeResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ChangeProducerOfRecord(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.ProducerOfRecord.bulkTransfer.</summary>
    [HttpPost("bulk-transfer")]
    [Authorize(Policy = "pty.ProducerOfRecord.bulkTransfer")]
    public async Task<IResult> BulkTransferAsync(
        [FromBody] ProducerOfRecordBulkTransferRequest request, [FromServices] ICommandHandler<BulkTransferProducerOfRecord, ProducerOfRecordBulkTransferResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new BulkTransferProducerOfRecord(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.ProducerOfRecord.cancel.</summary>
    [HttpPost("cancel")]
    [Authorize(Policy = "pty.ProducerOfRecord.cancel")]
    public async Task<IResult> CancelAsync(
        [FromBody] ProducerOfRecordCancelRequest request, [FromServices] ICommandHandler<CancelProducerOfRecord, ProducerOfRecordCancelResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CancelProducerOfRecord(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
