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

/// <summary>Permission names (the operations' <c>x-permission</c>), granted to roles in <c>Platform:Permissions</c>.</summary>
internal static class BillingPermissions
{
    public const string AccountGet = "bil.BillingAccount.get";
    public const string InvoiceGet = "bil.Invoice.get";
    public const string InvoiceList = "bil.Invoice.list";
    public const string PaymentTake = "bil.Payment.take";
    public const string ReceiptGet = "bil.Receipt.get";
    public const string AllocationAllocate = "bil.Allocation.allocate";
}

/// <summary>
/// REST facade of the slice's BIL operations (contracts/openapi/bil.yaml): thin, commands through the pipeline. No
/// personal data travels in a path or query (D-SLC-05): only ids.
/// </summary>
[ApiController]
[Route("api/bil/v1")]
internal sealed class BillingController : ControllerBase
{
    /// <summary>bil.BillingAccount.get.</summary>
    [HttpGet("billing-accounts/{id}")]
    [Authorize(Policy = BillingPermissions.AccountGet)]
    public async Task<IResult> GetAccountAsync(string id, [FromServices] BillingReader reader, CancellationToken cancellationToken)
    {
        var found = Guid.TryParse(id, out var guid)
            ? await reader.AccountAsync(LegalEntity(), new BillingAccountId(guid), cancellationToken).ConfigureAwait(false)
            : null;
        return found is null ? HttpResults.Problem(BillingErrors.NotFound("billing account"), HttpContext) : Results.Ok(found);
    }

    /// <summary>bil.Invoice.get.</summary>
    [HttpGet("invoices/{id}")]
    [Authorize(Policy = BillingPermissions.InvoiceGet)]
    public async Task<IResult> GetInvoiceAsync(string id, [FromServices] BillingReader reader, CancellationToken cancellationToken)
    {
        var found = Guid.TryParse(id, out var guid)
            ? await reader.InvoiceAsync(LegalEntity(), new InvoiceId(guid), cancellationToken).ConfigureAwait(false)
            : null;
        return found is null ? HttpResults.Problem(BillingErrors.NotFound("invoice"), HttpContext) : Results.Ok(found);
    }

    /// <summary>bil.Invoice.list by account, policy or term.</summary>
    [HttpGet("invoices")]
    [Authorize(Policy = BillingPermissions.InvoiceList)]
    public async Task<IResult> ListInvoicesAsync(
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] Guid? billingAccountId, [FromQuery] Guid? policyId, [FromQuery] Guid? policyTermId,
        [FromServices] BillingReader reader, CancellationToken cancellationToken) =>
        Results.Ok(await reader.InvoicesAsync(
            LegalEntity(),
            billingAccountId is { } a ? new BillingAccountId(a) : null,
            policyId is { } p ? new PolicyId(p) : null,
            policyTermId is { } t ? new PolicyTermId(t) : null,
            cursor,
            limit,
            cancellationToken).ConfigureAwait(false));

    /// <summary>bil.Payment.take → 201, Location = the receipt.</summary>
    [HttpPost("payments/take")]
    [Authorize(Policy = BillingPermissions.PaymentTake)]
    public async Task<IResult> TakePaymentAsync(
        [FromBody] PaymentTakeRequest request, [FromServices] ICommandHandler<TakePayment, PaymentTakeResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new TakePayment(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/bil/v1/receipts/{result.Value.Receipt.ReceiptId.Value:D}", result.Value)
            : HttpResults.Problem(result.Error!, HttpContext);
    }

    /// <summary>bil.Receipt.get.</summary>
    [HttpGet("receipts/{id}")]
    [Authorize(Policy = BillingPermissions.ReceiptGet)]
    public async Task<IResult> GetReceiptAsync(string id, [FromServices] BillingReader reader, CancellationToken cancellationToken)
    {
        var found = Guid.TryParse(id, out var guid)
            ? await reader.ReceiptAsync(LegalEntity(), new PaymentId(guid), cancellationToken).ConfigureAwait(false)
            : null;
        return found is null ? HttpResults.Problem(BillingErrors.NotFound("receipt"), HttpContext) : Results.Ok(found);
    }

    /// <summary>bil.Allocation.allocate.</summary>
    [HttpPost("allocations/allocate")]
    [Authorize(Policy = BillingPermissions.AllocationAllocate)]
    public async Task<IResult> AllocateAsync(
        [FromBody] AllocationAllocateRequest request, [FromServices] ICommandHandler<AllocateReceipt, AllocationAllocateResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AllocateReceipt(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    private LegalEntityId LegalEntity()
    {
        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        return services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
    }
}

/// <summary>Problem Details from a domain error.</summary>
internal static class HttpResults
{
    public static IResult Problem(DomainError error, HttpContext http) =>
        http.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(error, http);
}
