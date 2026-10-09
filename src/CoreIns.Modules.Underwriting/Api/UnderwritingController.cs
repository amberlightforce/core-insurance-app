using Microsoft.Extensions.DependencyInjection;
using CoreIns.Modules.Underwriting.Commands;
using CoreIns.Modules.Underwriting.Contracts;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Services;
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
    public const string List = "uw.Issue.list";
    public const string Decide = "uw.Issue.decide";
    public const string ReferralList = "uw.Referral.list";
    public const string ReferralGet = "uw.Referral.get";
}

/// <summary>REST facade of <c>uw.Rules.evaluate</c>, <c>uw.Issue.blockingStatus</c>, <c>uw.Issue.list</c> and <c>uw.Issue.decide</c>.</summary>
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

    /// <summary>uw.Issue.list: the issues of a job, or the referral queue (Open issues of the legal entity).</summary>
    [HttpGet("issues")]
    [Authorize(Policy = UnderwritingPermissions.List)]
    public async Task<IResult> ListAsync(
        [FromQuery] Guid? jobRef, [FromQuery] string? status, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] UnderwritingIssueQueries queries, CancellationToken cancellationToken)
    {
        IssueStatusCode? statusCode = null;
        if (status is not null)
        {
            if (!Enum.TryParse<IssueStatusCode>(status, ignoreCase: false, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(status, out _))
            {
                return Problem(DomainError.Of(ModuleCode.UW, "VALIDATION", "status must be Open, Approved, ApprovedWithConditions, Rejected, Invalidated or Closed."), HttpContext);
            }

            statusCode = parsed;
        }

        var result = await queries.ListAsync(jobRef, statusCode, cursor, limit, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.Issue.decide: approve or reject Open issues (a command: Idempotency-Key required, dry-run supported).</summary>
    [HttpPost("issues/decide")]
    [Authorize(Policy = UnderwritingPermissions.Decide)]
    public async Task<IResult> DecideAsync(
        [FromBody] IssueDecideRequest request, [FromServices] ICommandHandler<DecideIssues, IssueDecideResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new DecideIssues(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.Referral.list: the referral workbench queue (one row per referred job) with the queue counts.</summary>
    [HttpGet("referrals")]
    [Authorize(Policy = UnderwritingPermissions.ReferralList)]
    public async Task<IResult> ListReferralsAsync(
        [FromQuery] string? queue, [FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] ReferralQueries queries, CancellationToken cancellationToken)
    {
        ReferralQueueCode? code = null;
        if (queue is not null)
        {
            code = queue switch
            {
                "OPEN" => ReferralQueueCode.Open,
                "APPROVED_TODAY" => ReferralQueueCode.ApprovedToday,
                "REJECTED" => ReferralQueueCode.Rejected,
                "DECIDED_BY_ME_TODAY" => ReferralQueueCode.DecidedByMeToday,
                "MINE" => ReferralQueueCode.Mine,
                _ => null,
            };
            if (code is null)
            {
                return Problem(DomainError.Of(ModuleCode.UW, "VALIDATION", "queue must be OPEN, APPROVED_TODAY, REJECTED, DECIDED_BY_ME_TODAY or MINE."), HttpContext);
            }
        }

        var result = await queries.ListAsync(code, cursor, limit, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.Referral.get: one referred job (by its POL job id) with the quote header, risk facts and every issue.</summary>
    [HttpGet("referrals/{id}")]
    [Authorize(Policy = UnderwritingPermissions.ReferralGet)]
    public async Task<IResult> GetReferralAsync(string id, [FromServices] ReferralQueries queries, CancellationToken cancellationToken)
    {
        var result = await queries.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    private static IResult Problem(DomainError error, HttpContext http) =>
        http.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(error, http);
}
