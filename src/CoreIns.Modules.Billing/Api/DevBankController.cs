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

    /// <summary>Runs the direct-debit collection now (the worker does it on a timer); returns what was submitted and settled.</summary>
    [HttpPost("/dev/bank/direct-debits/run")]
    [Authorize(Policy = DisbursementPermissions.DevBank)]
    public async Task<IResult> RunDirectDebitsAsync([FromServices] ICommandHandler<RunDirectDebitCollection, DirectDebitRunResult> handler, CancellationToken cancellationToken)
    {
        if (environment.IsProduction())
        {
            return Results.NotFound();
        }

        var result = await handler.HandleAsync(new RunDirectDebitCollection(), cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? result.ToHttpResult(HttpContext) : Results.Ok(new { submitted = result.Value.Submitted, settled = result.Value.Settled, collectionId = result.Value.CollectionId });
    }

    /// <summary>The bank settles a submitted direct debit (the money arrives); the receipt is created and allocated to the invoice.</summary>
    [HttpPost("/dev/bank/direct-debits/{endToEndId}/settle")]
    [Authorize(Policy = DisbursementPermissions.DevBank)]
    public Task<IResult> SettleDirectDebitAsync(string endToEndId, [FromServices] ICommandHandler<ApplyDirectDebitStatus, DirectDebitStatusResult> handler, CancellationToken cancellationToken) =>
        StatusAsync(handler, endToEndId, DirectDebitOutcome.Settled, null, cancellationToken);

    /// <summary>The bank refuses a submitted direct debit before settlement; the reason code defaults to AC04.</summary>
    [HttpPost("/dev/bank/direct-debits/{endToEndId}/reject")]
    [Authorize(Policy = DisbursementPermissions.DevBank)]
    public Task<IResult> RejectDirectDebitAsync(string endToEndId, [FromQuery] string? reason, [FromServices] ICommandHandler<ApplyDirectDebitStatus, DirectDebitStatusResult> handler, CancellationToken cancellationToken) =>
        StatusAsync(handler, endToEndId, DirectDebitOutcome.Rejected, reason ?? "AC04", cancellationToken);

    /// <summary>The payer's bank returns a settled direct debit (R-transaction: return); the reason code defaults to MS03. The receipt is reversed and the invoice reopens.</summary>
    [HttpPost("/dev/bank/direct-debits/{endToEndId}/return")]
    [Authorize(Policy = DisbursementPermissions.DevBank)]
    public Task<IResult> ReturnDirectDebitAsync(string endToEndId, [FromQuery] string? reason, [FromServices] ICommandHandler<ApplyDirectDebitStatus, DirectDebitStatusResult> handler, CancellationToken cancellationToken) =>
        StatusAsync(handler, endToEndId, DirectDebitOutcome.Returned, reason ?? "MS03", cancellationToken);

    private async Task<IResult> StatusAsync(
        ICommandHandler<ApplyDirectDebitStatus, DirectDebitStatusResult> handler, string endToEndId, DirectDebitOutcome outcome, string? reason, CancellationToken cancellationToken)
    {
        if (environment.IsProduction())
        {
            return Results.NotFound();
        }

        var result = await handler.HandleAsync(new ApplyDirectDebitStatus(endToEndId, outcome, reason), cancellationToken).ConfigureAwait(false);
        return result.IsFailure
            ? result.ToHttpResult(HttpContext)
            : Results.Ok(new { itemId = result.Value.ItemId, state = result.Value.State, receiptId = result.Value.ReceiptId, returnEntryId = result.Value.ReturnEntryId });
    }

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
