using CoreIns.Modules.Channels.Commands;
using CoreIns.Modules.Channels.Contracts.Api;
using CoreIns.Modules.Channels.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Channels.Api;

/// <summary>Permission names of the withdrawal operations (<c>x-permission</c>).</summary>
internal static class ChannelsPermissions
{
    public const string WithdrawalDeclare = "chn.Withdrawal.declare";
    public const string WithdrawalConfirm = "chn.Withdrawal.confirm";
    public const string WithdrawalClose = "chn.Withdrawal.close";
    public const string WithdrawalGet = "chn.Withdrawal.get";
    public const string WithdrawalList = "chn.Withdrawal.list";
}

/// <summary>REST facade of <c>chn.Withdrawal.*</c> (contracts/openapi/chn.yaml).</summary>
[ApiController]
[Route("api/chn/v1/withdrawals")]
internal sealed class WithdrawalsController : ControllerBase
{
    /// <summary>chn.Withdrawal.declare.</summary>
    [HttpPost("declare")]
    [Authorize(Policy = ChannelsPermissions.WithdrawalDeclare)]
    public async Task<IResult> DeclareAsync(
        [FromBody] WithdrawalDeclareRequest request,
        [FromServices] ICommandHandler<DeclareWithdrawal, WithdrawalDeclareResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DeclareWithdrawal(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>chn.Withdrawal.confirm.</summary>
    [HttpPost("confirm")]
    [Authorize(Policy = ChannelsPermissions.WithdrawalConfirm)]
    public async Task<IResult> ConfirmAsync(
        [FromBody] WithdrawalConfirmRequest request,
        [FromServices] ICommandHandler<ConfirmWithdrawal, WithdrawalConfirmResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ConfirmWithdrawal(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>chn.Withdrawal.close.</summary>
    [HttpPost("{id}/close")]
    [Authorize(Policy = ChannelsPermissions.WithdrawalClose)]
    public async Task<IResult> CloseAsync(
        string id,
        [FromBody] WithdrawalCloseRequest request,
        [FromServices] ICommandHandler<CloseWithdrawal, WithdrawalCloseResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CloseWithdrawal(id, request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>chn.Withdrawal.get (by request number or id).</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = ChannelsPermissions.WithdrawalGet)]
    public async Task<IResult> GetAsync(string id, [FromServices] WithdrawalReader reader, CancellationToken cancellationToken)
    {
        // TODO(hardening): a portal customer may read only requests of their own policies.
        var found = await reader.GetAsync(LegalEntity(), id, cancellationToken).ConfigureAwait(false);
        return found is null
            ? Problem(DomainError.Of(ModuleCode.CHN, "NOT-FOUND", "The withdrawal request does not exist in your legal entity."))
            : Results.Ok(found);
    }

    /// <summary>chn.Withdrawal.list.</summary>
    [HttpGet]
    [Authorize(Policy = ChannelsPermissions.WithdrawalList)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? requestNumber, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] WithdrawalReader reader, CancellationToken cancellationToken) =>
        Results.Ok(await reader.ListAsync(LegalEntity(), requestNumber, cursor, limit, cancellationToken).ConfigureAwait(false));

    private LegalEntityId LegalEntity()
    {
        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        return services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity!.Value);
    }

    private IResult Problem(DomainError error) => HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(error, HttpContext);
}
