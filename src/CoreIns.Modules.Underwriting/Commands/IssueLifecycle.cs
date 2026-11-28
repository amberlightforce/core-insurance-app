using System.Security.Cryptography;
using System.Text;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Contracts.Events;
using CoreIns.Modules.Underwriting.Domain;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Modules.Underwriting.Services;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel.Identifiers;
using FluentValidation;

namespace CoreIns.Modules.Underwriting.Commands;

/// <summary><c>uw.Issue.reopen</c>: put an Approved or Rejected issue back to Open (REQ-UW-080).</summary>
internal sealed record ReopenIssue(IssueReopenRequest Request) : ICommand<IssueReopenResponse>;

/// <summary><c>uw.Issue.addManual</c>: an underwriter raises an issue by hand (REQ-UW-100).</summary>
internal sealed record AddManualIssue(IssueAddManualRequest Request) : ICommand<IssueAddManualResponse>;

/// <summary><c>uw.Issue.close</c>: close an issue that does not block, with a reason.</summary>
internal sealed record CloseIssue(IssueCloseRequest Request) : ICommand<IssueCloseResponse>;

internal sealed class ReopenIssueValidator : AbstractValidator<ReopenIssue>
{
    public ReopenIssueValidator() =>
        RuleFor(c => c.Request.Reason).NotEmpty().Must(r => !string.IsNullOrWhiteSpace(r)).MaximumLength(1000).WithErrorCode("REASON");
}

internal sealed class AddManualIssueValidator : AbstractValidator<AddManualIssue>
{
    public AddManualIssueValidator()
    {
        RuleFor(c => c.Request.JobRef.Value).NotEqual(Guid.Empty);
        RuleFor(c => c.Request.IssueType).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Request.BlockingPoint).IsInEnum();
        RuleFor(c => c.Request.Reason).NotEmpty().Must(r => !string.IsNullOrWhiteSpace(r)).MaximumLength(1000).WithErrorCode("REASON");
    }
}

internal sealed class CloseIssueValidator : AbstractValidator<CloseIssue>
{
    public CloseIssueValidator() =>
        RuleFor(c => c.Request.Reason).NotEmpty().Must(r => !string.IsNullOrWhiteSpace(r)).MaximumLength(1000).WithErrorCode("REASON");
}

/// <summary>
/// Shared steps of the lifecycle commands: the caller is a person, and the job is serialised with evaluations and decisions (the
/// per-job advisory lock, then the issue row lock) so a concurrent evaluation cannot reconcile the issue while it is changed.
/// TODO(hardening): refuse while another underwriter holds the review lock of the referral (as decide does); persist the reason of a
/// manual issue on the row (today it is on the audit record only); raised-by of a manual issue shows the evaluator of its anchor.
/// </summary>
internal sealed class IssueLifecycleSupport(
    UnderwritingStore store, IssueLifecycleStore lifecycle, RequestContext context, ILegalEntityDirectory legalEntities, DecisionEligibility eligibility, IEventPublisher events)
{
    public UnderwritingStore Store => store;

    public IssueLifecycleStore Lifecycle => lifecycle;

    public Guid LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;

    public DomainError? RequireHuman() =>
        eligibility.CallerIsHuman ? null : DomainError.Of(ModuleCode.UW, "HUMAN-DECISION-REQUIRED", "Only a person may change an underwriting issue (REQ-UW-115).");

    /// <summary>Locks the job, then the issue; the issue as stored, or the not-found / stale error.</summary>
    public async Task<(IssueRecord? Issue, DomainError? Error)> LockAsync(Guid issueId, int? expectedVersion, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        foreach (var job in await store.JobsOfIssuesAsync(legalEntity, [issueId], cancellationToken).ConfigureAwait(false))
        {
            await store.LockJobAsync(job, cancellationToken).ConfigureAwait(false);
        }

        var issue = (await store.LockIssuesAsync(legalEntity, [issueId], cancellationToken).ConfigureAwait(false)) is [var found, ..] ? found : null;
        if (issue is null)
        {
            return (null, DomainError.Of(ModuleCode.UW, "ISSUE-NOT-FOUND", $"Issue {issueId:D} was not found."));
        }

        return expectedVersion is { } expected && expected != issue.RecordVersion ? (null, Stale(issueId)) : (issue, null);
    }

    public async Task<IssueListItem> ReadAsync(Guid issueId, CancellationToken cancellationToken) =>
        UnderwritingIssueQueries.Item((await store.LockIssuesAsync(LegalEntity, [issueId], cancellationToken).ConfigureAwait(false))[0]);

    public static DomainError Stale(Guid issueId) =>
        new(ErrorCode.For(ModuleCode.UW, "STALE"), "The issue changed since it was read; reload it and try again.")
        { Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["issueId"] = issueId.ToString("D") } };

    public static DomainError Transition(string message) => DomainError.Of(ModuleCode.UW, "ISSUE-TRANSITION", message);

    public void PublishRaised(Guid jobId, Guid issueId, string issueType, string issueKey, BlockingPoint point, string severity, string lane, bool reopened) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(UWIssueRaisedV1.Descriptor), "Job", jobId.ToString("D"),
            new UWIssueRaisedV1
            {
                JobId = JobId.From(jobId),
                IssueId = new UwIssueId(issueId),
                IssueType = issueType,
                IssueKeyHash = Sha256Hash.ComputeUtf8(issueKey),
                BlockingPoint = point,
                Severity = severity,
                Lane = lane,
                Reopened = reopened,
            },
            Keys(jobId, issueId)));

    public void PublishClosed(Guid jobId, Guid issueId, string reason) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(UWIssueClosedV1.Descriptor), "Job", jobId.ToString("D"),
            new UWIssueClosedV1 { JobId = JobId.From(jobId), IssueId = new UwIssueId(issueId), Reason = reason },
            Keys(jobId, issueId)));

    public void PublishInvalidated(Guid jobId, Guid issueId, ApprovalInvalidatedV1.ReasonValue reason, string changed) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ApprovalInvalidatedV1.Descriptor), "Job", jobId.ToString("D"),
            new ApprovalInvalidatedV1
            {
                JobId = JobId.From(jobId),
                IssueId = new UwIssueId(issueId),
                ApprovalId = issueId, // the approval is recorded on the issue row until UWApproval exists
                Reason = reason,
                ChangedFieldCodes = [changed],
            },
            Keys(jobId, issueId).With("approvalId", issueId.ToString("D"))));

    private static BusinessKeys Keys(Guid jobId, Guid issueId) => BusinessKeys.Empty.With("jobId", jobId.ToString("D")).With("issueId", issueId.ToString("D"));
}

/// <summary>
/// Reopening an approval invalidates it (Invalidated, MANUAL_REOPEN, <c>ApprovalInvalidated</c> published) and a new Open issue for
/// the same key takes its place, so the point blocks again until it is decided afresh. A rejection is closed and replaced the same
/// way. An Open, Invalidated or Closed issue cannot be reopened (UW-ERR-ISSUE-TRANSITION).
/// </summary>
internal sealed class ReopenIssueHandler(IssueLifecycleSupport support) : ICommandHandler<ReopenIssue, IssueReopenResponse>
{
    public async Task<Result<IssueReopenResponse>> HandleAsync(ReopenIssue command, CancellationToken cancellationToken)
    {
        if (support.RequireHuman() is { } notHuman)
        {
            return notHuman;
        }

        var request = command.Request;
        var (issue, error) = await support.LockAsync(request.IssueId.Value, request.ExpectedRecordVersion, cancellationToken).ConfigureAwait(false);
        if (issue is null)
        {
            return error!;
        }

        var approved = IssueStatus.IsApproved(issue.Status);
        if (!approved && issue.Status != IssueStatus.Rejected)
        {
            return IssueLifecycleSupport.Transition($"Issue {issue.IssueId} is {issue.Status}; only an Approved or Rejected issue can be reopened.");
        }

        if (!await support.Lifecycle.RetireForReopenAsync(
                issue.IssueId, issue.RecordVersion, approved ? IssueStatus.Invalidated : IssueStatus.Closed, "MANUAL_REOPEN", cancellationToken).ConfigureAwait(false))
        {
            return IssueLifecycleSupport.Stale(issue.IssueId);
        }

        if (approved)
        {
            support.PublishInvalidated(issue.JobId, issue.IssueId, ApprovalInvalidatedV1.ReasonValue.ManualReopen, "manualReopen");
        }
        else
        {
            support.PublishClosed(issue.JobId, issue.IssueId, "MANUAL_REOPEN");
        }

        var newId = Guid.CreateVersion7();
        await support.Lifecycle.CopyAsOpenAsync(issue.IssueId, newId, cancellationToken).ConfigureAwait(false);
        support.PublishRaised(issue.JobId, newId, issue.IssueType, issue.IssueKey, DecideIssuesHandler.Point(issue.BlockingPoint), issue.Severity, issue.Lane, reopened: true);
        return new IssueReopenResponse { Issue = await support.ReadAsync(newId, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>
/// An underwriter raises an issue by hand on an evaluated job: Open, blocking its point, anchored to the job's latest evaluation. Its
/// key is <c>MANUAL:type:id</c> and its rule id is <c>MANUAL</c>, so no rule-set evaluation reconciles or closes it; it ends by a
/// decision (<c>uw.Issue.decide</c>) or <c>uw.Issue.close</c>.
/// </summary>
internal sealed class AddManualIssueHandler(IssueLifecycleSupport support, IssueTypeStore issueTypes) : ICommandHandler<AddManualIssue, IssueAddManualResponse>
{
    public async Task<Result<IssueAddManualResponse>> HandleAsync(AddManualIssue command, CancellationToken cancellationToken)
    {
        if (support.RequireHuman() is { } notHuman)
        {
            return notHuman;
        }

        var request = command.Request;
        var jobId = request.JobRef.Value;
        var type = await issueTypes.GetAsync(request.IssueType, cancellationToken).ConfigureAwait(false);
        if (type is not { Active: true })
        {
            return IssueLifecycleSupport.Transition($"Issue type {request.IssueType} is not an active type of the catalogue.");
        }

        await support.Store.LockJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (await support.Lifecycle.LatestEvaluationAsync(support.LegalEntity, jobId, cancellationToken).ConfigureAwait(false) is not { } evaluationId)
        {
            return IssueLifecycleSupport.Transition("The job has not been evaluated by underwriting yet, so it has no issues to add to; run the quote or bind first.");
        }

        var issueId = Guid.CreateVersion7();
        var key = $"MANUAL:{type.Code}:{issueId:N}";
        var point = request.BlockingPoint switch
        {
            BlockingPoint.PreQuote => "PRE_QUOTE",
            BlockingPoint.PreBind => "PRE_BIND",
            BlockingPoint.PreIssue => "PRE_ISSUE",
            _ => "NON_BLOCKING",
        };
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        await support.Store.InsertIssueAsync(
            issueId, support.LegalEntity, jobId, type.Code, key, point, type.Severity, type.DefaultLane, "MANUAL", type.NameEn, type.NameEl, evaluationId, fingerprint, null, cancellationToken).ConfigureAwait(false);
        support.PublishRaised(jobId, issueId, type.Code, key, request.BlockingPoint, type.Severity, type.DefaultLane, reopened: false);
        return new IssueAddManualResponse { Issue = await support.ReadAsync(issueId, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>
/// Closes an Open issue that does not block, or an approval (closed with the reason). An Open issue that blocks (or a Rejected
/// one) cannot be closed (UW-ERR-ISSUE-TRANSITION): approve it, or let the rules stop hitting.
/// </summary>
internal sealed class CloseIssueHandler(IssueLifecycleSupport support) : ICommandHandler<CloseIssue, IssueCloseResponse>
{
    public async Task<Result<IssueCloseResponse>> HandleAsync(CloseIssue command, CancellationToken cancellationToken)
    {
        if (support.RequireHuman() is { } notHuman)
        {
            return notHuman;
        }

        var request = command.Request;
        var (issue, error) = await support.LockAsync(request.IssueId.Value, request.ExpectedRecordVersion, cancellationToken).ConfigureAwait(false);
        if (issue is null)
        {
            return error!;
        }

        var closable = IssueStatus.IsApproved(issue.Status) || (issue.Status == IssueStatus.Open && issue.BlockingPoint == "NON_BLOCKING");
        if (!closable)
        {
            return IssueLifecycleSupport.Transition(
                IssueStatus.Blocks(issue.Status) && issue.BlockingPoint != "NON_BLOCKING"
                    ? $"Issue {issue.IssueId} is {issue.Status} and blocks {issue.BlockingPoint}; decide it instead of closing it."
                    : $"Issue {issue.IssueId} is {issue.Status}; it cannot be closed.");
        }

        if (!await support.Lifecycle.CloseAsync(issue.IssueId, issue.RecordVersion, "MANUAL_CLOSE", cancellationToken).ConfigureAwait(false))
        {
            return IssueLifecycleSupport.Stale(issue.IssueId);
        }

        support.PublishClosed(issue.JobId, issue.IssueId, "MANUAL_CLOSE");
        return new IssueCloseResponse { Issue = await support.ReadAsync(issue.IssueId, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>Audit facts of the lifecycle commands: the issue acted on, the status reached and the reason given.</summary>
internal sealed class IssueLifecycleAuditor<TCommand, TResult> : ICommandAuditor<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public CommandAuditFacts Describe(TCommand command, Result<TResult>? result)
    {
        var (action, reason, requested) = command switch
        {
            ReopenIssue c => ("REOPEN", c.Request.Reason, c.Request.IssueId.Value),
            CloseIssue c => ("CLOSE", c.Request.Reason, c.Request.IssueId.Value),
            AddManualIssue c => ("ADD_MANUAL", c.Request.Reason, Guid.Empty),
            _ => ("?", string.Empty, Guid.Empty),
        };
        var issue = result is { IsSuccess: true } ok
            ? ok.Value switch
            {
                IssueReopenResponse r => r.Issue,
                IssueCloseResponse r => r.Issue,
                IssueAddManualResponse r => r.Issue,
                _ => null,
            }
            : null;
        var subject = issue?.Id ?? requested;
        var keys = BusinessKeys.Empty;
        if (subject != Guid.Empty)
        {
            keys = keys.With("issueId", subject.ToString("D"));
        }

        if (issue is not null)
        {
            keys = keys.With("jobId", issue.JobRef.Value.ToString("D"));
        }

        return new CommandAuditFacts
        {
            ObjectRef = subject == Guid.Empty ? null : ObjectRef.For(ModuleCode.UW, "Issue", new UwIssueId(subject)),
            BusinessKeys = keys,
            Changes = issue is null
                ? []
                : Platform.Audit.AuditDiff.Compute(new { }, new { action, status = issue.Status.ToString(), issueId = issue.Id, reason }),
        };
    }
}
