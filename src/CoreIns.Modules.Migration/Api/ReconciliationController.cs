using CoreIns.Modules.Migration.Commands;
using CoreIns.Modules.Migration.Services.Reconciliation;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Migration.Api;

/// <summary>REST facade of the reconciliation engine (BO-MIG-10; contracts/openapi/mig.yaml mig.Recon.*).</summary>
[ApiController]
[Route("api/mig/v1/reconciliation")]
internal sealed class ReconciliationController : ControllerBase
{
    public const string LoadControlTotalsPermission = "mig.Recon.loadControlTotals";
    public const string RunPermission = "mig.Recon.run";
    public const string ResultsPermission = "mig.Recon.results";

    [HttpPost("control-totals/load")]
    [Authorize(Policy = LoadControlTotalsPermission)]
    public async Task<IResult> LoadAsync([FromBody] ReconLoadBody body, [FromServices] ICommandHandler<LoadControlTotals, ReconLoadView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new LoadControlTotals(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("run")]
    [Authorize(Policy = RunPermission)]
    public async Task<IResult> RunAsync([FromBody] ReconRunBody body, [FromServices] ICommandHandler<RunReconciliation, ReconReportView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RunReconciliation(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpGet("results")]
    [Authorize(Policy = ResultsPermission)]
    public async Task<IResult> ResultsAsync([FromQuery] Guid? runId, [FromQuery] string? scope, [FromServices] ReconciliationService service, CancellationToken cancellationToken) =>
        (await service.ResultsAsync(runId, scope, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
