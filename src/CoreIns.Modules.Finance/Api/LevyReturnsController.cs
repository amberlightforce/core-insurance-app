using CoreIns.Modules.Finance.Commands;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Modules.Finance.Services;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Finance.Api;

/// <summary>Permissions of the levy return operations (the operations' <c>x-permission</c>).</summary>
internal static class LevyReturnPermissions
{
    public const string List = "fin.LevyReturn.list";
    public const string Get = "fin.LevyReturn.get";
    public const string Recalculate = "fin.LevyReturn.recalculate";
    public const string AddAdjustment = "fin.LevyReturn.addAdjustment";
    public const string GenerateFile = "fin.LevyReturn.generateFile";
    public const string Transition = "fin.LevyReturn.transition";
    public const string RecordFiling = "fin.LevyReturn.recordFiling";
    public const string RequestPayment = "fin.LevyReturn.requestPayment";
}

/// <summary>REST facade of <c>fin.LevyReturn.*</c> (BO-FIN-10, REQ-FIN-005, -189..-196): levy returns from the ledger, review, file, filing and the BIL payment.</summary>
[ApiController]
[Route("api/fin/v1/levy-returns")]
internal sealed class LevyReturnsController : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = LevyReturnPermissions.List)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? period, [FromQuery] string? levyType, [FromQuery] string? status, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] LevyReturnService service, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > FinanceHttp.MaxLimit)
        {
            return this.Problem(FinanceHttp.Invalid($"limit must be within 1..{FinanceHttp.MaxLimit}."));
        }

        return (await service.ListAsync(period, levyType, status, cursor, limit ?? FinanceHttp.DefaultLimit, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }

    [HttpGet("{id}")]
    [Authorize(Policy = LevyReturnPermissions.Get)]
    public async Task<IResult> GetAsync(string id, [FromServices] LevyReturnService service, CancellationToken cancellationToken) =>
        Guid.TryParse(id, out var returnId)
            ? (await service.GetAsync(returnId, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext)
            : this.Problem(FinanceHttp.Invalid("id must be a UUID."));

    [HttpPost("recalculate")]
    [Authorize(Policy = LevyReturnPermissions.Recalculate)]
    public async Task<IResult> RecalculateAsync([FromBody] LevyReturnRecalculateRequest request, [FromServices] ICommandHandler<RecalculateLevyReturn, LevyReturnRecalculateResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RecalculateLevyReturn(request.Period, request.LevyType), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("add-adjustment")]
    [Authorize(Policy = LevyReturnPermissions.AddAdjustment)]
    public async Task<IResult> AddAdjustmentAsync([FromBody] LevyReturnAddAdjustmentRequest request, [FromServices] ICommandHandler<AddLevyReturnAdjustment, LevyReturnAddAdjustmentResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AddLevyReturnAdjustment(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("generate-file")]
    [Authorize(Policy = LevyReturnPermissions.GenerateFile)]
    public async Task<IResult> GenerateFileAsync([FromBody] LevyReturnGenerateFileRequest request, [FromServices] ICommandHandler<GenerateLevyReturnFile, LevyReturnGenerateFileResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new GenerateLevyReturnFile(request.ReturnId), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("transition")]
    [Authorize(Policy = LevyReturnPermissions.Transition)]
    public async Task<IResult> TransitionAsync([FromBody] LevyReturnTransitionRequest request, [FromServices] ICommandHandler<TransitionLevyReturn, LevyReturnTransitionResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new TransitionLevyReturn(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("record-filing")]
    [Authorize(Policy = LevyReturnPermissions.RecordFiling)]
    public async Task<IResult> RecordFilingAsync([FromBody] LevyReturnRecordFilingRequest request, [FromServices] ICommandHandler<RecordLevyReturnFiling, LevyReturnRecordFilingResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RecordLevyReturnFiling(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("request-payment")]
    [Authorize(Policy = LevyReturnPermissions.RequestPayment)]
    public async Task<IResult> RequestPaymentAsync([FromBody] LevyReturnRequestPaymentRequest request, [FromServices] ICommandHandler<RequestLevyReturnPayment, LevyReturnRequestPaymentResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RequestLevyReturnPayment(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
