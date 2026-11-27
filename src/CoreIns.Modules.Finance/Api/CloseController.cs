using CoreIns.Modules.Finance.Commands;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Finance.Api;

/// <summary>Permissions of the period close operations (the operations' <c>x-permission</c>).</summary>
internal static class ClosePermissions
{
    public const string Tasks = "fin.Close.tasks";
    public const string SignOff = "fin.Close.signOff";
    public const string SoftClose = "fin.Close.softClose";
    public const string Lock = "fin.Close.lock";
    public const string Reopen = "fin.Close.reopen";
    public const string AcceptBreak = "fin.Close.acceptBreak";
}

/// <summary>REST facade of <c>fin.Close.*</c> (REQ-FIN-007, -227..-241): the close checklist and the OPEN, SOFT_CLOSED, LOCKED state machine of an accounting period.</summary>
[ApiController]
[Route("api/fin/v1/close")]
internal sealed class CloseController : ControllerBase
{
    [HttpGet("tasks")]
    [Authorize(Policy = ClosePermissions.Tasks)]
    public async Task<IResult> TasksAsync([FromQuery] string? period, [FromServices] PeriodCloseService service, CancellationToken cancellationToken)
    {
        if (!PeriodClose.IsPeriodCode(period))
        {
            return this.Problem(FinanceHttp.Invalid("period must be yyyy-MM."));
        }

        return (await service.TasksAsync(period!, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }

    [HttpPost("sign-off")]
    [Authorize(Policy = ClosePermissions.SignOff)]
    public async Task<IResult> SignOffAsync([FromBody] CloseSignOffRequest request, [FromServices] ICommandHandler<SignOffCloseTask, ClosePeriodView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SignOffCloseTask(request.Period, TaskCode(request.Task), request.Reason), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("soft-close")]
    [Authorize(Policy = ClosePermissions.SoftClose)]
    public async Task<IResult> SoftCloseAsync([FromBody] CloseSoftCloseRequest request, [FromServices] ICommandHandler<SoftClosePeriod, ClosePeriodView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SoftClosePeriod(request.Period, request.Reason), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("lock")]
    [Authorize(Policy = ClosePermissions.Lock)]
    public async Task<IResult> LockAsync([FromBody] CloseLockRequest request, [FromServices] ICommandHandler<LockPeriod, ClosePeriodView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new LockPeriod(request.Period, request.Reason), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("reopen")]
    [Authorize(Policy = ClosePermissions.Reopen)]
    public async Task<IResult> ReopenAsync([FromBody] CloseReopenRequest request, [FromServices] ICommandHandler<ReopenPeriod, ClosePeriodView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReopenPeriod(request.Period, request.Reason), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("accept-break")]
    [Authorize(Policy = ClosePermissions.AcceptBreak)]
    public async Task<IResult> AcceptBreakAsync([FromBody] CloseAcceptBreakRequest request, [FromServices] ICommandHandler<AcceptCloseBreak, ClosePeriodView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AcceptCloseBreak(request.Period, request.Reason), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    private static string TaskCode(CloseTaskCode task) => task switch
    {
        CloseTaskCode.IntakeExceptions => PeriodClose.IntakeExceptions,
        CloseTaskCode.Reconciliations => PeriodClose.Reconciliations,
        CloseTaskCode.EarningRun => PeriodClose.EarningRun,
        _ => PeriodClose.ManualJournals,
    };
}
