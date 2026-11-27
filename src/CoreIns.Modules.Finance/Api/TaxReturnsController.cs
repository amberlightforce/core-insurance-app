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

/// <summary>Permissions of the tax return operations (the operations' <c>x-permission</c>).</summary>
internal static class TaxReturnPermissions
{
    public const string List = "fin.TaxReturn.list";
    public const string Get = "fin.TaxReturn.get";
    public const string Recalculate = "fin.TaxReturn.recalculate";
    public const string AddAdjustment = "fin.TaxReturn.addAdjustment";
    public const string GenerateFile = "fin.TaxReturn.generateFile";
    public const string Transition = "fin.TaxReturn.transition";
    public const string RecordFiling = "fin.TaxReturn.recordFiling";
    public const string RequestPayment = "fin.TaxReturn.requestPayment";
}

/// <summary>REST facade of <c>fin.TaxReturn.*</c> (BO-FIN-09, REQ-FIN-005, -178..-188): IPT returns from the ledger, review, file, filing and the BIL payment.</summary>
[ApiController]
[Route("api/fin/v1/tax-returns")]
internal sealed class TaxReturnsController : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = TaxReturnPermissions.List)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? period, [FromQuery] string? status, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] TaxReturnService service, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > FinanceHttp.MaxLimit)
        {
            return this.Problem(FinanceHttp.Invalid($"limit must be within 1..{FinanceHttp.MaxLimit}."));
        }

        return (await service.ListAsync(period, status, cursor, limit ?? FinanceHttp.DefaultLimit, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }

    [HttpGet("{id}")]
    [Authorize(Policy = TaxReturnPermissions.Get)]
    public async Task<IResult> GetAsync(string id, [FromServices] TaxReturnService service, CancellationToken cancellationToken) =>
        Guid.TryParse(id, out var returnId)
            ? (await service.GetAsync(returnId, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext)
            : this.Problem(FinanceHttp.Invalid("id must be a UUID."));

    [HttpPost("recalculate")]
    [Authorize(Policy = TaxReturnPermissions.Recalculate)]
    public async Task<IResult> RecalculateAsync([FromBody] TaxReturnRecalculateRequest request, [FromServices] ICommandHandler<RecalculateTaxReturn, TaxReturnRecalculateResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RecalculateTaxReturn(request.Period, request.TaxCode), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("add-adjustment")]
    [Authorize(Policy = TaxReturnPermissions.AddAdjustment)]
    public async Task<IResult> AddAdjustmentAsync([FromBody] TaxReturnAddAdjustmentRequest request, [FromServices] ICommandHandler<AddTaxReturnAdjustment, TaxReturnAddAdjustmentResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AddTaxReturnAdjustment(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("generate-file")]
    [Authorize(Policy = TaxReturnPermissions.GenerateFile)]
    public async Task<IResult> GenerateFileAsync([FromBody] TaxReturnGenerateFileRequest request, [FromServices] ICommandHandler<GenerateTaxReturnFile, TaxReturnGenerateFileResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new GenerateTaxReturnFile(request.ReturnId), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("transition")]
    [Authorize(Policy = TaxReturnPermissions.Transition)]
    public async Task<IResult> TransitionAsync([FromBody] TaxReturnTransitionRequest request, [FromServices] ICommandHandler<TransitionTaxReturn, TaxReturnTransitionResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new TransitionTaxReturn(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("record-filing")]
    [Authorize(Policy = TaxReturnPermissions.RecordFiling)]
    public async Task<IResult> RecordFilingAsync([FromBody] TaxReturnRecordFilingRequest request, [FromServices] ICommandHandler<RecordTaxReturnFiling, TaxReturnRecordFilingResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RecordTaxReturnFiling(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("request-payment")]
    [Authorize(Policy = TaxReturnPermissions.RequestPayment)]
    public async Task<IResult> RequestPaymentAsync([FromBody] TaxReturnRequestPaymentRequest request, [FromServices] ICommandHandler<RequestTaxReturnPayment, TaxReturnRequestPaymentResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RequestTaxReturnPayment(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
