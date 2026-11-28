using CoreIns.Modules.Migration.Commands;
using CoreIns.Modules.Migration.Services.Cutover;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Migration.Api;

/// <summary>REST facade of the rehearsal, cut-over and rollback plan (BO-MIG-12; contracts/openapi/mig.yaml mig.Cutover.*).</summary>
[ApiController]
[Route("api/mig/v1/cutover")]
internal sealed class CutoverController : ControllerBase
{
    [HttpPost("plan")]
    [Authorize(Policy = "mig.Cutover.plan")]
    public async Task<IResult> PlanAsync([FromBody] CutoverPlanBody body, [FromServices] ICommandHandler<PlanCutover, CutoverPlanView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new PlanCutover(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("start-step")]
    [Authorize(Policy = "mig.Cutover.startStep")]
    public async Task<IResult> StartStepAsync([FromBody] CutoverStepBody body, [FromServices] ICommandHandler<StartCutoverStep, CutoverPlanView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new StartCutoverStep(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("complete-step")]
    [Authorize(Policy = "mig.Cutover.completeStep")]
    public async Task<IResult> CompleteStepAsync([FromBody] CutoverStepBody body, [FromServices] ICommandHandler<CompleteCutoverStep, CutoverPlanView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CompleteCutoverStep(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("rollback")]
    [Authorize(Policy = "mig.Cutover.rollback")]
    public async Task<IResult> RollbackAsync([FromBody] CutoverRollbackBody body, [FromServices] ICommandHandler<RollbackCutover, CutoverPlanView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RollbackCutover(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
