using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Persistence;
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

/// <summary>Permission names of write-offs and money transfers (<c>x-permission</c>).</summary>
internal static class WriteOffPermissions
{
    public const string List = "bil.WriteOff.list";
    public const string Propose = "bil.WriteOff.propose";
    public const string Decide = "bil.WriteOff.decide";
    public const string Post = "bil.WriteOff.post";
    public const string TransferMoney = "bil.Transfer.money";
}

/// <summary>
/// REST facade of write-offs and money transfers (BO-BIL-13, contracts/openapi/bil.yaml). A write-off is proposed by one person, decided by
/// another who holds BIL.WRITE_OFF authority over the amount (checked by the handler), then posted; a residual within the MKT tolerance is
/// written off at once. No personal data travels in a path or query.
/// </summary>
[ApiController]
[Route("api/bil/v1")]
internal sealed class WriteOffController : ControllerBase
{
    /// <summary>bil.WriteOff.list.</summary>
    [HttpGet("write-offs")]
    [Authorize(Policy = WriteOffPermissions.List)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? state, [FromQuery] Guid? billingAccountId, [FromQuery] int? limit, [FromServices] WriteOffReader reader, CancellationToken cancellationToken)
    {
        string? parsed = null;
        if (state is not null)
        {
            parsed = new[] { WriteOffStates.Proposed, WriteOffStates.Approved, WriteOffStates.Rejected, WriteOffStates.Posted }.FirstOrDefault(s => string.Equals(s, state, StringComparison.Ordinal));
            if (parsed is null)
            {
                return Invalid($"The state '{state}' is not a write-off state.");
            }
        }

        if (limit is < 1 or > 200)
        {
            return Invalid("The limit must be between 1 and 200.");
        }

        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        var legalEntity = services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
        return Results.Ok(await reader.ListAsync(legalEntity, parsed, billingAccountId is { } a ? new BillingAccountId(a) : null, limit ?? 50, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>bil.WriteOff.propose.</summary>
    [HttpPost("write-offs/propose")]
    [Authorize(Policy = WriteOffPermissions.Propose)]
    public async Task<IResult> ProposeAsync(
        [FromBody] WriteOffProposeRequest request, [FromServices] ICommandHandler<ProposeWriteOff, WriteOffProposeResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ProposeWriteOff(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.WriteOff.decide.</summary>
    [HttpPost("write-offs/decide")]
    [Authorize(Policy = WriteOffPermissions.Decide)]
    public async Task<IResult> DecideAsync(
        [FromBody] WriteOffDecideRequest request, [FromServices] ICommandHandler<DecideWriteOff, WriteOffDecideResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DecideWriteOff(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.WriteOff.post.</summary>
    [HttpPost("write-offs/post")]
    [Authorize(Policy = WriteOffPermissions.Post)]
    public async Task<IResult> PostAsync(
        [FromBody] WriteOffPostRequest request, [FromServices] ICommandHandler<PostWriteOff, WriteOffPostResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new PostWriteOff(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.Transfer.money.</summary>
    [HttpPost("transfers/money")]
    [Authorize(Policy = WriteOffPermissions.TransferMoney)]
    public async Task<IResult> TransferMoneyAsync(
        [FromBody] TransferMoneyRequest request, [FromServices] ICommandHandler<TransferMoney, TransferMoneyResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new TransferMoney(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    private IResult Invalid(string message) => HttpResults.Problem(DomainError.Of(ModuleCode.BIL, "VALIDATION", message), HttpContext);
}
