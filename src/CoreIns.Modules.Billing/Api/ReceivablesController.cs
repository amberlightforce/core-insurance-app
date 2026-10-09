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

[ApiController]
[Route("api/bil/v1/receivables")]
internal sealed class ReceivablesController : ControllerBase
{
    [HttpPost("register")]
    [Authorize(Policy = "bil.Receivable.register")]
    public async Task<IResult> RegisterAsync([FromBody] ReceivableRegisterRequest request, [FromServices] ICommandHandler<RegisterReceivable, ReceivableRegisterResponse> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new RegisterReceivable(request, null), ct).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpGet("{id}")]
    [Authorize(Policy = "bil.Receivable.get")]
    public async Task<IResult> GetAsync(Guid id, [FromServices] ReceivableReader reader, CancellationToken ct)
    {
        var row = await reader.GetAsync(Entity(), id, ct).ConfigureAwait(false);
        return row is null ? HttpResults.Problem(BillingErrors.NotFound("receivable"), HttpContext) : Results.Ok(new ReceivableGetResponse { Receivable = row });
    }

    [HttpGet]
    [Authorize(Policy = "bil.Receivable.list")]
    public async Task<IResult> ListAsync([FromQuery] Guid? billingAccountId, [FromQuery] Guid? counterpartyPartyId, [FromQuery] Guid? claimId, [FromQuery] Guid? recoveryId, [FromQuery] string? statementRef, [FromQuery] string? paymentReference, [FromQuery] string? sourceType, [FromQuery] string? status, [FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] ReceivableReader reader, CancellationToken ct)
    {
        if (cursor is not null && !Guid.TryParse(cursor, out _)) return HttpResults.Problem(DomainError.Of(ModuleCode.BIL, "VALIDATION", "Invalid cursor."), HttpContext);
        ReceivableSourceType? source = sourceType switch { null => null, "FS_CLEARING" => ReceivableSourceType.FsClearing, "CLM_CLAIM_PAYMENT" => ReceivableSourceType.ClmClaimPayment, _ => null };
        ReceivableStatus? state = status switch { null => null, "OPEN" => ReceivableStatus.Open, "PARTIALLY_PAID" => ReceivableStatus.PartiallyPaid, "PAID" => ReceivableStatus.Paid, "CANCELLED" => ReceivableStatus.Cancelled, _ => null };
        if ((sourceType is not null && source is null) || (status is not null && state is null)) return HttpResults.Problem(DomainError.Of(ModuleCode.BIL, "VALIDATION", "Invalid source or status filter."), HttpContext);
        return Results.Ok(await reader.ListAsync(Entity(), billingAccountId is { } a ? new BillingAccountId(a) : null, counterpartyPartyId is { } p ? new PartyId(p) : null, claimId is { } c ? new ClaimId(c) : null, recoveryId, statementRef, paymentReference, source, state, cursor, limit, ct).ConfigureAwait(false));
    }

    private LegalEntityId Entity()
    {
        var context = HttpContext.RequestServices.GetRequiredService<RequestContext>();
        return HttpContext.RequestServices.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
    }
}
