using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Billing.Api;

/// <summary>Permission names of the refund operations (<c>x-permission</c>), granted to roles in <c>Platform:Permissions</c>.</summary>
internal static class RefundPermissions
{
    public const string Propose = "bil.Refund.propose";
    public const string Get = "bil.Refund.get";
    public const string List = "bil.Refund.list";
    public const string Decide = "bil.Refund.decide";
    public const string Resubmit = "bil.Refund.resubmit";
}

/// <summary>
/// REST facade of refunds (contracts/openapi/bil.yaml, SL3-BIL-REFUND). The IBAN never travels here: the payee is a payee-account
/// id and responses carry the masked IBAN (PITFALLS 18-20). Authority and segregation of duties are decided by the handlers from
/// BIL's own data, never from anything the client sends (PITFALLS 4).
/// </summary>
[ApiController]
[Route("api/bil/v1/refunds")]
internal sealed class RefundController : ControllerBase
{
    /// <summary>bil.Refund.propose → 200 with the refund (PENDING_APPROVAL, or PAID when approved by rule).</summary>
    [HttpPost("propose")]
    [Authorize(Policy = RefundPermissions.Propose)]
    public async Task<IResult> ProposeAsync(
        [FromBody] RefundProposeRequest request, [FromServices] ICommandHandler<ProposeRefund, RefundProposeResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ProposeRefund(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.Refund.decide.</summary>
    [HttpPost("decide")]
    [Authorize(Policy = RefundPermissions.Decide)]
    public async Task<IResult> DecideAsync(
        [FromBody] RefundDecideRequest request, [FromServices] ICommandHandler<DecideRefund, RefundDecideResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DecideRefund(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.Refund.resubmit.</summary>
    [HttpPost("resubmit")]
    [Authorize(Policy = RefundPermissions.Resubmit)]
    public async Task<IResult> ResubmitAsync(
        [FromBody] RefundResubmitRequest request, [FromServices] ICommandHandler<ResubmitRefund, RefundResubmitResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ResubmitRefund(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.Refund.get.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = RefundPermissions.Get)]
    public async Task<IResult> GetAsync(string id, [FromServices] RefundReader reader, CancellationToken cancellationToken)
    {
        var found = Guid.TryParse(id, out var guid)
            ? await reader.GetAsync(LegalEntity(), new RefundId(guid), cancellationToken).ConfigureAwait(false)
            : null;
        return found is null ? HttpResults.Problem(BillingErrors.NotFound("refund"), HttpContext) : Results.Ok(new RefundGetResponse { Refund = found });
    }

    /// <summary>bil.Refund.list by account, policy or state.</summary>
    [HttpGet]
    [Authorize(Policy = RefundPermissions.List)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] Guid? billingAccountId, [FromQuery] Guid? policyId, [FromQuery] RefundState? state,
        [FromServices] RefundReader reader, CancellationToken cancellationToken) =>
        Results.Ok(await reader.ListAsync(
            LegalEntity(),
            billingAccountId is { } a ? new BillingAccountId(a) : null,
            policyId is { } p ? new PolicyId(p) : null,
            state,
            cursor,
            limit,
            cancellationToken).ConfigureAwait(false));

    private LegalEntityId LegalEntity()
    {
        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        return services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
    }
}
