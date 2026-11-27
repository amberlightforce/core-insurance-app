using CoreIns.Modules.Reinsurance.Accounting;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Reinsurance.Api;

/// <summary>Permission names of the cash call operations (BO-RI-15), granted in <c>permissions/ri.json</c>.</summary>
internal static class CashCallPermissions
{
    public const string Raise = "ri.CashCall.raise";
    public const string Issue = "ri.CashCall.issue";
    public const string RecordReceipt = "ri.CashCall.recordReceipt";
    public const string Cancel = "ri.CashCall.cancel";
    public const string List = "ri.CashCall.list";
}

/// <summary>REST facade of the cash calls (BO-RI-15, contracts/openapi/ri.yaml).</summary>
[ApiController]
[Route("api/ri/v1/cash-calls")]
internal sealed class CashCallsController : ControllerBase
{
    /// <summary>ri.CashCall.list, newest first, with the outstanding amount and aging.</summary>
    [HttpGet]
    [Authorize(Policy = CashCallPermissions.List)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? contractId, [FromQuery] string? status, [FromQuery] bool? openOnly, [FromQuery] int? limit, [FromServices] CashCallReader reader,
        CancellationToken cancellationToken)
    {
        if (contractId is not null && !Guid.TryParse(contractId, out _))
        {
            return RiHttp.Problem(RiErrors.Validation("contractId", "ID_INVALID", "The contract id filter is not valid."), HttpContext);
        }

        return (await reader.ListAsync(contractId is null ? null : Guid.Parse(contractId), status, openOnly ?? false, limit, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }

    /// <summary>ri.CashCall.raise.</summary>
    [HttpPost("raise")]
    [Authorize(Policy = CashCallPermissions.Raise)]
    public async Task<IResult> RaiseAsync(
        [FromBody] CashCallRaiseRequest request, [FromServices] ICommandHandler<RaiseCashCall, CashCallRaiseResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RaiseCashCall(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.CashCall.issue.</summary>
    [HttpPost("issue")]
    [Authorize(Policy = CashCallPermissions.Issue)]
    public async Task<IResult> IssueAsync(
        [FromBody] CashCallIssueRequest request, [FromServices] ICommandHandler<IssueCashCall, CashCallIssueResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new IssueCashCall(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.CashCall.recordReceipt.</summary>
    [HttpPost("record-receipt")]
    [Authorize(Policy = CashCallPermissions.RecordReceipt)]
    public async Task<IResult> RecordReceiptAsync(
        [FromBody] CashCallRecordReceiptRequest request, [FromServices] ICommandHandler<RecordCashCallReceipt, CashCallRecordReceiptResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RecordCashCallReceipt(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.CashCall.cancel.</summary>
    [HttpPost("cancel")]
    [Authorize(Policy = CashCallPermissions.Cancel)]
    public async Task<IResult> CancelAsync(
        [FromBody] CashCallCancelRequest request, [FromServices] ICommandHandler<CancelCashCall, CashCallCancelResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CancelCashCall(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
