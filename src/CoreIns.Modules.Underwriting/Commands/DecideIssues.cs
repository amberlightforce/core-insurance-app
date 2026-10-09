using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CoreIns.Modules.Underwriting.Authority;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Contracts.Events;
using CoreIns.Modules.Underwriting.Domain;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using FluentValidation;

namespace CoreIns.Modules.Underwriting.Commands;

/// <summary><c>uw.Issue.decide</c>: approve or reject one or several Open issues (REQ-UW-079).</summary>
internal sealed record DecideIssues(IssueDecideRequest Request) : ICommand<IssueDecideResponse>;

internal sealed class DecideIssuesValidator : AbstractValidator<DecideIssues>
{
    public DecideIssuesValidator()
    {
        RuleFor(c => c.Request.IssueIds).NotNull().Must(ids => ids is { Count: >= 1 and <= 20 }).WithMessage("Give 1 to 20 issue ids.")
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count).WithMessage("Each issue id may appear once.");
        RuleFor(c => c.Request.Decision).IsInEnum();
        RuleFor(c => c.Request.Reason).NotEmpty().Must(r => !string.IsNullOrWhiteSpace(r)).MaximumLength(1000).WithErrorCode("REASON");
        RuleFor(c => c.Request.Message).MaximumLength(2000);
    }
}

/// <summary>
/// Records a decision per issue (REQ-UW-079) after the guards of PRD-04 §7.3: the decider is a person (REQ-UW-115,
/// BR-UW-014), did not quote or bind the job (SOD-UW-02 / BR-UW-013, REQ-UW-116: whoever ran an evaluation for the job is
/// the requester side), and holds UW.ISSUE_APPROVAL for the issue type — a binding <c>plt.Authority.check</c> per issue whose
/// check id is stored with the decision and on the audit record (REQ-UW-109, -111; BR-UW-011). The approval holds while the
/// fingerprint the decider saw is unchanged (REQ-UW-091). Optimistic on <c>record_version</c>. No second approver: PRD-04
/// asks for one only above the BR-UW-012 large-risk thresholds (motor not applicable) or for UW.SPECIAL_APPROVAL.
/// </summary>
internal sealed class DecideIssuesHandler(
    UnderwritingStore store,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    DecisionEligibility eligibility,
    IEventPublisher events) : ICommandHandler<DecideIssues, IssueDecideResponse>
{
    public async Task<Result<IssueDecideResponse>> HandleAsync(DecideIssues command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (!eligibility.CallerIsHuman)
        {
            return DomainError.Of(ModuleCode.UW, "HUMAN-DECISION-REQUIRED", "Only a person may decide an underwriting issue (REQ-UW-115).");
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
        var ids = request.IssueIds.Select(i => i.Value).ToList();
        // Serialise with evaluations of the same jobs (per-job advisory lock, taken in a fixed order), then lock the rows.
        foreach (var job in await store.JobsOfIssuesAsync(legalEntity, ids, cancellationToken).ConfigureAwait(false))
        {
            await store.LockJobAsync(job, cancellationToken).ConfigureAwait(false);
        }

        var issues = await store.LockIssuesAsync(legalEntity, ids, cancellationToken).ConfigureAwait(false);
        if (ids.FirstOrDefault(id => issues.All(i => i.IssueId != id)) is var missing && missing != Guid.Empty)
        {
            return DomainError.Of(ModuleCode.UW, "NOT-FOUND", $"Issue {missing} was not found.");
        }

        foreach (var issue in issues)
        {
            if (issue.Status != IssueStatus.Open)
            {
                return DomainError.Of(ModuleCode.UW, "ISSUE-TRANSITION", $"Issue {issue.IssueId} is {issue.Status}; only an Open issue can be decided.");
            }

            if (request.ExpectedRecordVersions is { } expected && expected.TryGetValue(issue.IssueId.ToString("D"), out var version) && version != issue.RecordVersion)
            {
                return Stale(issue.IssueId, "The issue changed since it was read; reload it and decide again.");
            }

            if (issue.Fingerprint is null)
            {
                return Stale(issue.IssueId, "The issue was raised before decisions recorded the risk facts; run the bind once more so the rules re-evaluate, then decide.");
            }
        }

        var actor = context.Actor.ToString();
        var participation = await store.ParticipationAsync(legalEntity, [.. issues.Select(i => i.JobId).Distinct()], cancellationToken).ConfigureAwait(false);
        foreach (var jobId in issues.Select(i => i.JobId).Distinct())
        {
            var part = participation.GetValueOrDefault(jobId, JobParticipation.None);
            if (DecisionEligibility.JobChanged(await eligibility.JobAsync(jobId, cancellationToken).ConfigureAwait(false), part))
            {
                return Stale(issues.First(i => i.JobId == jobId).IssueId, "The job changed since it was last evaluated, so the editor is not yet recorded; run the quote or bind again so the rules re-evaluate, then decide (SOD-UW-02).");
            }

            if (eligibility.SodReasons(participation.GetValueOrDefault(jobId, JobParticipation.None)).Count > 0)
            {
                return DomainError.Of(ModuleCode.UW, "SOD", "You created, edited, quoted or bound this job, so you cannot decide its underwriting issues; ask another underwriter with authority (SOD-UW-02).");
            }
        }

        var checks = new Dictionary<Guid, AuthorityCheckResult>();
        foreach (var issue in issues)
        {
            var check = await eligibility.CheckAuthorityAsync(issue.IssueType, ObjectRef.For(ModuleCode.UW, "Issue", new UwIssueId(issue.IssueId)), cancellationToken).ConfigureAwait(false);
            context.AuthorityChecks.Add(check);
            if (check.Decision != AuthorityDecision.Allow)
            {
                var refer = check.Decision == AuthorityDecision.Refer;
                return new DomainError(
                    ErrorCode.For(ModuleCode.UW, refer ? "AUTHORITY-REFER" : PlatformErrors.AuthorityDenied),
                    refer
                        ? $"{check.Type} for {issue.IssueType} is above your authority ({check.ReasonCode}); refer it to {string.Join(", ", check.ReferralTargets.Select(t => t.Id))}."
                        : $"You hold no {check.Type} authority for {issue.IssueType} ({check.ReasonCode}).")
                {
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["issueId"] = issue.IssueId.ToString("D"),
                        ["authorityCheckId"] = check.CheckId.ToString(),
                        ["reasonCode"] = check.ReasonCode,
                        ["referralTargets"] = string.Join(",", check.ReferralTargets.Select(t => $"{t.Kind}:{t.Id}")),
                    },
                };
            }

            checks[issue.IssueId] = check;
        }

        var approve = request.Decision == IssueDecisionCode.Approve;
        var status = approve ? IssueStatus.Approved : IssueStatus.Rejected;
        var reason = request.Reason.Trim();
        var decisions = new List<IssueDecideResponse.DecisionItem>();
        foreach (var issue in issues.OrderBy(i => ids.IndexOf(i.IssueId)))
        {
            var check = checks[issue.IssueId];
            var decided = await store.DecideAsync(
                issue.IssueId, issue.RecordVersion, status, approve ? "APPROVE" : "REJECT", issue.Fingerprint!.Trim(), actor, reason,
                string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim(), check.CheckId.Value, cancellationToken).ConfigureAwait(false);
            if (!decided)
            {
                return Stale(issue.IssueId, "The issue changed while it was being decided; reload it and decide again.");
            }

            var jobRef = JobId.From(issue.JobId);
            var keys = BusinessKeys.Empty.With("jobId", issue.JobId.ToString("D")).With("issueId", issue.IssueId.ToString("D"));
            if (approve)
            {
                events.Publish(new OutgoingEvent(
                    EventDescriptor.From(UWIssueApprovedV1.Descriptor), "Job", issue.JobId.ToString("D"),
                    new UWIssueApprovedV1
                    {
                        JobId = jobRef,
                        IssueId = new UwIssueId(issue.IssueId),
                        IssueType = issue.IssueType,
                        BlockingPoint = Point(issue.BlockingPoint),
                        Outcome = UWIssueApprovedV1.OutcomeValue.Approved,
                        ApproverUserId = UserIdOf(context.Actor),
                        Scope = "JOB",
                        ConditionKinds = [],
                        AuthorityCheckIds = [check.CheckId],
                    },
                    keys));
            }
            else
            {
                events.Publish(new OutgoingEvent(
                    EventDescriptor.From(UWIssueRejectedV1.Descriptor), "Job", issue.JobId.ToString("D"),
                    new UWIssueRejectedV1
                    {
                        JobId = jobRef,
                        IssueId = new UwIssueId(issue.IssueId),
                        IssueType = issue.IssueType,
                        ApproverUserId = UserIdOf(context.Actor),
                        ReasonCode = "UNDERWRITER_REJECTED",
                    },
                    keys));
            }

            decisions.Add(new IssueDecideResponse.DecisionItem
            {
                IssueId = new UwIssueId(issue.IssueId),
                Status = approve ? IssueStatusCode.Approved : IssueStatusCode.Rejected,
                RecordVersion = issue.RecordVersion + 1,
                AuthorityCheckId = check.CheckId,
            });
        }

        return new IssueDecideResponse
        {
            Decisions = decisions,
            CheckIds = [.. decisions.Select(d => d.AuthorityCheckId.Value)],
            PendingSecondApproval = false,
        };
    }

    private static DomainError Stale(Guid issueId, string message) =>
        new(ErrorCode.For(ModuleCode.UW, "STALE"), message) { Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["issueId"] = issueId.ToString("D") } };

    internal static BlockingPoint Point(string point) => point switch
    {
        "PRE_QUOTE" => BlockingPoint.PreQuote,
        "PRE_BIND" => BlockingPoint.PreBind,
        "PRE_ISSUE" => BlockingPoint.PreIssue,
        _ => BlockingPoint.NonBlocking,
    };

    /// <summary>
    /// The person's directory object id when the actor id is a GUID (Entra oid); otherwise (synthetic development users such as
    /// <c>dev:uwsenior</c>) a stable name-based id derived from the actor id, so the event never carries the raw sign-in name.
    /// </summary>
    internal static UserId UserIdOf(ActorRef actor)
    {
        if (Guid.TryParse(actor.Id, out var oid) && oid != Guid.Empty)
        {
            return UserId.From(oid);
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("coreins:user:" + actor.Id))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8 (custom)
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 9562 variant
        return UserId.From(new Guid(bytes, bigEndian: true));
    }
}

/// <summary>Audit facts of a decision: the issues and their jobs (the authority checks are recorded by the pipeline from the request context).</summary>
internal sealed class DecideIssuesAuditor : ICommandAuditor<DecideIssues, IssueDecideResponse>
{
    public CommandAuditFacts Describe(DecideIssues command, Result<IssueDecideResponse>? result)
    {
        var first = command.Request.IssueIds.Count > 0 ? command.Request.IssueIds[0].Value : Guid.Empty;
        return new CommandAuditFacts
        {
            ObjectRef = first == Guid.Empty ? null : ObjectRef.For(ModuleCode.UW, "Issue", new UwIssueId(first)),
            BusinessKeys = BusinessKeys.Empty.With("issueId", first.ToString("D")),
            Changes = result is { IsSuccess: true } ok
                ? Platform.Audit.AuditDiff.Compute(
                    new { status = "Open" },
                    new
                    {
                        decision = command.Request.Decision.ToString().ToUpper(CultureInfo.InvariantCulture),
                        issues = ok.Value.Decisions.Select(d => new { issueId = d.IssueId.Value, status = d.Status.ToString(), authorityCheckId = d.AuthorityCheckId.Value }),
                        authorityCheckIds = ok.Value.CheckIds,
                    })
                : [],
        };
    }
}
