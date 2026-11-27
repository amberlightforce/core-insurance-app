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

/// <summary>Permission names of the technical account operations (BO-RI-11), granted in <c>permissions/ri.json</c>.</summary>
internal static class AccountItemPermissions
{
    public const string ItemList = "ri.Item.list";
    public const string ItemGenerate = "ri.Item.generate";
    public const string ItemReverse = "ri.Item.reverse";
    public const string BalanceReport = "ri.Balance.report";
}

/// <summary>REST facade of the technical account (BO-RI-11, contracts/openapi/ri.yaml): items, their generation and reversal, and the balance report.</summary>
[ApiController]
[Route("api/ri/v1")]
internal sealed class AccountItemsController : ControllerBase
{
    /// <summary>ri.Item.list, newest first.</summary>
    [HttpGet("items")]
    [Authorize(Policy = AccountItemPermissions.ItemList)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? contractId, [FromQuery] string? reinsurerPartyId, [FromQuery] string? kind, [FromQuery] string? period,
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] AccountItemReader reader, CancellationToken cancellationToken)
    {
        if (contractId is not null && !Guid.TryParse(contractId, out _) || reinsurerPartyId is not null && !Guid.TryParse(reinsurerPartyId, out _))
        {
            return RiHttp.Problem(RiErrors.Validation("id", "ID_INVALID", "An id filter is not valid."), HttpContext);
        }

        return (await reader.ListAsync(
            contractId is null ? null : Guid.Parse(contractId), reinsurerPartyId is null ? null : Guid.Parse(reinsurerPartyId), kind, period, cursor, limit, cancellationToken)
            .ConfigureAwait(false)).ToHttpResult(HttpContext);
    }

    /// <summary>ri.Item.generate.</summary>
    [HttpPost("items/generate")]
    [Authorize(Policy = AccountItemPermissions.ItemGenerate)]
    public async Task<IResult> GenerateAsync(
        [FromBody] ItemGenerateRequest request, [FromServices] ICommandHandler<GenerateItems, ItemGenerateResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new GenerateItems(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Item.reverse.</summary>
    [HttpPost("items/reverse")]
    [Authorize(Policy = AccountItemPermissions.ItemReverse)]
    public async Task<IResult> ReverseAsync(
        [FromBody] ItemReverseRequest request, [FromServices] ICommandHandler<ReverseItem, ItemReverseResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReverseItem(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Balance.report (a read; POST carries the filters).</summary>
    [HttpPost("balances/report")]
    [Authorize(Policy = AccountItemPermissions.BalanceReport)]
    public async Task<IResult> BalanceReportAsync([FromBody] BalanceReportRequest request, [FromServices] AccountItemReader reader, CancellationToken cancellationToken) =>
        (await reader.ReportAsync(request, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
