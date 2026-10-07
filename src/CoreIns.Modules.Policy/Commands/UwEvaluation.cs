using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Underwriting.Contracts;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Policy.Commands;

/// <summary>
/// <c>uw.Rules.evaluate</c> at a checkpoint (REQ-POL-157, REQ-POL-003): the risk snapshot is the rating input (passed
/// in-process; it holds the driver's date of birth and is not stored by POL), the snapshot reference names the quote
/// version and draft version, and the command's key is derived from POL's own key so a replay replays UW too.
/// </summary>
internal static class UwEvaluation
{
    public static async Task<Result<(RulesEvaluateResponse Evaluation, List<UwIssue> Issues)>> EvaluateAsync(
        IUnderwritingRulesService underwriting,
        RequestContext context,
        JobRow job,
        QuoteVersionRow version,
        RatingView view,
        RulesEvaluateRequest.CheckpointValue checkpoint,
        TimeZoneInfo zone,
        CancellationToken cancellationToken)
    {
        RulesEvaluateResponse evaluation;
        try
        {
            evaluation = await underwriting.EvaluateAsync(
                new RulesEvaluateRequest
                {
                    JobRef = job.JobId,
                    Checkpoint = checkpoint,
                    SnapshotRef = $"pol:quote:{version.QuoteId.Value:D}:{version.DraftVersion}",
                    SnapshotHash = CanonicalJson.Hash(view.Input.GetRawText()),
                    ProductCode = job.ProductCode,
                    EffectiveDate = job.EffectiveAt.ToBusinessDate(zone),
                    RiskSnapshot = view.Input,
                },
                new CommandOptions(JobSupport.Derived(context.IdempotencyKey, "uw.Rules.evaluate:" + checkpoint)) { DryRun = context.DryRun },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Module != ModuleCode.POL)
        {
            return DomainError.Of(
                ModuleCode.POL, checkpoint == RulesEvaluateRequest.CheckpointValue.PreBind ? "GATE-FAILED" : "VALIDATION",
                $"Underwriting could not evaluate the job: {ex.Error.Code} {ex.Error.Detail}");
        }

        var issues = evaluation.Issues.Select(i => new UwIssue
        {
            IssueId = i.IssueId, IssueType = i.IssueType, Severity = i.Severity, BlockingPoint = i.BlockingPoint, IssueKey = i.IssueKey,
            Lane = i.Lane, ExplanationKeys = i.ExplanationKeys, ApprovalStatus = i.ApprovalStatus,
        }).ToList();
        return (evaluation, issues);
    }
}
