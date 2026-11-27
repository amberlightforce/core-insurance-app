using System.Security.Cryptography;
using System.Text;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Domain;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Modules.Work.Contracts;
using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Underwriting.Commands;

/// <summary><c>uw.Referral.request</c>: put a job in front of the underwriters (or resume it after information was supplied).</summary>
internal sealed record RequestReferral(ReferralRequestRequest Request) : ICommand<ReferralRequestResponse>;

/// <summary><c>uw.Referral.withdraw</c>: take a referral back.</summary>
internal sealed record WithdrawReferral(ReferralWithdrawRequest Request) : ICommand<ReferralWithdrawResponse>;

/// <summary><c>uw.Referral.referUp</c>: send a referral to a higher authority.</summary>
internal sealed record ReferReferralUp(ReferralReferUpRequest Request) : ICommand<ReferralReferUpResponse>;

/// <summary><c>uw.Referral.requestInformation</c>: pause the SLA and ask for more information.</summary>
internal sealed record RequestReferralInformation(ReferralRequestInformationRequest Request) : ICommand<ReferralRequestInformationResponse>;

/// <summary><c>uw.Review.lock</c>: take or renew the soft review lock.</summary>
internal sealed record LockReview(ReviewLockRequest Request) : ICommand<ReviewLockResponse>;

/// <summary><c>uw.Review.release</c>: release the lock, or break another underwriter's lock (a manager, with a reason).</summary>
internal sealed record ReleaseReview(ReviewReleaseRequest Request) : ICommand<ReviewReleaseResponse>;

internal sealed class RequestReferralValidator : AbstractValidator<RequestReferral>
{
    public RequestReferralValidator()
    {
        RuleFor(c => c.Request.Comment).NotEmpty().Must(c => !string.IsNullOrWhiteSpace(c)).MaximumLength(2000).WithErrorCode("REASON");
        RuleFor(c => c.Request.IssueIds).Must(ids => ids is null || (ids.Count <= 20 && ids.Distinct().Count() == ids.Count)).WithMessage("Give at most 20 distinct issue ids.");
    }
}

internal sealed class WithdrawReferralValidator : AbstractValidator<WithdrawReferral>
{
    public WithdrawReferralValidator() =>
        RuleFor(c => c.Request.Comment).NotEmpty().Must(c => !string.IsNullOrWhiteSpace(c)).MaximumLength(2000).WithErrorCode("REASON");
}

internal sealed class ReferReferralUpValidator : AbstractValidator<ReferReferralUp>
{
    public ReferReferralUpValidator()
    {
        RuleFor(c => c.Request.Comment).NotEmpty().Must(c => !string.IsNullOrWhiteSpace(c)).MaximumLength(2000).WithErrorCode("REASON");
        RuleFor(c => c.Request.TargetRole).MaximumLength(128);
    }
}

internal sealed class RequestReferralInformationValidator : AbstractValidator<RequestReferralInformation>
{
    public RequestReferralInformationValidator() =>
        RuleFor(c => c.Request.Comment).NotEmpty().Must(c => !string.IsNullOrWhiteSpace(c)).MaximumLength(2000).WithErrorCode("REASON");
}

/// <summary>
/// Shared pieces of the referral commands (BO-UW-05): the caller must be a person, the job is serialised with evaluations and decisions
/// (per-job advisory lock), the referral state is created from the job's Open issues, and a command by someone other than the live
/// lock holder is refused while the lock stands.
/// </summary>
internal sealed class ReferralSupport(
    ReferralStateStore states,
    UnderwritingStore store,
    ReferralReads reads,
    DecisionEligibility eligibility,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    IOptions<UnderwritingOptions> options)
{
    /// <summary>Roles that may break a review lock (a manager; the platform administrator).</summary>
    private static readonly string[] ManagerRoles = ["Staff.UnderwritingManager", "Platform.Admin"];

    public ReferralStateStore States => states;

    public DecisionEligibility Eligibility => eligibility;

    public string Me => context.Actor.ToString();

    public DateTime Now => clock.Now.ToUtcDateTime();

    public Guid LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;

    public bool IsManager => ManagerRoles.Any(context.Roles.Contains);

    public TimeSpan Lease => TimeSpan.FromTicks(TimeSpan.TicksPerMinute * Math.Max(1, options.Value.ReviewLockMinutes));

    /// <summary>Refuses a non-person, then takes the job's advisory lock.</summary>
    public async Task<DomainError?> BeginAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (!eligibility.CallerIsHuman)
        {
            return DomainError.Of(ModuleCode.UW, "HUMAN-DECISION-REQUIRED", "Only a person may handle a referral.");
        }

        await store.LockJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        return null;
    }

    public async Task<IReadOnlyList<IssueRecord>> OpenIssuesAsync(Guid jobId, CancellationToken cancellationToken) =>
        [.. (await reads.IssuesOfJobsAsync(LegalEntity, [jobId], cancellationToken).ConfigureAwait(false)).Select(i => i.Issue).Where(i => i.Status == IssueStatus.Open)];

    public static DomainError NotReferrable(Guid jobId, string why) =>
        DomainError.Of(ModuleCode.UW, "NOT-REFERRABLE", $"Job {jobId:D} is not a referral: {why}");

    /// <summary>The referral state of the job; one is created (Active) from the job's Open issues when it has none yet.</summary>
    public async Task<(ReferralStateRecord? State, DomainError? Error)> EnsureAsync(Guid jobId, string comment, CancellationToken cancellationToken)
    {
        var state = await states.GetAsync(LegalEntity, jobId, cancellationToken).ConfigureAwait(false);
        if (state is not null)
        {
            return (state, null);
        }

        if ((await OpenIssuesAsync(jobId, cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            return (null, NotReferrable(jobId, "it has no Open underwriting issue."));
        }

        state = NewState(jobId, comment);
        await states.InsertAsync(state, cancellationToken).ConfigureAwait(false);
        return (state, null);
    }

    public ReferralStateRecord NewState(Guid jobId, string comment)
    {
        var now = Now;
        return new ReferralStateRecord
        {
            LegalEntityId = LegalEntity,
            JobId = jobId,
            Lifecycle = "Active",
            Priority = "NORMAL",
            SlaDue = now.AddTicks(TimeSpan.TicksPerHour * Math.Max(1, options.Value.ReferralSlaHours)),
            RequestComment = comment.Trim(),
            RequestedBy = Me,
            RequestedAt = now,
            UpdatedAt = now,
            UpdatedBy = Me,
        };
    }

    public static DomainError? Stale(ReferralStateRecord state, int? expected) =>
        expected is { } version && version != state.RecordVersion
            ? DomainError.Of(ModuleCode.UW, "STALE", "The referral changed since it was read; reload it and try again.")
            : null;

    /// <summary>Another underwriter holds the live lock: the command is refused (the holder, or nobody, may proceed).</summary>
    public DomainError? GuardLock(ReferralStateRecord state)
    {
        var holder = state.LiveLockOwner(Now);
        return holder is not null && !string.Equals(holder, Me, StringComparison.Ordinal) ? LockedBy(state) : null;
    }

    public static DomainError LockedBy(ReferralStateRecord state) =>
        new(ErrorCode.For(ModuleCode.UW, "LOCKED-BY-OTHER"), $"{state.LockOwner} is reviewing this referral until {state.LockExpiresAt:u}.")
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["lockOwner"] = state.LockOwner ?? string.Empty,
                ["lockExpiresAt"] = state.LockExpiresAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            },
        };

    /// <summary>Writes the changed state (bumping the record version); a concurrent change answers STALE.</summary>
    public async Task<DomainError?> SaveAsync(ReferralStateRecord state, CancellationToken cancellationToken)
    {
        state.UpdatedAt = Now;
        state.UpdatedBy = Me;
        return await states.UpdateAsync(state, cancellationToken).ConfigureAwait(false)
            ? null
            : DomainError.Of(ModuleCode.UW, "STALE", "The referral changed while it was being saved; reload it and try again.");
    }

    public ReferralState ToDto(ReferralStateRecord s)
    {
        var holder = s.LiveLockOwner(Now);
        return new ReferralState
        {
            JobRef = JobId.From(s.JobId),
            Lifecycle = s.IsActive ? ReferralLifecycleCode.Active : ReferralLifecycleCode.Withdrawn,
            AwaitingInformation = s.AwaitingInfo,
            RequiredRole = s.RequiredRole,
            ReferredBy = s.ReferredBy,
            Priority = string.Equals(s.Priority, "HIGH", StringComparison.Ordinal) ? ReferralPriorityCode.High : ReferralPriorityCode.Normal,
            SlaDue = s.SlaDue is { } due ? Time(due) : null,
            RequestedBy = s.RequestedBy,
            RequestedAt = Time(s.RequestedAt),
            Lock = holder is null
                ? null
                : new ReviewLock
                {
                    Owner = holder,
                    AcquiredAt = Time(s.LockAcquiredAt ?? s.UpdatedAt),
                    ExpiresAt = Time(s.LockExpiresAt!.Value),
                    HeldByMe = string.Equals(holder, Me, StringComparison.Ordinal),
                },
            RecordVersion = s.RecordVersion,
        };
    }

    private static Instant Time(DateTime value) => Instant.FromUtcDateTime(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

/// <summary>
/// Raises the follow-up task in the Work queue through <c>wrk.Activity.create</c> (NX-HD-05 patterns). Same stance as the PSR tasks:
/// the referral state is the system of record, a missing or failing Work service is logged and never blocks the underwriter.
/// TODO(hardening): retry a failed raise from a sweeper instead of only logging it.
/// </summary>
internal sealed partial class UwWorkTasks(IServiceProvider services, ILogger<UwWorkTasks> logger)
{
    public const string InformationPattern = "WRK-UW-REFERRAL-INFORMATION";
    public const string ReferUpPattern = "WRK-UW-REFERRAL-REFER-UP";

    public async Task RaiseAsync(string patternCode, Guid jobId, string cause, string? suggestedAssignee, CancellationToken cancellationToken)
    {
        var work = services.GetService<IWorkActivityService>();
        if (work is null)
        {
            LogUnavailable(logger, patternCode);
            return;
        }

        var primary = new ObjectRef(ModuleCode.UW, "Referral", jobId.ToString("D"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{patternCode}|{primary}|{cause}"));
        try
        {
            await work.CreateAsync(
                new ActivityCreateRequest { PatternCode = patternCode, PrimaryObject = primary, RaisingModule = ModuleCode.UW, CauseRef = cause, SuggestedAssignee = suggestedAssignee },
                new CommandOptions(IdempotencyKey.From(new Guid(hash.AsSpan(0, 16)))),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.ToString().EndsWith("DUPLICATE", StringComparison.Ordinal))
        {
            // already open: nothing to add
        }
#pragma warning disable CA1031 // The referral is already recorded; staff still see it in the workbench.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogFailed(logger, ex, patternCode);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Work service is not available; referral task {Pattern} was not raised.")]
    private static partial void LogUnavailable(ILogger logger, string pattern);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Raising the referral task {Pattern} failed; the referral state stands.")]
    private static partial void LogFailed(ILogger logger, Exception exception, string pattern);
}

internal sealed class RequestReferralHandler(ReferralSupport support) : ICommandHandler<RequestReferral, ReferralRequestResponse>
{
    public async Task<Result<ReferralRequestResponse>> HandleAsync(RequestReferral command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var jobId = request.JobRef.Value;
        if (await support.BeginAsync(jobId, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var open = await support.OpenIssuesAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (request.IssueIds is { Count: > 0 } ids && ids.Any(id => open.All(i => i.IssueId != id.Value)))
        {
            return ReferralSupport.NotReferrable(jobId, "an issue given is not an Open issue of the job.");
        }

        var state = await support.States.GetAsync(support.LegalEntity, jobId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            if (open.Count == 0)
            {
                return ReferralSupport.NotReferrable(jobId, "it has no Open underwriting issue.");
            }

            state = support.NewState(jobId, request.Comment);
            await support.States.InsertAsync(state, cancellationToken).ConfigureAwait(false);
            return new ReferralRequestResponse { Referral = support.ToDto(state) };
        }

        if (ReferralSupport.Stale(state, request.ExpectedRecordVersion) is { } stale)
        {
            return stale;
        }

        if (support.GuardLock(state) is { } locked)
        {
            return locked;
        }

        var now = support.Now;
        if (!state.IsActive)
        {
            // A withdrawn referral is raised again from scratch.
            if (open.Count == 0)
            {
                return ReferralSupport.NotReferrable(jobId, "it has no Open underwriting issue.");
            }

            var fresh = support.NewState(jobId, request.Comment);
            state.Lifecycle = "Active";
            state.AwaitingInfo = false;
            state.InfoComment = null;
            state.SlaPausedAt = null;
            state.SlaDue = fresh.SlaDue;
            state.Priority = "NORMAL";
            state.RequiredRole = null;
            state.ReferredBy = null;
            state.RequestComment = fresh.RequestComment;
            state.RequestedBy = fresh.RequestedBy;
            state.RequestedAt = now;
        }
        else if (state.AwaitingInfo)
        {
            // The information arrived: resume the SLA clock by the time it was paused.
            if (state.SlaDue is { } due && state.SlaPausedAt is { } pausedAt)
            {
                state.SlaDue = due + (now - pausedAt);
            }

            state.AwaitingInfo = false;
            state.SlaPausedAt = null;
        }
        else
        {
            // Already with the underwriters: nothing to change.
            return new ReferralRequestResponse { Referral = support.ToDto(state) };
        }

        if (await support.SaveAsync(state, cancellationToken).ConfigureAwait(false) is { } error)
        {
            return error;
        }

        return new ReferralRequestResponse { Referral = support.ToDto(state) };
    }
}

internal sealed class WithdrawReferralHandler(ReferralSupport support) : ICommandHandler<WithdrawReferral, ReferralWithdrawResponse>
{
    public async Task<Result<ReferralWithdrawResponse>> HandleAsync(WithdrawReferral command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var jobId = request.JobRef.Value;
        if (await support.BeginAsync(jobId, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var state = await support.States.GetAsync(support.LegalEntity, jobId, cancellationToken).ConfigureAwait(false);
        if (state is null || !state.IsActive)
        {
            return ReferralSupport.NotReferrable(jobId, "it has no active referral to withdraw.");
        }

        if (ReferralSupport.Stale(state, request.ExpectedRecordVersion) is { } stale)
        {
            return stale;
        }

        if (!string.Equals(state.RequestedBy, support.Me, StringComparison.Ordinal) && !support.IsManager)
        {
            return DomainError.Of(ModuleCode.UW, "AUTHORITY-DENIED", "Only the person who requested the referral, or a manager, may withdraw it.");
        }

        if (support.GuardLock(state) is { } locked)
        {
            return locked;
        }

        state.Lifecycle = "Withdrawn";
        state.AwaitingInfo = false;
        state.SlaPausedAt = null;
        state.ClearLock();
        if (await support.SaveAsync(state, cancellationToken).ConfigureAwait(false) is { } error)
        {
            return error;
        }

        return new ReferralWithdrawResponse { Referral = support.ToDto(state) };
    }
}

internal sealed class ReferReferralUpHandler(ReferralSupport support, UwWorkTasks work) : ICommandHandler<ReferReferralUp, ReferralReferUpResponse>
{
    public async Task<Result<ReferralReferUpResponse>> HandleAsync(ReferReferralUp command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var jobId = request.JobRef.Value;
        if (await support.BeginAsync(jobId, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var (state, error) = await support.EnsureAsync(jobId, request.Comment, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (!state!.IsActive)
        {
            return ReferralSupport.NotReferrable(jobId, "the referral was withdrawn.");
        }

        if (ReferralSupport.Stale(state, request.ExpectedRecordVersion) is { } stale)
        {
            return stale;
        }

        if (support.GuardLock(state) is { } locked)
        {
            return locked;
        }

        // The higher authority: the role the UW.ISSUE_APPROVAL check of the job's first Open issue names as a referral target
        // (the grants' limits decide who is "higher"), or the role the caller names.
        var open = await support.OpenIssuesAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (open.Count == 0)
        {
            return ReferralSupport.NotReferrable(jobId, "it has no Open underwriting issue to refer up.");
        }

        var check = await support.Eligibility.CheckAuthorityAsync(open[0].IssueType, ObjectRef.For(ModuleCode.UW, "Issue", new UwIssueId(open[0].IssueId)), cancellationToken).ConfigureAwait(false);
        var targets = check.ReferralTargets.Where(t => string.Equals(t.Kind, "ROLE", StringComparison.Ordinal)).Select(t => t.Id).ToList();
        string role;
        if (!string.IsNullOrWhiteSpace(request.TargetRole))
        {
            role = request.TargetRole.Trim();
            // TODO(hardening): with no named target (the caller already holds the authority) any Staff or Platform role is accepted; narrow it to the grants above the caller's.
            if (targets.Count > 0 ? !targets.Contains(role, StringComparer.Ordinal) : !(role.StartsWith("Staff.", StringComparison.Ordinal) || role.StartsWith("Platform.", StringComparison.Ordinal)))
            {
                return DomainError.Of(ModuleCode.UW, "AUTHORITY-DENIED", $"{role} is not a higher authority for {open[0].IssueType}.");
            }
        }
        else if (targets.Count > 0)
        {
            role = targets[0];
        }
        else
        {
            return ReferralSupport.NotReferrable(jobId, "no higher authority is configured for its issue type; name a targetRole.");
        }

        state.RequiredRole = role;
        state.ReferredBy = support.Me;
        state.Priority = "HIGH";
        state.AwaitingInfo = false;
        state.ClearLock();
        if (await support.SaveAsync(state, cancellationToken).ConfigureAwait(false) is { } saveError)
        {
            return saveError;
        }

        await work.RaiseAsync(UwWorkTasks.ReferUpPattern, jobId, $"refer-up:{jobId:D}:{state.RecordVersion}", null, cancellationToken).ConfigureAwait(false);
        return new ReferralReferUpResponse { Referral = support.ToDto(state) };
    }
}

internal sealed class RequestReferralInformationHandler(ReferralSupport support, UwWorkTasks work) : ICommandHandler<RequestReferralInformation, ReferralRequestInformationResponse>
{
    public async Task<Result<ReferralRequestInformationResponse>> HandleAsync(RequestReferralInformation command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var jobId = request.JobRef.Value;
        if (await support.BeginAsync(jobId, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var (state, error) = await support.EnsureAsync(jobId, request.Comment, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (!state!.IsActive)
        {
            return ReferralSupport.NotReferrable(jobId, "the referral was withdrawn.");
        }

        if (ReferralSupport.Stale(state, request.ExpectedRecordVersion) is { } stale)
        {
            return stale;
        }

        if (support.GuardLock(state) is { } locked)
        {
            return locked;
        }

        if (state.AwaitingInfo)
        {
            // Already waiting: the earlier request stands.
            return new ReferralRequestInformationResponse { Referral = support.ToDto(state) };
        }

        state.AwaitingInfo = true;
        state.InfoComment = request.Comment.Trim();
        state.SlaPausedAt = support.Now;
        if (await support.SaveAsync(state, cancellationToken).ConfigureAwait(false) is { } saveError)
        {
            return saveError;
        }

        await work.RaiseAsync(UwWorkTasks.InformationPattern, jobId, $"info:{jobId:D}:{state.RecordVersion}", null, cancellationToken).ConfigureAwait(false);
        return new ReferralRequestInformationResponse { Referral = support.ToDto(state) };
    }
}

internal sealed class LockReviewHandler(ReferralSupport support, UnderwritingStore store) : ICommandHandler<LockReview, ReviewLockResponse>
{
    public async Task<Result<ReviewLockResponse>> HandleAsync(LockReview command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var jobId = request.JobRef.Value;
        if (await support.BeginAsync(jobId, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var (state, error) = await support.EnsureAsync(jobId, "Review started", cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (!state!.IsActive)
        {
            return ReferralSupport.NotReferrable(jobId, "the referral was withdrawn.");
        }

        if (ReferralSupport.Stale(state, request.ExpectedRecordVersion) is { } stale)
        {
            return stale;
        }

        // Someone who worked on the job can never decide it (SOD-UW-02), so they may not hold the review either.
        var participation = (await store.ParticipationAsync(support.LegalEntity, [jobId], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(jobId, JobParticipation.None);
        if (support.Eligibility.SodReasons(participation).Count > 0)
        {
            return DomainError.Of(ModuleCode.UW, "SOD", "You created, edited, quoted or bound this job, so you cannot review it for decision (SOD-UW-02).");
        }

        if (support.GuardLock(state) is { } locked)
        {
            return locked;
        }

        var now = support.Now;
        if (state.LiveLockOwner(now) is null)
        {
            state.LockOwner = support.Me;
            state.LockAcquiredAt = now;
        }

        // Taking the lock again renews the lease.
        state.LockExpiresAt = now + support.Lease;
        if (await support.SaveAsync(state, cancellationToken).ConfigureAwait(false) is { } saveError)
        {
            return saveError;
        }

        return new ReviewLockResponse { Referral = support.ToDto(state) };
    }
}

internal sealed class ReleaseReviewHandler(ReferralSupport support, ReferralAuditNotes notes) : ICommandHandler<ReleaseReview, ReviewReleaseResponse>
{
    public async Task<Result<ReviewReleaseResponse>> HandleAsync(ReleaseReview command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var jobId = request.JobRef.Value;
        if (await support.BeginAsync(jobId, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var state = await support.States.GetAsync(support.LegalEntity, jobId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return ReferralSupport.NotReferrable(jobId, "it has no referral state.");
        }

        if (ReferralSupport.Stale(state, request.ExpectedRecordVersion) is { } stale)
        {
            return stale;
        }

        var holder = state.LiveLockOwner(support.Now);
        if (holder is null)
        {
            // Nobody holds it (never taken, released, or the lease ran out): a stale row is cleaned, otherwise nothing to do.
            if (state.LockOwner is not null)
            {
                state.ClearLock();
                if (await support.SaveAsync(state, cancellationToken).ConfigureAwait(false) is { } cleanError)
                {
                    return cleanError;
                }
            }

            return new ReviewReleaseResponse { Referral = support.ToDto(state) };
        }

        if (!string.Equals(holder, support.Me, StringComparison.Ordinal))
        {
            if (request.BreakLock != true)
            {
                return ReferralSupport.LockedBy(state);
            }

            if (!support.IsManager)
            {
                return DomainError.Of(ModuleCode.UW, "AUTHORITY-DENIED", "Only an underwriting manager may break another underwriter's review lock.");
            }

            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                return DomainError.Of(ModuleCode.UW, "REASON-REQUIRED", "A reason is required to break a review lock.");
            }

            notes.BrokenLockOwner = holder;
        }

        state.ClearLock();
        if (await support.SaveAsync(state, cancellationToken).ConfigureAwait(false) is { } error)
        {
            return error;
        }

        return new ReviewReleaseResponse { Referral = support.ToDto(state) };
    }
}

/// <summary>Facts a handler passes to its auditor within one request (who lost the lock when a manager broke it).</summary>
internal sealed class ReferralAuditNotes
{
    public string? BrokenLockOwner { get; set; }
}

/// <summary>Audit facts of the referral commands: the referral (job) and what the caller said; a broken lock names the previous owner and the reason.</summary>
internal sealed class ReferralAuditor<TCommand, TResult>(ReferralAuditNotes notes) : ICommandAuditor<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public CommandAuditFacts Describe(TCommand command, Result<TResult>? result)
    {
        var (job, comment, reason, breakLock) = command switch
        {
            RequestReferral c => (c.Request.JobRef.Value, c.Request.Comment, (string?)null, false),
            WithdrawReferral c => (c.Request.JobRef.Value, c.Request.Comment, null, false),
            ReferReferralUp c => (c.Request.JobRef.Value, c.Request.Comment, c.Request.TargetRole, false),
            RequestReferralInformation c => (c.Request.JobRef.Value, c.Request.Comment, null, false),
            LockReview c => (c.Request.JobRef.Value, null, null, false),
            ReleaseReview c => (c.Request.JobRef.Value, null, c.Request.Reason, c.Request.BreakLock == true),
            _ => (Guid.Empty, null, null, false),
        };
        var state = result is { IsSuccess: true } ok ? StateOf(ok.Value) : null;
        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.UW, "Referral", job.ToString("D")),
            BusinessKeys = BusinessKeys.Empty.With("jobId", job.ToString("D")),
            Changes = state is null
                ? []
                : Platform.Audit.AuditDiff.Compute(
                    new { },
                    new
                    {
                        operation = typeof(TCommand).Name,
                        comment,
                        reason,
                        lifecycle = state.Lifecycle.ToString(),
                        awaitingInformation = state.AwaitingInformation,
                        requiredRole = state.RequiredRole,
                        lockOwner = state.Lock?.Owner,
                        brokenLockOwner = breakLock ? notes.BrokenLockOwner : null,
                        recordVersion = state.RecordVersion,
                    }),
        };
    }

    private static ReferralState? StateOf(TResult value) => value switch
    {
        ReferralRequestResponse r => r.Referral,
        ReferralWithdrawResponse r => r.Referral,
        ReferralReferUpResponse r => r.Referral,
        ReferralRequestInformationResponse r => r.Referral,
        ReviewLockResponse r => r.Referral,
        ReviewReleaseResponse r => r.Referral,
        _ => null,
    };
}
