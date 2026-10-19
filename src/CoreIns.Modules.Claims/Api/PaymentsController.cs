using CoreIns.Modules.Claims.Commands.Payments;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Claims.Api;

/// <summary>Permission names of the payment corrections (x-permission).</summary>
internal static class ClaimsPaymentPermissions
{
    public const string Void = "clm.Payment.void";
    public const string Stop = "clm.Payment.stop";
    public const string Reissue = "clm.Payment.reissue";
}

/// <summary>REST facade of <c>clm.Payment.void/stop/reissue</c> (SL4-CLM-PAYOPS); the payment list stays on the financials controller.</summary>
[ApiController]
[Route("api/clm/v1/payments")]
internal sealed class PaymentsController : ControllerBase
{
    [HttpPost("void")]
    [Authorize(Policy = ClaimsPaymentPermissions.Void)]
    public async Task<IResult> VoidAsync(
        [FromBody] PaymentVoidRequest request, [FromServices] ICommandHandler<VoidPayment, PaymentVoidResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new VoidPayment(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("stop")]
    [Authorize(Policy = ClaimsPaymentPermissions.Stop)]
    public async Task<IResult> StopAsync(
        [FromBody] PaymentStopRequest request, [FromServices] ICommandHandler<StopPayment, PaymentStopResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new StopPayment(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("reissue")]
    [Authorize(Policy = ClaimsPaymentPermissions.Reissue)]
    public async Task<IResult> ReissueAsync(
        [FromBody] PaymentReissueRequest request, [FromServices] ICommandHandler<ReissuePayment, PaymentReissueResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReissuePayment(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
