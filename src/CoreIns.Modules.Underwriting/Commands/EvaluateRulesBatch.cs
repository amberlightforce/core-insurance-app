using System.Text.Json;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Domain;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;

namespace CoreIns.Modules.Underwriting.Commands;

/// <summary><c>uw.Rules.evaluateBatch</c> (BO-UW-03): many risk snapshots against one rule-set version, with no effect on jobs or issues.</summary>
internal sealed record EvaluateRulesBatch(RulesEvaluateBatchRequest Request) : ICommand<RulesEvaluateBatchResponse>;

internal sealed class EvaluateRulesBatchValidator : AbstractValidator<EvaluateRulesBatch>
{
    public EvaluateRulesBatchValidator()
    {
        RuleFor(c => c.Request.ProductCode).NotEmpty().MaximumLength(64).WithErrorCode("PRODUCT_CODE");
        RuleFor(c => c.Request.Items).NotEmpty();
        RuleFor(c => c.Request).Must(r => string.IsNullOrEmpty(r.RuleSetCode) == string.IsNullOrEmpty(r.RuleSetVersion))
            .WithMessage("Pin a rule-set version with both ruleSetCode and ruleSetVersion, or neither.");
        RuleForEach(c => c.Request.Items).ChildRules(item => item.RuleFor(i => i.ItemRef).NotEmpty().MaximumLength(200));
    }
}

/// <summary>
/// Evaluates up to <see cref="MaxItems"/> risk snapshots against one rule-set version: the one the caller pinned (code and
/// version, Active or Approved) or the version in force on the effective date. Nothing is recorded (no evaluation row, no
/// issue, no event, no job read), so the same request returns the same results and the same rule-set hash, whatever else runs.
/// A snapshot that is not a valid risk gives an ERROR result for that item only. Size is bounded, so the call is synchronous
/// (no background run).
/// </summary>
internal sealed class EvaluateRulesBatchHandler(
    UnderwritingStore store,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock) : ICommandHandler<EvaluateRulesBatch, RulesEvaluateBatchResponse>
{
    /// <summary>The most items one request evaluates; above it the caller splits the batch (UW-ERR-LIMIT-EXCEEDED).</summary>
    internal const int MaxItems = 200;

    public async Task<Result<RulesEvaluateBatchResponse>> HandleAsync(EvaluateRulesBatch command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (request.Items.Count > MaxItems)
        {
            return DomainError.Of(ModuleCode.UW, "LIMIT-EXCEEDED", $"A batch has at most {MaxItems} items; got {request.Items.Count}.");
        }

        var caller = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var today = clock.Now.ToBusinessDate(legalEntities.TimeZoneOf(caller)).Value;
        var defaultDate = request.EffectiveDate?.Value ?? today;
        var checkpoint = request.Checkpoint == RulesEvaluateBatchRequest.CheckpointValue.PreQuote ? "PRE_QUOTE" : "PRE_BIND";
        var pinned = !string.IsNullOrEmpty(request.RuleSetCode);

        await store.EnsureSeededAsync(cancellationToken).ConfigureAwait(false);
        CompiledRuleSet? ruleSet;
        if (pinned)
        {
            ruleSet = await store.ResolveVersionAsync(request.RuleSetCode!, request.RuleSetVersion!, request.ProductCode, checkpoint, cancellationToken).ConfigureAwait(false);
            if (ruleSet is null)
            {
                return DomainError.Of(ModuleCode.UW, "RULESET-NOT-FOUND", $"There is no Active or Approved rule set {request.RuleSetCode} {request.RuleSetVersion} for {request.ProductCode} at {checkpoint}.");
            }
        }
        else
        {
            ruleSet = await store.ResolveAsync(request.ProductCode, checkpoint, defaultDate, cancellationToken).ConfigureAwait(false);
            if (ruleSet is null)
            {
                return DomainError.Of(ModuleCode.UW, "RULESET-UNRESOLVED", $"No rule set is active for {request.ProductCode} on {defaultDate:yyyy-MM-dd}.");
            }
        }

        var results = new List<RulesEvaluateBatchResponse.ResultItem>(request.Items.Count);
        foreach (var item in request.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(Evaluate(ruleSet, item, item.EffectiveDate?.Value ?? defaultDate));
        }

        return new RulesEvaluateBatchResponse
        {
            RuleSetCode = ruleSet.Dto.Code,
            RuleSetVersion = ruleSet.Dto.Version,
            RuleSetHash = Sha256Hash.Parse(ruleSet.Hash),
            DataStatus = ruleSet.Dto.DataStatus,
            Pinned = pinned,
            Results = results,
            Warnings = ruleSet.Dto.DataStatus == "ILLUSTRATIVE_TEST_DATA" ? ["UW-WARN-ILLUSTRATIVE-RULES"] : [],
        };
    }

    private static RulesEvaluateBatchResponse.ResultItem Evaluate(CompiledRuleSet ruleSet, RulesEvaluateBatchRequest.ItemItem item, DateOnly effective)
    {
        if (item.RiskSnapshot.ValueKind != JsonValueKind.Object)
        {
            return Failed(item.ItemRef, "The item needs a risk snapshot object (riskSnapshot).");
        }

        UwRisk risk;
        try
        {
            risk = UwRisk.Parse(item.RiskSnapshot);
        }
        catch (DomainException ex)
        {
            return Failed(item.ItemRef, ex.Error.Detail ?? ex.Error.Code.ToString());
        }

        var result = ruleSet.Table.Evaluate(CompiledRuleSet.Facts(risk, effective), effective);
        if (!result.IsSuccess)
        {
            return Failed(item.ItemRef, result.Error!.ToString());
        }

        var hits = result.Matches.Select(m => new RulesEvaluateBatchResponse.ResultItem.HitItem
        {
            RuleId = m.RuleId,
            RuleType = Text(m, "ruleType"),
            IssueType = Text(m, "issueType"),
            BlockingPoint = Text(m, "blockingPoint") switch
            {
                "PRE_QUOTE" => BlockingPoint.PreQuote,
                "PRE_BIND" => BlockingPoint.PreBind,
                "PRE_ISSUE" => BlockingPoint.PreIssue,
                _ => BlockingPoint.NonBlocking,
            },
            MessageEn = Text(m, "messageEn"),
            MessageEl = Text(m, "messageEl"),
        }).ToList();
        var declined = hits.Any(h => h.RuleType == "DECLINE");
        var referred = hits.Any(h => h.RuleType == "REFER");
        return new RulesEvaluateBatchResponse.ResultItem
        {
            ItemRef = item.ItemRef,
            Outcome = declined ? RulesEvaluateBatchResponse.ResultItem.OutcomeValue.Decline
                : referred ? RulesEvaluateBatchResponse.ResultItem.OutcomeValue.Refer
                : RulesEvaluateBatchResponse.ResultItem.OutcomeValue.Accept,
            Lane = declined ? "EXPERT" : referred ? "ASSISTED" : "STRAIGHT_THROUGH",
            Hits = hits,
        };
    }

    private static RulesEvaluateBatchResponse.ResultItem Failed(string itemRef, string error) => new()
    {
        ItemRef = itemRef,
        Outcome = RulesEvaluateBatchResponse.ResultItem.OutcomeValue.Error,
        Hits = [],
        Error = error,
    };

    private static string Text(CoreIns.Rules.DecisionTables.DecisionMatch match, string column) =>
        ((CoreIns.Rules.StringValue)match.Output(column)).Value;
}

/// <summary>Audit facts of a batch evaluation: the rule set it ran against (nothing else is touched).</summary>
internal sealed class EvaluateRulesBatchAuditor : ICommandAuditor<EvaluateRulesBatch, RulesEvaluateBatchResponse>
{
    public CommandAuditFacts Describe(EvaluateRulesBatch command, Result<RulesEvaluateBatchResponse>? result)
    {
        var version = result is { IsSuccess: true } done ? done.Value.RuleSetCode + "@" + done.Value.RuleSetVersion : command.Request.RuleSetCode is null ? "unknown" : command.Request.RuleSetCode + "@" + command.Request.RuleSetVersion;
        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.UW, "RuleSet", version),
            BusinessKeys = BusinessKeys.Empty.With("productCode", command.Request.ProductCode),
        };
    }
}
