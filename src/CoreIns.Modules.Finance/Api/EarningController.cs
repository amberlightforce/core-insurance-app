using CoreIns.Modules.Finance.Commands;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Modules.Finance.Queries;
using CoreIns.Modules.Finance.Services;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Finance.Api;

/// <summary>Permissions of the earning operations (the operations' <c>x-permission</c>).</summary>
internal static class EarningPermissions
{
    public const string Run = "fin.Earning.run";
    public const string Status = "fin.Earning.status";
    public const string Simulate = "fin.Earning.simulate";
    public const string Result = "fin.Earning.result";
}

/// <summary>REST facade of <c>fin.Earning.*</c> (BO-FIN-06a, REQ-FIN-003): run the earning engine, read the latest run, simulate without writing.</summary>
[ApiController]
[Route("api/fin/v1/earning")]
internal sealed class EarningController : ControllerBase
{
    /// <summary>fin.Earning.run: posts the earned premium movement; idempotent per period (a rerun posts only the difference).</summary>
    [HttpPost("run")]
    [Authorize(Policy = EarningPermissions.Run)]
    public async Task<IResult> RunAsync([FromBody] EarningRunRequest request, [FromServices] ICommandHandler<RunEarning, EarningRunResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RunEarning(request.AsOf), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>fin.Earning.status: the latest completed run; <c>run</c> is absent before the first one.</summary>
    [HttpGet("status")]
    [Authorize(Policy = EarningPermissions.Status)]
    public async Task<IResult> StatusAsync([FromServices] EarningEngine engine, CancellationToken cancellationToken) =>
        (await engine.StatusAsync(cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>fin.Earning.simulate: what a run would earn and post; nothing is written.</summary>
    [HttpPost("simulate")]
    [Authorize(Policy = EarningPermissions.Simulate)]
    public async Task<IResult> SimulateAsync([FromBody] EarningRunRequest request, [FromServices] EarningEngine engine, CancellationToken cancellationToken) =>
        (await engine.RunAsync(request.AsOf, simulate: true, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>fin.Earning.result: the per-term detail of one run (the latest when no <c>runId</c>), paged; the totals over all pages equal the run totals.</summary>
    [HttpGet("result")]
    [Authorize(Policy = EarningPermissions.Result)]
    public async Task<IResult> ResultAsync([FromQuery] Guid? runId, [FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] EarningReader reader, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > FinanceHttp.MaxLimit)
        {
            return this.Problem(FinanceHttp.Invalid($"limit must be within 1..{FinanceHttp.MaxLimit}."));
        }

        var take = limit ?? FinanceHttp.DefaultLimit;
        if (FinanceHttp.LegalEntity(HttpContext) is not (var entity, _))
        {
            return this.Problem(DomainError.Of(ModuleCode.FIN, "NOT-FOUND", "There is no such earning run."));
        }

        try
        {
            var page = await reader.ResultAsync(entity.Value, runId, cursor, take, cancellationToken).ConfigureAwait(false);
            return page is null ? this.Problem(DomainError.Of(ModuleCode.FIN, "NOT-FOUND", "There is no such earning run.")) : Results.Ok(page);
        }
        catch (FormatException)
        {
            return this.Problem(FinanceHttp.Invalid("The cursor is not valid."));
        }
    }
}
