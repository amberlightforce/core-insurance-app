using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Services;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Modules.Billing.Api;

/// <summary>
/// Development-only manual stub bank (SL4-BIL-DISBOPS, D-SL4-13), outside the public contract like <c>/dev/clock</c>:
/// <c>POST /dev/bank/disbursements/{id}/{issue|clear|return|reject}</c>. With <c>Billing:StubBank:Mode=Manual</c> the stub bank
/// holds each released payment at Released; these endpoints give the bank acknowledgement, statement debit, return or reject.
/// Answers 404 in Production and whenever the stub is not in Manual mode.
/// </summary>
[ApiController]
[Route("dev/bank/disbursements")]
internal sealed class DevBankController(IHostEnvironment environment, IServiceProvider services) : ControllerBase
{
    private bool Enabled => !environment.IsProduction() && services.GetService(typeof(IBankChannel)) is StubBankChannel { Manual: true };

    /// <summary>The bank acknowledges a Released payment (pain.002 ACCP): Released to Issued.</summary>
    [HttpPost("{id:guid}/issue")]
    [Authorize(Policy = DisbursementPermissions.DevBank)]
    public Task<IResult> IssueAsync(Guid id, [FromServices] ICommandHandler<DevBankOperation, DevBankResult> handler, CancellationToken cancellationToken) =>
        RunAsync(handler, id, "issue", null, cancellationToken);

    /// <summary>The statement debit of an Issued payment: Issued to Cleared.</summary>
    [HttpPost("{id:guid}/clear")]
    [Authorize(Policy = DisbursementPermissions.DevBank)]
    public Task<IResult> ClearAsync(Guid id, [FromServices] ICommandHandler<DevBankOperation, DevBankResult> handler, CancellationToken cancellationToken) =>
        RunAsync(handler, id, "clear", null, cancellationToken);

    /// <summary>The beneficiary bank returns the payment (Issued or Cleared); the reason code defaults to AC04.</summary>
    [HttpPost("{id:guid}/return")]
    [Authorize(Policy = DisbursementPermissions.DevBank)]
    public Task<IResult> ReturnAsync(Guid id, [FromQuery] string? reason, [FromServices] ICommandHandler<DevBankOperation, DevBankResult> handler, CancellationToken cancellationToken) =>
        RunAsync(handler, id, "return", reason, cancellationToken);

    /// <summary>The bank rejects a Released payment (pain.002 RJCT); the reason code defaults to AC01.</summary>
    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = DisbursementPermissions.DevBank)]
    public Task<IResult> RejectAsync(Guid id, [FromQuery] string? reason, [FromServices] ICommandHandler<DevBankOperation, DevBankResult> handler, CancellationToken cancellationToken) =>
        RunAsync(handler, id, "reject", reason, cancellationToken);

    private async Task<IResult> RunAsync(
        ICommandHandler<DevBankOperation, DevBankResult> handler, Guid id, string action, string? reason, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return Results.NotFound();
        }

        return (await handler.HandleAsync(new DevBankOperation(new DisbursementId(id), action, reason), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }
}
