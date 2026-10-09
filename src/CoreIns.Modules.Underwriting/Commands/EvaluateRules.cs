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
/// <param name="Request">The request.</param>
/// <param name="ReconcileIssues">True only for POL's in-process call (snapshot of a real quote version): only it changes the job's issues (D-UW-01).</param>
internal sealed record EvaluateRules(RulesEvaluateRequest Request, bool ReconcileIssues = false) : ICommand<RulesEvaluateResponse>;

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
/// issue type and element (POL's in-process evaluation only); decline beats refer beats accept. Every evaluation records the
/// rule-set code, version and content hash and, from POL, who worked on the job (SOD-UW-02). Issues are decided by uw.Issue.decide.
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
            // The business date of a Greek legal entity is the Europe/Athens date, not the UTC date.
            var effective = request.EffectiveDate?.Value ?? clock.Now.ToBusinessDate(AthensOrUtc()).Value;
            var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
            await store.EnsureSeededAsync(cancellationToken).ConfigureAwait(false);
            var ruleSet = await store.ResolveAsync(request.ProductCode!, request.Checkpoint == RulesEvaluateRequest.CheckpointValue.PreQuote ? "PRE_QUOTE" : "PRE_BIND", effective, cancellationToken).ConfigureAwait(false)
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
                    stepsUsed = result.Trace.StepsUsed,
                }), actor, command.ReconcileIssues ? [.. request.JobParticipants ?? []] : [], command.ReconcileIssues ? request.ProducerCode : null,
                command.ReconcileIssues ? JsonSerializer.Serialize(risk.Derived(effective), RuleSetJson.Options) : null, cancellationToken).ConfigureAwait(false);

            // Reconcile with the job's non-terminal issues by issue key (REQ-UW-059, -092, -093; PRD-04 §7.3): a new key raises
            // an issue; an Open issue is kept; an approval holds while the facts are unchanged and is invalidated (new Open issue)
            // when they change; a rejection stays until the quote's facts change, and facts that were rejected before raise their
            // issue Rejected again. Only POL's in-process evaluation (the snapshot of a real quote version) changes job issues
            // (D-UW-01); an HTTP evaluation sees them read only. Only issues raised by this rule set are reconciled: a PRE_QUOTE
            // evaluation must not close a PRE_BIND referral. A per-job advisory lock serialises evaluations and decisions.
            var ruleIds = ruleSet.Dto.Rules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            if (command.ReconcileIssues)
            {
                await store.LockJobAsync(jobId, cancellationToken).ConfigureAwait(false);
            }

            var active = (await store.ActiveIssuesAsync(legalEntity, jobId, cancellationToken).ConfigureAwait(false)).Where(i => ruleIds.Contains(i.RuleId)).ToList();
            var ruleSetRef = $"{ruleSet.Dto.Code}@{ruleSet.Dto.Version}#{ruleSet.Hash}";
            var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var hit in hits)
            {
                fingerprints.TryAdd(hit.IssueKey, risk.Fingerprint(effective, ruleSetRef, hit.RuleId));
            }

            var steps = IssueReconciliation.Plan([.. active.Select(i => i.ForReconciliation())], fingerprints);
            var items = new List<RulesEvaluateResponse.IssueItem>();
            foreach (var hit in hits)
            {
                var step = steps.First(s => s.IssueKey == hit.IssueKey);
                var fingerprint = fingerprints[hit.IssueKey];
                var blocking = hit.BlockingPoint switch
                {
                    "PRE_QUOTE" => BlockingPoint.PreQuote,
                    "PRE_BIND" => BlockingPoint.PreBind,
                    "PRE_ISSUE" => BlockingPoint.PreIssue,
                    _ => BlockingPoint.NonBlocking,
                };
                var issueId = step.Existing?.IssueId ?? Guid.CreateVersion7();
                var status = step.Existing?.Status ?? IssueStatus.Open;
                // Read only (HTTP): the existing issue for the key as it stands, or a preview of the issue POL's evaluation would raise.
                if (command.ReconcileIssues)
                {
                    switch (step.Action)
                    {
                        case IssueAction.KeepOpen:
                            await store.RefreshFingerprintAsync(issueId, fingerprint, cancellationToken).ConfigureAwait(false);
                            break;
                        case IssueAction.KeepApproved or IssueAction.KeepRejected:
                            break;
                        case IssueAction.InvalidateAndRaise:
                            await store.InvalidateIssueAsync(issueId, evaluationId, "VALUE_CHANGED", cancellationToken).ConfigureAwait(false);
                            events.Publish(new OutgoingEvent(
                                EventDescriptor.From(ApprovalInvalidatedV1.Descriptor), "Job", jobId.ToString("D"),
                                new ApprovalInvalidatedV1
                                {
                                    JobId = request.JobRef,
                                    IssueId = new UwIssueId(issueId),
                                    ApprovalId = issueId, // the approval is recorded on the issue row until UWApproval exists
                                    Reason = ApprovalInvalidatedV1.ReasonValue.ValueChanged,
                                    ChangedFieldCodes = ["riskFacts"],
                                },
                                BusinessKeys.Empty.With("jobId", jobId.ToString("D")).With("issueId", issueId.ToString("D")).With("approvalId", issueId.ToString("D"))));
                            (issueId, status) = await RaiseAsync(hit, blocking, fingerprint, reopened: true).ConfigureAwait(false);
                            break;
                        case IssueAction.CloseAndRaise:
                            await CloseAsync(issueId, "VALUE_CHANGED").ConfigureAwait(false);
                            (issueId, status) = await RaiseAsync(hit, blocking, fingerprint, reopened: true).ConfigureAwait(false);
                            break;
                        default:
                            (issueId, status) = await RaiseAsync(hit, blocking, fingerprint, reopened: false).ConfigureAwait(false);
                            break;
                    }
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
                    ApprovalStatus = status,
                });
            }

            if (command.ReconcileIssues)
            {
                foreach (var gone in steps.Where(s => s.Action is IssueAction.Close or IssueAction.CloseRejected))
                {
                    await CloseAsync(gone.Existing!.IssueId, gone.Action == IssueAction.Close ? "RULE_NO_LONGER_HITS" : "VALUE_CHANGED").ConfigureAwait(false);
                }
            }

            // A new issue for facts a decider already rejected on this job is raised Rejected: a rejection sticks to its facts.
            async Task<(Guid Id, string Status)> RaiseAsync(Hit hit, BlockingPoint blocking, string fingerprint, bool reopened)
            {
                var id = Guid.CreateVersion7();
                var rejected = await store.RejectionOfAsync(legalEntity, jobId, hit.IssueKey, fingerprint, cancellationToken).ConfigureAwait(false);
                await store.InsertIssueAsync(
                    id, legalEntity, jobId, hit.IssueType, hit.IssueKey, hit.BlockingPoint, hit.RuleType, lane, hit.RuleId, hit.MessageEn, hit.MessageEl,
                    evaluationId, fingerprint, rejected, cancellationToken).ConfigureAwait(false);
                events.Publish(new OutgoingEvent(
                    EventDescriptor.From(UWIssueRaisedV1.Descriptor), "Job", jobId.ToString("D"),
                    new UWIssueRaisedV1
                    {
                        JobId = request.JobRef,
                        IssueId = new UwIssueId(id),
                        IssueType = hit.IssueType,
                        IssueKeyHash = Sha256Hash.ComputeUtf8(hit.IssueKey),
                        BlockingPoint = blocking,
                        Severity = hit.RuleType,
                        Lane = lane,
                        Reopened = reopened,
                    },
                    BusinessKeys.Empty.With("jobId", jobId.ToString("D")).With("issueId", id.ToString("D"))));
                return (id, rejected is null ? IssueStatus.Open : IssueStatus.Rejected);
            }

            async Task CloseAsync(Guid id, string reason)
            {
                await store.CloseIssueAsync(id, evaluationId, reason, cancellationToken).ConfigureAwait(false);
                events.Publish(new OutgoingEvent(
                    EventDescriptor.From(UWIssueClosedV1.Descriptor), "Job", jobId.ToString("D"),
                    new UWIssueClosedV1 { JobId = request.JobRef, IssueId = new UwIssueId(id), Reason = reason },
                    BusinessKeys.Empty.With("jobId", jobId.ToString("D")).With("issueId", id.ToString("D"))));
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
                DataStatus = ruleSet.Dto.DataStatus,
                Warnings = [.. ruleSet.Dto.DataStatus == "ILLUSTRATIVE_TEST_DATA" ? ["UW-WARN-ILLUSTRATIVE-RULES"] : Array.Empty<string>(), .. command.ReconcileIssues ? Array.Empty<string>() : ["UW-WARN-ISSUES-NOT-RECORDED"]],
            };
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }

    private static TimeZoneInfo AthensOrUtc()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
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
