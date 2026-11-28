using System.Globalization;
using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Billing.Api;

/// <summary>Permission names of the receipt list and the allocation reversals (<c>x-permission</c>).</summary>
internal static class AllocationPermissions
{
    public const string ReceiptList = "bil.Receipt.list";
    public const string Unallocate = "bil.Allocation.unallocate";
    public const string Reallocate = "bil.Allocation.reallocate";
}

/// <summary>
/// REST facade of the receipt list and of the allocation reversals (BO-BIL-07, contracts/openapi/bil.yaml). Reversals are append-only
/// and limited by BIL.ALLOCATION_REVERSE authority over the amount (checked by the handler); no personal data travels in a path or query.
/// </summary>
[ApiController]
[Route("api/bil/v1")]
internal sealed class AllocationController : ControllerBase
{
    /// <summary>bil.Receipt.list: receipts of the legal entity, keyset-paged and filtered.</summary>
    [HttpGet("receipts")]
    [Authorize(Policy = AllocationPermissions.ReceiptList)]
    public async Task<IResult> ListReceiptsAsync(
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] Guid? billingAccountId, [FromQuery] string? state,
        [FromQuery] string? valueDateFrom, [FromQuery] string? valueDateTo, [FromQuery] string? bankReferencePrefix,
        [FromQuery] string? amountMin, [FromQuery] string? amountMax,
        [FromServices] ReceiptReader reader, CancellationToken cancellationToken)
    {
        PaymentState? parsedState = null;
        if (state is not null)
        {
            parsedState = Enum.GetValues<PaymentState>().Cast<PaymentState?>().FirstOrDefault(v => string.Equals(Codes.Of(v!.Value), state, StringComparison.Ordinal));
            if (parsedState is null)
            {
                return Invalid($"The state '{state}' is not a receipt state.");
            }
        }

        BusinessDate? from = null;
        BusinessDate? to = null;
        if (valueDateFrom is not null)
        {
            if (!BusinessDate.TryParse(valueDateFrom, out var parsed))
            {
                return Invalid("valueDateFrom must be a date (yyyy-MM-dd).");
            }

            from = parsed;
        }

        if (valueDateTo is not null)
        {
            if (!BusinessDate.TryParse(valueDateTo, out var parsed))
            {
                return Invalid("valueDateTo must be a date (yyyy-MM-dd).");
            }

            to = parsed;
        }

        if (!TryAmount(amountMin, out var min) || !TryAmount(amountMax, out var max))
        {
            return Invalid("amountMin and amountMax must be decimal numbers.");
        }

        if (bankReferencePrefix is { Length: > 64 })
        {
            return Invalid("bankReferencePrefix is at most 64 characters.");
        }

        var filter = new ReceiptListFilter(billingAccountId is { } a ? new BillingAccountId(a) : null, parsedState, from, to, bankReferencePrefix, min, max);
        return Results.Ok(await reader.ListAsync(LegalEntity(), filter, cursor, limit, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>bil.Allocation.unallocate.</summary>
    [HttpPost("allocations/unallocate")]
    [Authorize(Policy = AllocationPermissions.Unallocate)]
    public async Task<IResult> UnallocateAsync(
        [FromBody] AllocationUnallocateRequest request, [FromServices] ICommandHandler<UnallocateReceipt, AllocationUnallocateResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new UnallocateReceipt(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.Allocation.reallocate.</summary>
    [HttpPost("allocations/reallocate")]
    [Authorize(Policy = AllocationPermissions.Reallocate)]
    public async Task<IResult> ReallocateAsync(
        [FromBody] AllocationReallocateRequest request, [FromServices] ICommandHandler<ReallocateReceipt, AllocationReallocateResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReallocateReceipt(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    private static bool TryAmount(string? text, out decimal? value)
    {
        value = null;
        if (text is null)
        {
            return true;
        }

        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private IResult Invalid(string message) => HttpResults.Problem(DomainError.Of(ModuleCode.BIL, "VALIDATION", message), HttpContext);

    private LegalEntityId LegalEntity()
    {
        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        return services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
    }
}
