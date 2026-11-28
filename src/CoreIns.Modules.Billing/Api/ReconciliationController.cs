using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Services;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Billing.Api;

/// <summary>Permission names of the reconciliation operations (<c>x-permission</c>), granted to roles in <c>Platform:Permissions</c>.</summary>
internal static class ReconciliationPermissions
{
    public const string Bank = "bil.Reconciliation.bank";
    public const string Invariants = "bil.Reconciliation.invariants";
}

/// <summary>
/// REST facade of the billing reconciliations (BO-BIL-11): the bank statement import with its automatic matching, and the sub-ledger
/// invariants. Both are billing staff operations; the import records money, so it is not a read.
/// </summary>
[ApiController]
[Route("api/bil/v1/reconciliation")]
internal sealed class ReconciliationController : ControllerBase
{
    /// <summary>bil.Reconciliation.bank: import a camt.053 or CSV statement of an own bank account and reconcile it.</summary>
    [HttpPost("bank")]
    [Authorize(Policy = ReconciliationPermissions.Bank)]
    public async Task<IResult> BankAsync(
        [FromBody] ReconciliationBankRequest request, [FromServices] ICommandHandler<ImportBankStatement, ReconciliationBankResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ImportBankStatement(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.Reconciliation.invariants: INV-01..15 as of a date; read-only (a query by contract, so no idempotency key).</summary>
    [HttpPost("invariants")]
    [SkipIdempotency]
    [Authorize(Policy = ReconciliationPermissions.Invariants)]
    public async Task<IResult> InvariantsAsync(
        [FromBody] ReconciliationInvariantsRequest request, [FromServices] SubLedgerInvariants invariants, CancellationToken cancellationToken) =>
        CoreIns.SharedKernel.Results.Result.Success(await invariants.RunAsync(request.Date, request.Account, cancellationToken).ConfigureAwait(false))
            .ToHttpResult(HttpContext);
}
