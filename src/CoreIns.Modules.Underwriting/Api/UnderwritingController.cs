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
    public const string EvaluateBatch = "uw.Rules.evaluateBatch";
    public const string IssueGet = "uw.Issue.get";
    public const string IssueExplain = "uw.Issue.explain";
    public const string ListForChannel = "uw.Issue.listForChannel";
    public const string ApprovalHistory = "uw.Approval.history";
    public const string Decide = "uw.Issue.decide";
    public const string IssueReopen = "uw.Issue.reopen";
    public const string IssueAddManual = "uw.Issue.addManual";
    public const string IssueClose = "uw.Issue.close";
    public const string ReferralList = "uw.Referral.list";
    public const string ReferralGet = "uw.Referral.get";
    public const string ReferralRequest = "uw.Referral.request";
    public const string ReferralWithdraw = "uw.Referral.withdraw";
    public const string ReferralReferUp = "uw.Referral.referUp";
    public const string ReferralRequestInformation = "uw.Referral.requestInformation";
    public const string ReviewLock = "uw.Review.lock";
    public const string ReviewRelease = "uw.Review.release";
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
        [FromServices] UnderwritingIssueQueries queries, CancellationToken cancellationToken,
        [FromQuery] string? issueType = null, [FromQuery] string? blockingPoint = null)
    {
        BlockingPoint? point = null;
        if (blockingPoint is not null)
        {
            point = blockingPoint switch
            {
                "PRE_QUOTE" => BlockingPoint.PreQuote,
                "PRE_BIND" => BlockingPoint.PreBind,
                "PRE_ISSUE" => BlockingPoint.PreIssue,
                "NON_BLOCKING" => BlockingPoint.NonBlocking,
                _ => null,
            };
            if (point is null)
            {
                return Problem(DomainError.Of(ModuleCode.UW, "VALIDATION", "blockingPoint must be PRE_QUOTE, PRE_BIND, PRE_ISSUE or NON_BLOCKING."), HttpContext);
            }
        }


        IssueStatusCode? statusCode = null;
        if (status is not null)
        {
            if (!Enum.TryParse<IssueStatusCode>(status, ignoreCase: false, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(status, out _))
            {
                return Problem(DomainError.Of(ModuleCode.UW, "VALIDATION", "status must be Open, Approved, ApprovedWithConditions, Rejected, Invalidated or Closed."), HttpContext);
            }

            statusCode = parsed;
        }

        var result = await queries.ListAsync(jobRef, statusCode, cursor, limit, cancellationToken, issueType, point).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.Rules.evaluateBatch: many risks against one rule-set version, with no effect on jobs or issues (a command: Idempotency-Key required).</summary>
    [HttpPost("rules/evaluate-batch")]
    [Authorize(Policy = UnderwritingPermissions.EvaluateBatch)]
    public Task<IResult> EvaluateBatchAsync(
        [FromBody] RulesEvaluateBatchRequest request, [FromServices] ICommandHandler<EvaluateRulesBatch, RulesEvaluateBatchResponse> handler, CancellationToken cancellationToken) =>
        RunAsync(handler, new EvaluateRulesBatch(request), cancellationToken);

    /// <summary>uw.Issue.explain: why an issue was raised (rule, facts read, observed value, threshold).</summary>
    [HttpGet("issues/explain")]
    [Authorize(Policy = UnderwritingPermissions.IssueExplain)]
    public async Task<IResult> ExplainAsync([FromQuery] Guid issueId, [FromServices] IssueReadQueries queries, CancellationToken cancellationToken)
    {
        var result = await queries.ExplainAsync(issueId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.Issue.listForChannel: the issues of a job with customer-safe wording (no rule ids or internals).</summary>
    [HttpGet("issues/list-for-channel")]
    [Authorize(Policy = UnderwritingPermissions.ListForChannel)]
    public async Task<IResult> ListForChannelAsync(
        [FromQuery] Guid? jobRef, [FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] IssueReadQueries queries, CancellationToken cancellationToken)
    {
        if (jobRef is null || jobRef == Guid.Empty)
        {
            return Problem(DomainError.Of(ModuleCode.UW, "VALIDATION", "jobRef is required."), HttpContext);
        }

        var result = await queries.ListForChannelAsync(jobRef.Value, cursor, limit, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.Issue.get: one issue with why it was raised.</summary>
    [HttpGet("issues/{id:guid}")]
    [Authorize(Policy = UnderwritingPermissions.IssueGet)]
    public async Task<IResult> GetIssueAsync(Guid id, [FromServices] IssueReadQueries queries, CancellationToken cancellationToken)
    {
        var result = await queries.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.Approval.history: decisions and approval invalidations per job and/or issue key.</summary>
    [HttpGet("approval/history")]
    [Authorize(Policy = UnderwritingPermissions.ApprovalHistory)]
    public async Task<IResult> ApprovalHistoryAsync(
        [FromQuery] Guid? jobRef, [FromQuery] string? issueKey, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] IssueReadQueries queries, CancellationToken cancellationToken)
    {
        var result = await queries.HistoryAsync(jobRef, issueKey, cursor, limit, cancellationToken).ConfigureAwait(false);
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

    /// <summary>uw.Issue.reopen: put an Approved or Rejected issue back to Open (a command: Idempotency-Key required).</summary>
    [HttpPost("issues/reopen")]
    [Authorize(Policy = UnderwritingPermissions.IssueReopen)]
    public Task<IResult> ReopenIssueAsync(
        [FromBody] IssueReopenRequest request, [FromServices] ICommandHandler<ReopenIssue, IssueReopenResponse> handler, CancellationToken cancellationToken) =>
        RunAsync(handler, new ReopenIssue(request), cancellationToken);

    /// <summary>uw.Issue.addManual: raise an issue by hand on an evaluated job.</summary>
    [HttpPost("issues/add-manual")]
    [Authorize(Policy = UnderwritingPermissions.IssueAddManual)]
    public Task<IResult> AddManualIssueAsync(
        [FromBody] IssueAddManualRequest request, [FromServices] ICommandHandler<AddManualIssue, IssueAddManualResponse> handler, CancellationToken cancellationToken) =>
        RunAsync(handler, new AddManualIssue(request), cancellationToken);

    /// <summary>uw.Issue.close: close an issue that does not block, with a reason.</summary>
    [HttpPost("issues/close")]
    [Authorize(Policy = UnderwritingPermissions.IssueClose)]
    public Task<IResult> CloseIssueAsync(
        [FromBody] IssueCloseRequest request, [FromServices] ICommandHandler<CloseIssue, IssueCloseResponse> handler, CancellationToken cancellationToken) =>
        RunAsync(handler, new CloseIssue(request), cancellationToken);

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

    /// <summary>uw.Referral.request: put a job in front of the underwriters, or resume it after information was supplied (BO-UW-05).</summary>
    [HttpPost("referrals/request")]
    [Authorize(Policy = UnderwritingPermissions.ReferralRequest)]
    public Task<IResult> RequestReferralAsync(
        [FromBody] ReferralRequestRequest request, [FromServices] ICommandHandler<RequestReferral, ReferralRequestResponse> handler, CancellationToken cancellationToken) =>
        RunAsync(handler, new RequestReferral(request), cancellationToken);

    /// <summary>uw.Referral.withdraw: take the referral back.</summary>
    [HttpPost("referrals/withdraw")]
    [Authorize(Policy = UnderwritingPermissions.ReferralWithdraw)]
    public Task<IResult> WithdrawReferralAsync(
        [FromBody] ReferralWithdrawRequest request, [FromServices] ICommandHandler<WithdrawReferral, ReferralWithdrawResponse> handler, CancellationToken cancellationToken) =>
        RunAsync(handler, new WithdrawReferral(request), cancellationToken);

    /// <summary>uw.Referral.referUp: send the referral to a higher authority.</summary>
    [HttpPost("referrals/refer-up")]
    [Authorize(Policy = UnderwritingPermissions.ReferralReferUp)]
    public Task<IResult> ReferUpAsync(
        [FromBody] ReferralReferUpRequest request, [FromServices] ICommandHandler<ReferReferralUp, ReferralReferUpResponse> handler, CancellationToken cancellationToken) =>
        RunAsync(handler, new ReferReferralUp(request), cancellationToken);

    /// <summary>uw.Referral.requestInformation: pause the SLA and ask for more information.</summary>
    [HttpPost("referrals/request-information")]
    [Authorize(Policy = UnderwritingPermissions.ReferralRequestInformation)]
    public Task<IResult> RequestInformationAsync(
        [FromBody] ReferralRequestInformationRequest request, [FromServices] ICommandHandler<RequestReferralInformation, ReferralRequestInformationResponse> handler, CancellationToken cancellationToken) =>
        RunAsync(handler, new RequestReferralInformation(request), cancellationToken);

    /// <summary>uw.Review.lock: take or renew the soft review lock of a referred job.</summary>
    [HttpPost("review/lock")]
    [Authorize(Policy = UnderwritingPermissions.ReviewLock)]
    public Task<IResult> LockReviewAsync(
        [FromBody] ReviewLockRequest request, [FromServices] ICommandHandler<LockReview, ReviewLockResponse> handler, CancellationToken cancellationToken) =>
        RunAsync(handler, new LockReview(request), cancellationToken);

    /// <summary>uw.Review.release: release the lock, or break another underwriter's (a manager, with a reason).</summary>
    [HttpPost("review/release")]
    [Authorize(Policy = UnderwritingPermissions.ReviewRelease)]
    public Task<IResult> ReleaseReviewAsync(
        [FromBody] ReviewReleaseRequest request, [FromServices] ICommandHandler<ReleaseReview, ReviewReleaseResponse> handler, CancellationToken cancellationToken) =>
        RunAsync(handler, new ReleaseReview(request), cancellationToken);

    private async Task<IResult> RunAsync<TCommand, TResult>(ICommandHandler<TCommand, TResult> handler, TCommand command, CancellationToken cancellationToken)
        where TCommand : ICommand<TResult>
    {
        var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    private static IResult Problem(DomainError error, HttpContext http) =>
        http.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(error, http);
}
