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

/// <summary>Permission names of the settlement operations (BO-RI-16), granted in <c>permissions/ri.json</c>.</summary>
internal static class SettlementPermissions
{
    public const string Propose = "ri.Settlement.propose";
    public const string Approve = "ri.Settlement.approve";
    public const string Release = "ri.Settlement.release";
    public const string RecordManual = "ri.Settlement.recordManual";
    public const string List = "ri.Settlement.list";
}

/// <summary>REST facade of the manual settlement (BO-RI-16, contracts/openapi/ri.yaml).</summary>
[ApiController]
[Route("api/ri/v1/settlements")]
internal sealed class SettlementsController : ControllerBase
{
    /// <summary>ri.Settlement.list, newest first.</summary>
    [HttpGet]
    [Authorize(Policy = SettlementPermissions.List)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? reinsurerPartyId, [FromQuery] string? status, [FromQuery] int? limit, [FromServices] SettlementReader reader, CancellationToken cancellationToken)
    {
        if (reinsurerPartyId is not null && !Guid.TryParse(reinsurerPartyId, out _))
        {
            return RiHttp.Problem(RiErrors.Validation("reinsurerPartyId", "ID_INVALID", "The reinsurer id filter is not valid."), HttpContext);
        }

        return (await reader.ListAsync(reinsurerPartyId is null ? null : Guid.Parse(reinsurerPartyId), status, limit, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }

    /// <summary>ri.Settlement.propose.</summary>
    [HttpPost("propose")]
    [Authorize(Policy = SettlementPermissions.Propose)]
    public async Task<IResult> ProposeAsync(
        [FromBody] SettlementProposeRequest request, [FromServices] ICommandHandler<ProposeSettlement, SettlementProposeResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ProposeSettlement(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Settlement.approve.</summary>
    [HttpPost("approve")]
    [Authorize(Policy = SettlementPermissions.Approve)]
    public async Task<IResult> ApproveAsync(
        [FromBody] SettlementApproveRequest request, [FromServices] ICommandHandler<ApproveSettlement, SettlementApproveResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ApproveSettlement(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Settlement.release.</summary>
    [HttpPost("release")]
    [Authorize(Policy = SettlementPermissions.Release)]
    public async Task<IResult> ReleaseAsync(
        [FromBody] SettlementReleaseRequest request, [FromServices] ICommandHandler<ReleaseSettlement, SettlementReleaseResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReleaseSettlement(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Settlement.recordManual.</summary>
    [HttpPost("record-manual")]
    [Authorize(Policy = SettlementPermissions.RecordManual)]
    public async Task<IResult> RecordManualAsync(
        [FromBody] SettlementRecordManualRequest request, [FromServices] ICommandHandler<RecordManualSettlement, SettlementRecordManualResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RecordManualSettlement(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
