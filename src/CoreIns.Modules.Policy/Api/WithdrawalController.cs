using CoreIns.Modules.Policy.Commands.Withdrawal;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Policy.Api;

/// <summary>REST facade of <c>pol.Withdrawal.submit</c> (contracts/openapi/pol.yaml): thin, the command runs through the pipeline.</summary>
[ApiController]
[Route("api/pol/v1")]
internal sealed class WithdrawalController : ControllerBase
{
    /// <summary>Permission of the operation (its <c>x-permission</c>), granted to roles in <c>Platform:Permissions</c>.</summary>
    public const string SubmitPermission = "pol.Withdrawal.submit";

    /// <summary>pol.Withdrawal.submit: with <c>?dryRun=true</c> the refund preview only, nothing is written.</summary>
    [HttpPost("withdrawals/submit")]
    [Authorize(Policy = SubmitPermission)]
    public async Task<IResult> SubmitAsync(
        [FromBody] WithdrawalSubmitRequest request, [FromServices] ICommandHandler<SubmitWithdrawal, WithdrawalSubmitResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SubmitWithdrawal(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
