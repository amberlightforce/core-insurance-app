using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Underwriting.Authority;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;

namespace CoreIns.Modules.Underwriting.Domain;

/// <summary>An evaluation row as read for the separation-of-duties check.</summary>
internal sealed class ParticipationRow
{
    public Guid JobId { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public string[] JobParticipants { get; set; } = [];

    public string? ProducerCode { get; set; }

    public string? SnapshotRef { get; set; }
}

/// <summary>
/// Everyone UW knows worked on one job (SOD-UW-02, BR-UW-013), from its evaluations: <see cref="Evaluators"/> ran an evaluation
/// (quoted or bound), <see cref="Participants"/> are the actors POL reported (creator, editors, quoters), <see cref="Creator"/> is the
/// first of them (POL lists the job creator first) and <see cref="ProducerCodes"/> the producer codes reported with the job.
/// </summary>
internal sealed record JobParticipation(string? Creator, IReadOnlySet<string> Participants, IReadOnlySet<string> Evaluators, IReadOnlySet<string> ProducerCodes, string? SnapshotRef = null)
{
    /// <summary>No one is known: the job has no evaluation in this legal entity.</summary>
    public static readonly JobParticipation None = new(null, new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), null);

    /// <summary>The participation of one job from its evaluation rows, oldest first.</summary>
    public static JobParticipation From(IEnumerable<ParticipationRow> rows)
    {
        var list = rows.ToList();
        return new JobParticipation(
            list.Select(r => r.JobParticipants.FirstOrDefault()).FirstOrDefault(p => !string.IsNullOrEmpty(p)),
            list.SelectMany(r => r.JobParticipants).ToHashSet(StringComparer.Ordinal),
            list.Select(r => r.CreatedBy).ToHashSet(StringComparer.Ordinal),
            list.Select(r => r.ProducerCode).OfType<string>().ToHashSet(StringComparer.Ordinal),
            // The snapshot of the latest evaluation POL ran for the job (only those report participants).
            list.LastOrDefault(r => r.JobParticipants.Length > 0)?.SnapshotRef);
    }
}

/// <summary>The decide check for one issue and one caller: every reason that bars it, and the authority outcome.</summary>
internal sealed record IssueEligibility(IReadOnlyList<DecidabilityReason> Reasons, AuthorityCheckResult Authority)
{
    public bool CanDecide => Reasons.Count == 0;
}

/// <summary>
/// The one place that answers "may the caller decide this issue" (REQ-UW-115/116, SOD-UW-02, BR-UW-013, REQ-UW-109; D-UW-01,
/// D-SL5-04). <c>uw.Issue.decide</c> uses <see cref="SodReasons"/> and <see cref="CheckAuthorityAsync"/> and records the check it
/// gets; the referral workbench preview uses <see cref="Evaluate"/>, which is the same two functions run dry: the authority service
/// persists nothing itself (the command pipeline records the checks a handler adds to the request context), so a preview never
/// adds the check to the context and writes no check, approval request or audit record. The preview never authorises: the decide
/// command re-runs the checks at commit.
/// </summary>
internal sealed class DecisionEligibility(RequestContext context, IAuthorityService authority, IClock clock, IPolicyJobService jobs)
{
    /// <summary>True when the caller is a person (REQ-UW-115, BR-UW-014); an AI agent or a service never decides.</summary>
    public bool CallerIsHuman => context.Actor.Kind == ActorKind.User;

    /// <summary>Why the caller may not decide this job's issues, as SoD reasons; empty when the caller did not work on the job.</summary>
    public IReadOnlyList<DecidabilityReason> SodReasons(JobParticipation job)
    {
        var me = context.Actor.ToString();
        var reasons = new List<DecidabilityReason>();
        var creator = string.Equals(job.Creator, me, StringComparison.Ordinal);
        var evaluator = job.Evaluators.Contains(me);
        if (creator)
        {
            reasons.Add(DecidabilityReason.SodCreator);
        }

        if (evaluator)
        {
            reasons.Add(DecidabilityReason.SodEvaluator);
        }

        // SOD_PRODUCER is inert today: UW only holds the producer CODE, and no actor id equals a producer code until PTY maps users
        // to producer codes (D-SL5 open point). Pinned by a test; the producer user is covered as creator/participant meanwhile.
        if (job.ProducerCodes.Contains(me))
        {
            reasons.Add(DecidabilityReason.SodProducer);
        }

        if (!creator && !evaluator && job.Participants.Contains(me))
        {
            reasons.Add(DecidabilityReason.SodParticipant);
        }

        return reasons;
    }

    /// <summary>
    /// The <c>UW.ISSUE_APPROVAL</c> check for an issue type (REQ-UW-109). The result is not recorded here: <c>uw.Issue.decide</c>
    /// adds it to the request context (a binding check), a preview does not (a dry check).
    /// </summary>
    public Task<AuthorityCheckResult> CheckAuthorityAsync(string issueType, ObjectRef? subject, CancellationToken cancellationToken) =>
        authority.CheckAsync(
            new AuthorityCheckRequest(
                context.Actor, context.Roles, UnderwritingAuthorityTypes.IssueApproval,
                new Dictionary<string, DimensionValue>(StringComparer.Ordinal) { [UnderwritingAuthorityTypes.IssueTypeDimension] = DimensionValue.OfCodes(issueType) },
                subject, clock.Now),
            cancellationToken);

    /// <summary>Whether the caller can decide <paramref name="issue"/> now, with every reason that says no. Dry: records nothing.</summary>
    public IssueEligibility Evaluate(IssueRecord issue, JobParticipation job, AuthorityCheckResult check, bool jobChanged)
    {
        var reasons = new List<DecidabilityReason>();
        if (issue.Status != IssueStatus.Open)
        {
            reasons.Add(DecidabilityReason.NotOpen);
        }

        if (!CallerIsHuman)
        {
            reasons.Add(DecidabilityReason.NotHuman);
        }

        reasons.AddRange(SodReasons(job));
        if (check.Decision != AuthorityDecision.Allow)
        {
            reasons.Add(DecidabilityReason.NoAuthority);
        }

        if (issue.Status == IssueStatus.Open && (issue.Fingerprint is null || jobChanged))
        {
            reasons.Add(DecidabilityReason.NeedsReevaluation);
        }

        return new IssueEligibility(reasons, check);
    }

    /// <summary>The dry authority check of every given issue, keyed by issue id (the same per-issue check as decide).</summary>
    public async Task<IReadOnlyDictionary<Guid, AuthorityCheckResult>> DryChecksAsync(
        IEnumerable<IssueRecord> issues, CancellationToken cancellationToken)
    {
        var checks = new Dictionary<Guid, AuthorityCheckResult>();
        foreach (var issue in issues)
        {
            checks[issue.IssueId] = await CheckAuthorityAsync(issue.IssueType, ObjectRef.For(ModuleCode.UW, "Issue", new SharedKernel.Identifiers.UwIssueId(issue.IssueId)), cancellationToken).ConfigureAwait(false);
        }

        return checks;
    }

    /// <summary>True when every given result says yes (a referral is decidable only when each of its Open issues is).</summary>
    public static bool AllDecidable(IReadOnlyCollection<IssueEligibility> open) => open.Count > 0 && open.All(r => r.CanDecide);

    /// <summary>The job through POL's in-process contract in the caller's legal entity, or null when POL does not know it.</summary>
    public async Task<JobView?> JobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            return (await jobs.GetAsync(jobId.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false)).Job;
        }
        catch (DomainException ex) when (ex.Error.Code.Name == "NOT-FOUND")
        {
            return null;
        }
    }

    /// <summary>
    /// True when the job changed since UW's latest POL evaluation of it (SOD-UW-02): every POL edit moves the draft version of the
    /// current quote version, so someone who edited after the evaluation is not yet among the recorded participants and the job must
    /// be evaluated again (which records them) before anyone decides. A job POL does not know (evaluated over REST only) has no editor.
    /// </summary>
    public static bool JobChanged(JobView? job, JobParticipation participation)
    {
        if (job is null)
        {
            return false;
        }

        var version = job.Versions.FirstOrDefault(v => v.VersionNo == job.CurrentVersionNo);
        return version is null || participation.SnapshotRef is null
            || !string.Equals(participation.SnapshotRef, $"pol:quote:{version.QuoteId.Value:D}:{version.DraftVersion}", StringComparison.Ordinal);
    }
}
