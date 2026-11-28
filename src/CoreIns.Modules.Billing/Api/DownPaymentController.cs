using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Modules.Billing.Api;

/// <summary>REST facade of the down-payment bind gate (BO-BIL-04, contracts/openapi/bil.yaml).</summary>
[ApiController]
[Route("api/bil/v1/down-payment")]
internal sealed class DownPaymentController : ControllerBase
{
    /// <summary>bil.DownPayment.status.</summary>
    [HttpGet("status")]
    [Authorize(Policy = "bil.DownPayment.status")]
    public async Task<IResult> StatusAsync(
        [FromQuery] Guid? quoteId, [FromQuery] Guid? jobId, [FromServices] DownPaymentService service, [FromServices] ILegalEntityDirectory legalEntities,
        [FromServices] RequestContext context, CancellationToken cancellationToken)
    {
        if (jobId is null || quoteId is not null)
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.BIL, "VALIDATION", "Give jobId."), HttpContext);
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
        return Results.Ok(await service.StatusAsync(legalEntity, jobId.Value, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>bil.DownPayment.initiate.</summary>
    [HttpPost("initiate")]
    [Authorize(Policy = "bil.DownPayment.initiate")]
    public async Task<IResult> InitiateAsync(
        [FromBody] DownPaymentInitiateRequest request, [FromServices] ICommandHandler<InitiateDownPayment, DownPaymentInitiateResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new InitiateDownPayment(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}

/// <summary>
/// Development-only stand-in of the bank statement that pays a down payment, outside the public contract like <c>/dev/bank</c>:
/// <c>POST /dev/bil/down-payments/{paymentReference}/pay?amount=</c> (default: the full amount). Answers 404 in Production.
/// </summary>
[ApiController]
[Route("dev/bil/down-payments")]
internal sealed class DevDownPaymentController(IHostEnvironment environment) : ControllerBase
{
    /// <summary>Records cash against the payment reference.</summary>
    [HttpPost("{reference}/pay")]
    [Authorize(Policy = DisbursementPermissions.DevBank)]
    public async Task<IResult> PayAsync(string reference, [FromQuery] decimal? amount, [FromServices] DownPaymentService service, CancellationToken cancellationToken)
    {
        if (environment.IsProduction())
        {
            return Results.NotFound();
        }

        return (await service.RecordCashAsync(reference, amount, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }
}
