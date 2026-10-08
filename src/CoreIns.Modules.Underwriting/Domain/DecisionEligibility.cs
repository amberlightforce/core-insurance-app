using CoreIns.Modules.Underwriting.Authority;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Time;

namespace CoreIns.Modules.Underwriting.Domain;

/// <summary>An evaluation row as read for the separation-of-duties check.</summary>
internal sealed class ParticipationRow
{
    public Guid JobId { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public string[] JobParticipants { get; set; } = [];

    public string? ProducerCode { get; set; }
}

/// <summary>
/// Everyone UW knows worked on one job (SOD-UW-02, BR-UW-013), from its evaluations: <see cref="Evaluators"/> ran an evaluation
/// (quoted or bound), <see cref="Participants"/> are the actors POL reported (creator, editors, quoters), <see cref="Creator"/> is the
/// first of them (POL lists the job creator first) and <see cref="ProducerCodes"/> the producer codes reported with the job.
/// </summary>
internal sealed record JobParticipation(string? Creator, IReadOnlySet<string> Participants, IReadOnlySet<string> Evaluators, IReadOnlySet<string> ProducerCodes)
{
    /// <summary>No one is known: the job has no evaluation in this legal entity.</summary>
    public static readonly JobParticipation None = new(null, new HashSet<string>(), new HashSet<string>(), new HashSet<string>());

    /// <summary>The participation of one job from its evaluation rows, oldest first.</summary>
    public static JobParticipation From(IEnumerable<ParticipationRow> rows)
    {
        var list = rows.ToList();
        return new JobParticipation(
            list.Select(r => r.JobParticipants.FirstOrDefault()).FirstOrDefault(p => !string.IsNullOrEmpty(p)),
            list.SelectMany(r => r.JobParticipants).ToHashSet(StringComparer.Ordinal),
            list.Select(r => r.CreatedBy).ToHashSet(StringComparer.Ordinal),
            list.Select(r => r.ProducerCode).OfType<string>().ToHashSet(StringComparer.Ordinal));
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
internal sealed class DecisionEligibility(RequestContext context, IAuthorityService authority, IClock clock)
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
    public IssueEligibility Evaluate(IssueRecord issue, JobParticipation job, AuthorityCheckResult check)
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

        if (issue.Status == IssueStatus.Open && issue.Fingerprint is null)
        {
            reasons.Add(DecidabilityReason.NeedsReevaluation);
        }

        return new IssueEligibility(reasons, check);
    }

    /// <summary>The dry authority checks of the given issue types, one per type (the dimension is the issue type only).</summary>
    public async Task<IReadOnlyDictionary<string, AuthorityCheckResult>> DryChecksAsync(
        IEnumerable<IssueRecord> issues, CancellationToken cancellationToken)
    {
        var checks = new Dictionary<string, AuthorityCheckResult>(StringComparer.Ordinal);
        foreach (var issue in issues)
        {
            if (!checks.ContainsKey(issue.IssueType))
            {
                checks[issue.IssueType] = await CheckAuthorityAsync(issue.IssueType, ObjectRef.For(ModuleCode.UW, "Issue", new SharedKernel.Identifiers.UwIssueId(issue.IssueId)), cancellationToken).ConfigureAwait(false);
            }
        }

        return checks;
    }
}
