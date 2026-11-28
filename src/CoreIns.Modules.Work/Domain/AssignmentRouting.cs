using System.Security.Cryptography;
using System.Text;
using CoreIns.Modules.Work.Persistence;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Work.Domain;

/// <summary>A staff member as the assignment rules see them.</summary>
internal sealed record WorkUser(string UserId, string DisplayName, IReadOnlySet<string> Roles, IReadOnlySet<string> Skills, bool Active);

/// <summary>
/// The user directory port of the Work core (BO-WRK-02). Today it is served from <c>wrk.assignee_profile</c>.
/// TODO(hardening): switch to the platform user directory when BO-PLT-05 lands (user_profile + role_assignment, token roles stay authoritative until then).
/// </summary>
internal interface IWorkUserDirectory
{
    Task<WorkUser?> FindAsync(string userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkUser>> AllAsync(CancellationToken cancellationToken);
}

internal sealed class LocalWorkUserDirectory(WorkDbContext db, RequestContext context, ILegalEntityDirectory legalEntities) : IWorkUserDirectory
{
    public async Task<WorkUser?> FindAsync(string userId, CancellationToken cancellationToken)
    {
        var legalEntity = Current();
        var row = await db.AssigneeProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.LegalEntityId == legalEntity && p.UserId == userId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToUser(row);
    }

    public async Task<IReadOnlyList<WorkUser>> AllAsync(CancellationToken cancellationToken)
    {
        var legalEntity = Current();
        var rows = await db.AssigneeProfiles.AsNoTracking().Where(p => p.LegalEntityId == legalEntity).OrderBy(p => p.UserId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(ToUser)];
    }

    private LegalEntityId Current() =>
        legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    private static WorkUser ToUser(AssigneeProfileRow row) => new(row.UserId, row.DisplayName, Split(row.Roles), Split(row.Skills), row.Active);

    private static HashSet<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
}

internal sealed record RejectedCandidate(string UserId, string Reason);

internal sealed record RuleStep(string Rule, string Outcome);

/// <summary>The routing answer: a user (with the rule that chose them) or the queue, plus how it was reached.</summary>
internal sealed record RoutingDecision(string? UserId, string QueueCode, string? RuleCode, IReadOnlyList<RuleStep> RulePath, IReadOnlyList<RejectedCandidate> Rejected);

/// <summary>
/// Rule based assignment (REQ-WRK-131): the queue's active rules run in priority order; the first rule with an eligible candidate wins and
/// picks the candidate with the fewest open activities (ties: lowest user id, so the choice is deterministic). No rule or no candidate: the queue.
/// </summary>
internal sealed class AssignmentRouter(WorkDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IWorkUserDirectory directory)
{
    public async Task<RoutingDecision> RouteAsync(string patternCode, string queueCode, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var queue = WorkCatalog.Queue(queueCode);
        var rules = await db.AssignmentRules.AsNoTracking()
            .Where(r => r.LegalEntityId == legalEntity && r.Active && r.QueueCode == queueCode && (r.PatternCode == null || r.PatternCode == patternCode))
            .OrderBy(r => r.Priority).ThenBy(r => r.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        var path = new List<RuleStep>();
        var rejected = new List<RejectedCandidate>();
        if (queue is null || rules.Count == 0)
        {
            path.Add(new RuleStep("-", rules.Count == 0 ? "NO_RULE" : "QUEUE_UNKNOWN"));
            return new RoutingDecision(null, queueCode, null, path, rejected);
        }

        var users = await directory.AllAsync(cancellationToken).ConfigureAwait(false);
        var open = await db.Activities.AsNoTracking()
            .Where(a => a.LegalEntityId == legalEntity && a.State == ActivityRow.Open && a.AssigneeType == ActivityRow.User)
            .GroupBy(a => a.AssigneeId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        foreach (var rule in rules)
        {
            var eligible = new List<WorkUser>();
            foreach (var user in users)
            {
                var reason = Reject(user, queue, rule);
                if (reason is null)
                {
                    eligible.Add(user);
                }
                else if (!rejected.Any(r => r.UserId == user.UserId))
                {
                    rejected.Add(new RejectedCandidate(user.UserId, $"{rule.Code}:{reason}"));
                }
            }

            if (eligible.Count == 0)
            {
                path.Add(new RuleStep(rule.Code, "NO_CANDIDATE"));
                continue;
            }

            var chosen = eligible.OrderBy(u => open.GetValueOrDefault(u.UserId)).ThenBy(u => u.UserId, StringComparer.Ordinal).First();
            path.Add(new RuleStep(rule.Code, $"MATCHED:{chosen.UserId}:open={open.GetValueOrDefault(chosen.UserId)}"));
            rejected.RemoveAll(r => r.UserId == chosen.UserId);
            return new RoutingDecision(chosen.UserId, queueCode, rule.Code, path, rejected);
        }

        return new RoutingDecision(null, queueCode, null, path, rejected);
    }

    /// <summary>Why a user may not take work from the queue, or null. Also the check for an explicit assignment.</summary>
    internal static string? Reject(WorkUser user, WorkQueue queue, AssignmentRuleRow? rule)
    {
        if (!user.Active)
        {
            return "INACTIVE";
        }

        if (!user.Roles.Overlaps(queue.Roles))
        {
            return "CANNOT_SEE_QUEUE";
        }

        if (rule?.RequiredRole is { Length: > 0 } role && !user.Roles.Contains(role))
        {
            return "MISSING_ROLE";
        }

        if (rule?.RequiredSkill is { Length: > 0 } skill && !user.Skills.Contains(skill))
        {
            return "MISSING_SKILL";
        }

        return null;
    }
}

/// <summary>User ids on the wire: contracts type a user as a UUID, assignee ids are directory strings, so the id is derived from the string.</summary>
internal static class WorkUserIds
{
    public static UserId For(string userId) => new(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("coreins.wrk.user:" + userId))[..16]));
}
