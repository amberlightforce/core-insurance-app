using Microsoft.Extensions.DependencyInjection;
using CoreIns.Modules.Underwriting.Commands;
using CoreIns.Modules.Underwriting.Contracts;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Underwriting.Api;

/// <summary>Permissions of the underwriting operations (the contracts' <c>x-permission</c>).</summary>
internal static class UnderwritingPermissions
{
    public const string Evaluate = "uw.Rules.evaluate";
    public const string BlockingStatus = "uw.Issue.blockingStatus";
}

/// <summary>REST facade of <c>uw.Rules.evaluate</c> and <c>uw.Issue.blockingStatus</c>.</summary>
[ApiController]
[Route("api/uw/v1")]
internal sealed class UnderwritingController : ControllerBase
{
    /// <summary>uw.Rules.evaluate: a command (records the evaluation and the issues), so the Idempotency-Key is required.</summary>
    [HttpPost("rules/evaluate")]
    [Authorize(Policy = UnderwritingPermissions.Evaluate)]
    public async Task<IResult> EvaluateAsync(
        [FromBody] RulesEvaluateRequest request, [FromServices] ICommandHandler<EvaluateRules, RulesEvaluateResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new EvaluateRules(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.Issue.blockingStatus: the gate query.</summary>
    [HttpGet("issues/blocking-status")]
    [Authorize(Policy = UnderwritingPermissions.BlockingStatus)]
    public async Task<IResult> BlockingStatusAsync(
        [FromQuery] Guid jobRef, [FromQuery] string blockingPoint, [FromServices] IUnderwritingIssueService service, CancellationToken cancellationToken)
    {
        BlockingPoint point;
        switch (blockingPoint)
        {
            case "PRE_QUOTE":
                point = BlockingPoint.PreQuote;
                break;
            case "PRE_BIND":
                point = BlockingPoint.PreBind;
                break;
            case "PRE_ISSUE":
                point = BlockingPoint.PreIssue;
                break;
            default:
                return Problem(DomainError.Of(ModuleCode.UW, "VALIDATION", "blockingPoint must be PRE_QUOTE, PRE_BIND or PRE_ISSUE."), HttpContext);
        }

        try
        {
            return Results.Ok(await service.BlockingStatusAsync(JobId.From(jobRef), point, cancellationToken).ConfigureAwait(false));
        }
        catch (DomainException ex)
        {
            return Problem(ex.Error, HttpContext);
        }
    }

    private static IResult Problem(DomainError error, HttpContext http) =>
        http.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(error, http);
}
