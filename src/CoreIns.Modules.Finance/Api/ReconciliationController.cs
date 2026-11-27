using CoreIns.Modules.Finance.Commands;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Modules.Finance.Services;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Finance.Api;

/// <summary>Permissions of the reconciliation operations (the operations' <c>x-permission</c>).</summary>
internal static class ReconciliationPermissions
{
    public const string Run = "fin.Reconciliation.run";
    public const string Detail = "fin.Reconciliation.detail";
    public const string Breaks = "fin.Reconciliation.breaks";
    public const string Match = "fin.Reconciliation.match";
    public const string Exchange = "fin.Reconciliation.exchange";
}

/// <summary>REST facade of <c>fin.Reconciliation.*</c> (BO-FIN-08, REQ-FIN-244, -250..-259): run, read, match and receive control totals.</summary>
[ApiController]
[Route("api/fin/v1/reconciliation")]
internal sealed class ReconciliationController : ControllerBase
{
    [HttpPost("run")]
    [Authorize(Policy = ReconciliationPermissions.Run)]
    public async Task<IResult> RunAsync([FromBody] ReconciliationRunRequest request, [FromServices] ICommandHandler<RunReconciliation, ReconciliationRunResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RunReconciliation(request.Period, request.Definition), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("match")]
    [Authorize(Policy = ReconciliationPermissions.Match)]
    public async Task<IResult> MatchAsync([FromBody] ReconciliationMatchRequest request, [FromServices] ICommandHandler<MatchReconciliationBreak, ReconciliationMatchResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new MatchReconciliationBreak(request.BreakId, request.Reason), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("exchange")]
    [Authorize(Policy = ReconciliationPermissions.Exchange)]
    public async Task<IResult> ExchangeAsync([FromBody] ReconciliationExchangeRequest request, [FromServices] ICommandHandler<ExchangeReconciliationTotals, ReconciliationExchangeResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ExchangeReconciliationTotals(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpGet("detail")]
    [Authorize(Policy = ReconciliationPermissions.Detail)]
    public async Task<IResult> DetailAsync([FromQuery] string? period, [FromQuery] string? definition, [FromServices] ReconciliationService service, CancellationToken cancellationToken) =>
        period is null
            ? this.Problem(FinanceHttp.Invalid("period must be yyyy-MM."))
            : (await service.DetailAsync(period, definition, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpGet("breaks")]
    [Authorize(Policy = ReconciliationPermissions.Breaks)]
    public async Task<IResult> BreaksAsync(
        [FromQuery] string? period, [FromQuery] string? definition, [FromQuery] string? state, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] ReconciliationService service, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > FinanceHttp.MaxLimit)
        {
            return this.Problem(FinanceHttp.Invalid($"limit must be within 1..{FinanceHttp.MaxLimit}."));
        }

        return (await service.BreaksAsync(period, definition, state, cursor, limit ?? FinanceHttp.DefaultLimit, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }
}
