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

/// <summary>Permission names of the premium accounting operations (BO-RI-10), granted in <c>permissions/ri.json</c>.</summary>
internal static class AccountingPermissions
{
    public const string DepositScheduleGet = "ri.DepositSchedule.get";
    public const string DepositScheduleReschedule = "ri.DepositSchedule.reschedule";
    public const string AdjustmentCalculate = "ri.Adjustment.calculate";
    public const string AdjustmentApprove = "ri.Adjustment.approve";
    public const string CommissionAdjust = "ri.Commission.adjust";
    public const string CommissionApprove = "ri.Commission.approve";
    public const string CommissionHistory = "ri.Commission.history";
}

/// <summary>
/// REST facade of the premium accounting of a treaty (BO-RI-10, contracts/openapi/ri.yaml): deposit schedules, adjustment premium and
/// sliding-scale commission. Thin; commands go through the pipeline, queries through the readers.
/// </summary>
[ApiController]
[Route("api/ri/v1")]
internal sealed class AccountingController : ControllerBase
{
    /// <summary>ri.DepositSchedule.get.</summary>
    [HttpGet("deposit-schedules/{id}")]
    [Authorize(Policy = AccountingPermissions.DepositScheduleGet)]
    public async Task<IResult> GetScheduleAsync(string id, [FromServices] DepositScheduleReader reader, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var scheduleId))
        {
            return RiHttp.Problem(RiErrors.Validation("id", "ID_INVALID", "The schedule id is not valid."), HttpContext);
        }

        return (await reader.GetAsync(scheduleId, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }

    /// <summary>ri.DepositSchedule.reschedule.</summary>
    [HttpPost("deposit-schedules/reschedule")]
    [Authorize(Policy = AccountingPermissions.DepositScheduleReschedule)]
    public async Task<IResult> RescheduleAsync(
        [FromBody] DepositScheduleRescheduleRequest request, [FromServices] ICommandHandler<RescheduleDeposit, DepositScheduleRescheduleResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RescheduleDeposit(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Adjustment.calculate.</summary>
    [HttpPost("adjustments/calculate")]
    [Authorize(Policy = AccountingPermissions.AdjustmentCalculate)]
    public async Task<IResult> CalculateAdjustmentAsync(
        [FromBody] AdjustmentCalculateRequest request, [FromServices] ICommandHandler<CalculateAdjustment, AdjustmentCalculateResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CalculateAdjustment(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Adjustment.approve (APPROVE or REJECT, once).</summary>
    [HttpPost("adjustments/approve")]
    [Authorize(Policy = AccountingPermissions.AdjustmentApprove)]
    public async Task<IResult> ApproveAdjustmentAsync(
        [FromBody] AdjustmentApproveRequest request, [FromServices] ICommandHandler<ApproveAdjustment, AdjustmentApproveResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ApproveAdjustment(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Commission.adjust.</summary>
    [HttpPost("commission/adjust")]
    [Authorize(Policy = AccountingPermissions.CommissionAdjust)]
    public async Task<IResult> AdjustCommissionAsync(
        [FromBody] CommissionAdjustRequest request, [FromServices] ICommandHandler<AdjustCommission, CommissionAdjustResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AdjustCommission(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Commission.approve (APPROVE or REJECT, once).</summary>
    [HttpPost("commission/approve")]
    [Authorize(Policy = AccountingPermissions.CommissionApprove)]
    public async Task<IResult> ApproveCommissionAsync(
        [FromBody] CommissionApproveRequest request, [FromServices] ICommandHandler<ApproveCommission, CommissionApproveResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ApproveCommission(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Commission.history, newest first.</summary>
    [HttpGet("commission/history")]
    [Authorize(Policy = AccountingPermissions.CommissionHistory)]
    public async Task<IResult> CommissionHistoryAsync(
        [FromQuery] Guid? contractId, [FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] CommissionHistoryReader reader, CancellationToken cancellationToken) =>
        (await reader.ListAsync(contractId, cursor, limit, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
