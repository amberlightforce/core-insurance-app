using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Party.Queries;

/// <summary>Status codes stored in <c>pty.party_merge</c>.</summary>
internal static class MergeStatus
{
    public const string Pending = "PENDING_APPROVAL";
    public const string Executed = "EXECUTED";
    public const string Rejected = "REJECTED";
    public const string Unmerged = "UNMERGED";
}

/// <summary>Reads of merge records: views, the list and the survivor of a party (BO-PTY-05).</summary>
internal sealed class PartyMergeQueries(PartyDbContext db)
{
    /// <summary>The longest merge chain followed by <see cref="ResolveSurvivorAsync"/> (a guard against corrupt data, chains are 1 or 2 long).</summary>
    private const int MaxChain = 20;

    public async Task<IReadOnlyList<PartyMergeView>> ViewsAsync(IReadOnlyList<PartyMergeRow> rows, CancellationToken cancellationToken)
    {
        var ids = rows.SelectMany(r => new[] { r.SurvivorPartyId, r.MergedPartyId }).Distinct().ToList();
        var numbers = await db.Parties.AsNoTracking().Where(p => ids.Contains(p.PartyId)).Select(p => new { p.PartyId, p.PartyNumber }).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byId = numbers.ToDictionary(n => n.PartyId, n => n.PartyNumber.Value);
        return [.. rows.Select(r => ToView(r, byId.GetValueOrDefault(r.SurvivorPartyId, string.Empty), byId.GetValueOrDefault(r.MergedPartyId, string.Empty)))];
    }

    public async Task<PartyMergeView> ViewAsync(PartyMergeRow row, CancellationToken cancellationToken) =>
        (await ViewsAsync([row], cancellationToken).ConfigureAwait(false))[0];

    public async Task<(IReadOnlyList<PartyMergeView> Items, bool HasMore)> ListAsync(
        LegalEntityId legalEntity, string? status, Guid? partyId, Guid? mergeId, int offset, int limit, CancellationToken cancellationToken)
    {
        var query = db.PartyMerges.AsNoTracking().Where(m => m.LegalEntityId == legalEntity);
        if (status is not null)
        {
            query = query.Where(m => m.Status == status);
        }

        if (partyId is { } party)
        {
            var id = new PartyId(party);
            query = query.Where(m => m.SurvivorPartyId == id || m.MergedPartyId == id);
        }

        if (mergeId is { } merge)
        {
            query = query.Where(m => m.MergeId == merge);
        }

        var rows = await query.OrderByDescending(m => m.RequestedAt).ThenBy(m => m.MergeId).Skip(offset).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        return (await ViewsAsync([.. rows.Take(limit)], cancellationToken).ConfigureAwait(false), rows.Count > limit);
    }

    /// <summary>Follows EXECUTED merges from the party to the survivor; the party itself when it was never merged. Null when the party is unknown.</summary>
    public async Task<PartySurvivorResponse?> ResolveSurvivorAsync(LegalEntityId legalEntity, Guid partyId, CancellationToken cancellationToken)
    {
        var requested = new PartyId(partyId);
        if (!await db.Parties.AsNoTracking().AnyAsync(p => p.LegalEntityId == legalEntity && p.PartyId == requested, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var current = requested;
        Guid? last = null;
        for (var i = 0; i < MaxChain; i++)
        {
            var next = await db.PartyMerges.AsNoTracking()
                .Where(m => m.LegalEntityId == legalEntity && m.MergedPartyId == current && m.Status == MergeStatus.Executed)
                .Select(m => new { m.SurvivorPartyId, m.MergeId }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                break;
            }

            current = next.SurvivorPartyId;
            last = next.MergeId;
        }

        var number = await db.Parties.AsNoTracking().Where(p => p.PartyId == current).Select(p => p.PartyNumber).SingleAsync(cancellationToken).ConfigureAwait(false);
        return new PartySurvivorResponse
        {
            RequestedPartyId = partyId, SurvivorPartyId = current, SurvivorPartyNumber = number.Value, Merged = current != requested, MergeId = last,
        };
    }

    private static PartyMergeView ToView(PartyMergeRow r, string survivorNumber, string mergedNumber) => new()
    {
        MergeId = r.MergeId,
        SurvivorPartyId = r.SurvivorPartyId,
        MergedPartyId = r.MergedPartyId,
        SurvivorPartyNumber = survivorNumber,
        MergedPartyNumber = mergedNumber,
        Status = r.Status switch
        {
            MergeStatus.Pending => PartyMergeView.StatusValue.PendingApproval,
            MergeStatus.Executed => PartyMergeView.StatusValue.Executed,
            MergeStatus.Rejected => PartyMergeView.StatusValue.Rejected,
            _ => PartyMergeView.StatusValue.Unmerged,
        },
        Reason = r.Reason,
        RequestedBy = r.RequestedBy,
        RequestedAt = r.RequestedAt,
        ApprovalRequestId = r.ApprovalRequestId is { } a ? new ApprovalRequestId(a) : null,
        DecidedBy = r.DecidedBy,
        DecidedAt = r.DecidedAt,
        DecisionComment = r.DecisionComment,
        ExecutedAt = r.ExecutedAt,
        UnmergeWindowEnd = r.UnmergeWindowEnd,
        UnmergedAt = r.UnmergedAt,
        UnmergedBy = r.UnmergedBy,
        PriorStatus = r.PriorStatus,
    };
}
