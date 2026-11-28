using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Billing.Api;

/// <summary>Permission names of the payment plan operations and the billing preview (<c>x-permission</c>).</summary>
internal static class PaymentPlanPermissions
{
    public const string List = "bil.PaymentPlan.list";
    public const string Select = "bil.PaymentPlan.select";
    public const string Change = "bil.PaymentPlan.change";
    public const string Preview = "bil.BillingPreview.compute";
}

/// <summary>REST facade of the payment plan catalogue, selection, change and the billing preview (BO-BIL-03, contracts/openapi/bil.yaml).</summary>
[ApiController]
[Route("api/bil/v1")]
internal sealed class PaymentPlanController : ControllerBase
{
    /// <summary>bil.PaymentPlan.list: plans offered for a product version and channel.</summary>
    [HttpGet("payment-plans")]
    [Authorize(Policy = PaymentPlanPermissions.List)]
    public IResult ListPlans(
        [FromQuery] string? product, [FromQuery] string? productVersion, [FromQuery] string? channel) =>
        string.IsNullOrWhiteSpace(product) || string.IsNullOrWhiteSpace(productVersion) || string.IsNullOrWhiteSpace(channel)
            ? HttpResults.Problem(DomainError.Of(ModuleCode.BIL, "VALIDATION", "product, productVersion and channel are required."), HttpContext)
            : Results.Ok(PaymentPlanReader.List("EUR"));

    /// <summary>bil.PaymentPlan.select.</summary>
    [HttpPost("payment-plans/select")]
    [Authorize(Policy = PaymentPlanPermissions.Select)]
    public async Task<IResult> SelectAsync(
        [FromBody] PaymentPlanSelectRequest request, [FromServices] ICommandHandler<SelectPaymentPlan, PaymentPlanSelectResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SelectPaymentPlan(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.PaymentPlan.change.</summary>
    [HttpPost("payment-plans/change")]
    [Authorize(Policy = PaymentPlanPermissions.Change)]
    public async Task<IResult> ChangeAsync(
        [FromBody] PaymentPlanChangeRequest request, [FromServices] ICommandHandler<ChangePaymentPlan, PaymentPlanChangeResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ChangePaymentPlan(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.BillingPreview.compute: the schedule of a hypothetical plan, nothing persisted.</summary>
    [HttpPost("billing-preview/compute")]
    [Authorize(Policy = PaymentPlanPermissions.Preview)]
    public async Task<IResult> PreviewAsync([FromBody] BillingPreviewComputeRequest request, [FromServices] PaymentPlanReader reader, CancellationToken cancellationToken)
    {
        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        var legalEntity = services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
        return (await reader.PreviewAsync(legalEntity, request, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }
}
