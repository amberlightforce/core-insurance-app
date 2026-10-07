using CoreIns.Modules.Underwriting.Queries;
using System.Data;
using System.Text.Json;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Contracts.Events;
using CoreIns.Modules.Underwriting.Domain;
using CoreIns.Modules.Underwriting.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Dapper;
using FluentValidation;

namespace CoreIns.Modules.Underwriting.Commands;

/// <summary><c>uw.Rules.evaluate</c> as a command of the platform pipeline: it records an evaluation and reconciles the job's issues.</summary>
internal sealed record EvaluateRules(RulesEvaluateRequest Request) : ICommand<RulesEvaluateResponse>;

internal sealed class EvaluateRulesValidator : AbstractValidator<EvaluateRules>
{
    public EvaluateRulesValidator()
    {
        RuleFor(c => c.Request.SnapshotRef).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Request.ProductCode).NotEmpty().MaximumLength(64).WithErrorCode("PRODUCT_CODE");
    }
}

/// <summary>
/// Evaluates the product's active rule set (a decision table on the shared rule engine) against the risk snapshot and
/// returns accept, refer or decline with reasons (REQ-UW-001, -042, -055, -059..061 subset). Hits become UW issues keyed by
/// issue type and element; an issue whose rule stops hitting is closed; decline beats refer beats accept. Every
/// evaluation records the rule-set code, version and content hash. Deciding issues is the referral workbench's job.
/// </summary>
internal sealed class EvaluateRulesHandler(
    UnderwritingStore store,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    IEventPublisher events) : ICommandHandler<EvaluateRules, RulesEvaluateResponse>
{
    public async Task<Result<RulesEvaluateResponse>> HandleAsync(EvaluateRules command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        try
        {
            if (request.RiskSnapshot is not { ValueKind: JsonValueKind.Object } snapshot)
            {
                return DomainError.Of(ModuleCode.UW, "SNAPSHOT", "The evaluation needs the risk snapshot (riskSnapshot).");
            }

            var risk = UwRisk.Parse(snapshot);
            var today = DateOnly.FromDateTime(clock.Now.ToUtcDateTime());
            var effective = request.EffectiveDate?.Value ?? today;
            var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
            await store.EnsureSeededAsync(cancellationToken).ConfigureAwait(false);
            var ruleSet = await store.ResolveAsync(request.ProductCode!, effective, cancellationToken).ConfigureAwait(false)
                ?? throw new DomainException(DomainError.Of(ModuleCode.UW, "RULESET-UNRESOLVED", $"No rule set is active for {request.ProductCode} on {effective:yyyy-MM-dd}."));

            var result = ruleSet.Table.Evaluate(CompiledRuleSet.Facts(risk, effective), effective);
            if (!result.IsSuccess)
            {
                return DomainError.Of(ModuleCode.UW, "EVAL-UNAVAILABLE", result.Error!.ToString());
            }

            var hits = result.Matches.Select(m => new Hit(
                m.RuleId,
                Text(m, "ruleType"),
                Text(m, "issueType"),
                Text(m, "blockingPoint"),
                Text(m, "messageEn"),
                Text(m, "messageEl"),
                $"{Text(m, "issueType")}:{risk.VehicleElementId}")).ToList();
            var outcome = hits.Any(h => h.RuleType == "DECLINE") ? "DECLINE" : hits.Any(h => h.RuleType == "REFER") ? "REFER" : "ACCEPT";
            var lane = outcome switch { "DECLINE" => "EXPERT", "REFER" => "ASSISTED", _ => "STRAIGHT_THROUGH" };
            var evaluationId = Guid.CreateVersion7();
            var jobId = request.JobRef.Value;
            var actor = context.Actor.ToString();

            await store.InsertEvaluationAsync(
                evaluationId, legalEntity, jobId, request.Checkpoint switch
                {
                    RulesEvaluateRequest.CheckpointValue.PreQuote => "PRE_QUOTE",
                    RulesEvaluateRequest.CheckpointValue.PreBind => "PRE_BIND",
                    RulesEvaluateRequest.CheckpointValue.PreIssue => "PRE_ISSUE",
                    _ => "RENEWAL",
                }, ruleSet, request.SnapshotRef, request.SnapshotHash?.Value,
                outcome, lane, JsonSerializer.Serialize(new
                {
                    ruleSet = new { code = ruleSet.Dto.Code, version = ruleSet.Dto.Version, hash = ruleSet.Hash, dataStatus = ruleSet.Dto.DataStatus },
                    effectiveDate = effective.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    matchedRules = result.MatchedRuleIds,
                    inputs = result.Trace.Inputs.Select(i => new { i.Name, value = i.Value.ToString() }),
                    variables = result.Trace.Variables.Select(v => new { v.Name, value = v.Value.ToString() }),
                    stepsUsed = result.Trace.StepsUsed,
                }), actor, cancellationToken).ConfigureAwait(false);

            // Reconcile with the job's open issues by issue key (REQ-UW-059): new key → raise, same key → keep, key gone → close.
            var open = await store.OpenIssuesAsync(legalEntity, jobId, cancellationToken).ConfigureAwait(false);
            var items = new List<RulesEvaluateResponse.IssueItem>();
            foreach (var hit in hits)
            {
                var existing = open.FirstOrDefault(i => i.IssueKey == hit.IssueKey);
                var issueId = existing?.IssueId ?? Guid.CreateVersion7();
                var blocking = hit.BlockingPoint switch
                {
                    "PRE_QUOTE" => BlockingPoint.PreQuote,
                    "PRE_BIND" => BlockingPoint.PreBind,
                    "PRE_ISSUE" => BlockingPoint.PreIssue,
                    _ => BlockingPoint.NonBlocking,
                };
                if (existing is null)
                {
                    await store.InsertIssueAsync(
                        issueId, legalEntity, jobId, hit.IssueType, hit.IssueKey, hit.BlockingPoint, hit.RuleType, lane, hit.RuleId, hit.MessageEn, hit.MessageEl,
                        evaluationId, cancellationToken).ConfigureAwait(false);
                    events.Publish(new OutgoingEvent(
                        EventDescriptor.From(UWIssueRaisedV1.Descriptor), "Job", jobId.ToString("D"),
                        new UWIssueRaisedV1
                        {
                            JobId = request.JobRef,
                            IssueId = new UwIssueId(issueId),
                            IssueType = hit.IssueType,
                            IssueKeyHash = Sha256Hash.ComputeUtf8(hit.IssueKey),
                            BlockingPoint = blocking,
                            Severity = hit.RuleType,
                            Lane = lane,
                            Reopened = false,
                        },
                        BusinessKeys.Empty.With("jobId", jobId.ToString("D")).With("issueId", issueId.ToString("D"))));
                }

                items.Add(new RulesEvaluateResponse.IssueItem
                {
                    IssueId = new UwIssueId(issueId),
                    IssueType = hit.IssueType,
                    Severity = hit.RuleType,
                    BlockingPoint = blocking,
                    IssueKey = hit.IssueKey,
                    Lane = lane,
                    ExplanationKeys = [hit.RuleId],
                    ApprovalStatus = "Open",
                });
            }

            foreach (var gone in open.Where(i => hits.All(h => h.IssueKey != i.IssueKey)))
            {
                await store.CloseIssueAsync(gone.IssueId, evaluationId, "RULE_NO_LONGER_HITS", cancellationToken).ConfigureAwait(false);
                events.Publish(new OutgoingEvent(
                    EventDescriptor.From(UWIssueClosedV1.Descriptor), "Job", jobId.ToString("D"),
                    new UWIssueClosedV1 { JobId = request.JobRef, IssueId = new UwIssueId(gone.IssueId), Reason = "RULE_NO_LONGER_HITS" },
                    BusinessKeys.Empty.With("jobId", jobId.ToString("D")).With("issueId", gone.IssueId.ToString("D"))));
            }

            return new RulesEvaluateResponse
            {
                EvaluationId = evaluationId,
                Lane = lane,
                Issues = items,
                Outcome = outcome switch
                {
                    "DECLINE" => RulesEvaluateResponse.OutcomeValue.Decline,
                    "REFER" => RulesEvaluateResponse.OutcomeValue.Refer,
                    _ => RulesEvaluateResponse.OutcomeValue.Accept,
                },
                Reasons = [.. hits.Select(h => new RulesEvaluateResponse.ReasonItem
                {
                    RuleId = h.RuleId,
                    IssueType = h.IssueType,
                    Outcome = h.RuleType == "DECLINE" ? RulesEvaluateResponse.ReasonItem.OutcomeValue.Decline : RulesEvaluateResponse.ReasonItem.OutcomeValue.Refer,
                    MessageEn = h.MessageEn,
                    MessageEl = h.MessageEl,
                })],
                RuleSetCode = ruleSet.Dto.Code,
                RuleSetVersion = ruleSet.Dto.Version,
                RuleSetHash = Sha256Hash.Parse(ruleSet.Hash),
            };
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }

    private static string Text(CoreIns.Rules.DecisionTables.DecisionMatch match, string column) =>
        ((CoreIns.Rules.StringValue)match.Output(column)).Value;

    private sealed record Hit(string RuleId, string RuleType, string IssueType, string BlockingPoint, string MessageEn, string MessageEl, string IssueKey);
}

/// <summary>Audit facts of an evaluation: the job it ran for.</summary>
internal sealed class EvaluateRulesAuditor : ICommandAuditor<EvaluateRules, RulesEvaluateResponse>
{
    public CommandAuditFacts Describe(EvaluateRules command, Result<RulesEvaluateResponse>? result) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.UW, "Job", command.Request.JobRef),
        BusinessKeys = BusinessKeys.Empty.With("jobId", command.Request.JobRef.Value.ToString("D")),
    };
}
