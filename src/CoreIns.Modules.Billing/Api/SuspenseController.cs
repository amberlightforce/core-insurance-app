using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
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

/// <summary>Permission names of the suspense operations (<c>x-permission</c>), granted to roles in <c>Platform:Permissions</c>.</summary>
internal static class SuspensePermissions
{
    public const string List = "bil.Suspense.list";
    public const string Suggest = "bil.Suspense.suggest";
    public const string Transfer = "bil.Suspense.transfer";
    public const string RefundToSender = "bil.Suspense.refundToSender";
}

/// <summary>
/// REST facade of the suspense workbench (BO-BIL-08, contracts/openapi/bil.yaml): unapplied cash as first-class items. Money out
/// (<c>refund-to-sender</c>) only proposes a refund; a second person decides it with <c>bil.Refund.decide</c> (PITFALLS 4/5).
/// </summary>
[ApiController]
[Route("api/bil/v1/suspense")]
internal sealed class SuspenseController : ControllerBase
{
    /// <summary>bil.Suspense.list: OPEN items by default, optionally of one account or state.</summary>
    [HttpGet]
    [Authorize(Policy = SuspensePermissions.List)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] Guid? billingAccountId, [FromQuery] string? state,
        [FromServices] SuspenseReader reader, CancellationToken cancellationToken)
    {
        // The state is the contract's wire code (REFUND_PENDING), which model binding of the enum would not read.
        SuspenseState? parsed = null;
        if (state is not null)
        {
            parsed = Enum.GetValues<SuspenseState>().Cast<SuspenseState?>().FirstOrDefault(v => string.Equals(Codes.Of(v!.Value), state, StringComparison.Ordinal));
            if (parsed is null)
            {
                return HttpResults.Problem(DomainError.Of(ModuleCode.BIL, "VALIDATION", $"The state '{state}' is not a suspense state."), HttpContext);
            }
        }

        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        var legalEntity = services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
        return Results.Ok(await reader.ListAsync(legalEntity, billingAccountId, parsed, cursor, limit, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>bil.Suspense.suggest.</summary>
    [HttpPost("suggest")]
    [Authorize(Policy = SuspensePermissions.Suggest)]
    public async Task<IResult> SuggestAsync(
        [FromBody] SuspenseSuggestRequest request, [FromServices] ICommandHandler<SuggestSuspense, SuspenseSuggestResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SuggestSuspense(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.Suspense.transfer.</summary>
    [HttpPost("transfer")]
    [Authorize(Policy = SuspensePermissions.Transfer)]
    public async Task<IResult> TransferAsync(
        [FromBody] SuspenseTransferRequest request, [FromServices] ICommandHandler<TransferSuspense, SuspenseTransferResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new TransferSuspense(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.Suspense.refundToSender.</summary>
    [HttpPost("refund-to-sender")]
    [Authorize(Policy = SuspensePermissions.RefundToSender)]
    public async Task<IResult> RefundToSenderAsync(
        [FromBody] SuspenseRefundToSenderRequest request, [FromServices] ICommandHandler<RefundSuspenseToSender, SuspenseRefundToSenderResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RefundSuspenseToSender(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
