using CoreIns.Modules.Underwriting.Commands;
using CoreIns.Modules.Underwriting.Contracts;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Underwriting.Services;

/// <summary>
/// <c>uw.Rules.evaluate</c> in-process (D-ARC-16). It runs through the command pipeline with the caller's
/// <see cref="CommandOptions"/> (idempotency, dry-run); a failure is thrown as a <see cref="DomainException"/> with the UW-ERR code.
/// </summary>
internal sealed class UnderwritingRulesService(RequestContext context, ICommandHandler<EvaluateRules, RulesEvaluateResponse> evaluate) : IUnderwritingRulesService
{
    public async Task<RulesEvaluateResponse> EvaluateAsync(RulesEvaluateRequest request, CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            var result = await evaluate.HandleAsync(new EvaluateRules(request), cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? result.Value : throw new DomainException(result.Error);
        }
    }
}

/// <summary><c>uw.Issue.blockingStatus</c> (REQ-UW-002): the gate POL asks before quote, bind and issue.</summary>
internal sealed class UnderwritingIssueService(UnderwritingStore store, RequestContext context, ILegalEntityDirectory legalEntities) : IUnderwritingIssueService
{
    public async Task<IssueBlockingStatusResponse> BlockingStatusAsync(JobId jobRef, BlockingPoint blockingPoint, CancellationToken cancellationToken = default)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var point = blockingPoint switch
        {
            BlockingPoint.PreQuote => "PRE_QUOTE",
            BlockingPoint.PreBind => "PRE_BIND",
            BlockingPoint.PreIssue => "PRE_ISSUE",
            _ => throw new DomainException(DomainError.Of(ModuleCode.UW, "VALIDATION", "Ask for PRE_QUOTE, PRE_BIND or PRE_ISSUE.")),
        };
        var issues = await store.BlockingAsync(legalEntity.Value, jobRef.Value, point, cancellationToken).ConfigureAwait(false);
        return new IssueBlockingStatusResponse
        {
            Blocked = issues.Count > 0,
            Issues = [.. issues.Select(i => new IssueBlockingStatusResponse.IssueItem
            {
                IssueId = new UwIssueId(i.IssueId),
                IssueType = i.IssueType,
                Severity = i.Severity,
                BlockingPoint = i.BlockingPoint switch
                {
                    "PRE_QUOTE" => BlockingPoint.PreQuote,
                    "PRE_BIND" => BlockingPoint.PreBind,
                    "PRE_ISSUE" => BlockingPoint.PreIssue,
                    _ => BlockingPoint.NonBlocking,
                },
                IssueKey = i.IssueKey,
                Lane = i.Lane,
                ApprovalStatus = i.Status,
            })],
        };
    }

    public Task<IssueListForChannelPage> ListForChannelAsync(string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        throw new DomainException(DomainError.Of(ModuleCode.UW, "NOT-AVAILABLE", "uw.Issue.listForChannel belongs to the referral workbench work package."));
}
