using System.Text.Json;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Modules.Party.Services.Screening;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Party.Queries;

/// <summary>Reads of the screening case queue (BO-PTY-07): <c>pty.Screening.caseGet</c>.</summary>
internal sealed class ScreeningCaseQueries(PartyDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(IReadOnlyList<ScreeningCaseView> Items, bool HasMore)> ListAsync(
        LegalEntityId legalEntity, string? status, Guid? partyId, Guid? caseId, int offset, int limit, CancellationToken cancellationToken)
    {
        var query = db.ScreeningCases.AsNoTracking().Where(c => c.LegalEntityId == legalEntity);
        if (status is not null)
        {
            query = query.Where(c => c.Status == status);
        }

        if (partyId is { } party)
        {
            var id = new PartyId(party);
            query = query.Where(c => c.PartyId == id);
        }

        if (caseId is { } one)
        {
            query = query.Where(c => c.CaseId == one);
        }

        var rows = await query.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.CaseId).Skip(offset).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ([.. rows.Take(limit).Select(View)], rows.Count > limit);
    }

    public static ScreeningCaseView View(ScreeningCaseRow row) => new()
    {
        CaseId = row.CaseId,
        CaseNumber = row.CaseNumber,
        Status = row.Status switch
        {
            ScreeningStatus.Open => ScreeningCaseView.StatusValue.Open,
            ScreeningStatus.PendingApproval => ScreeningCaseView.StatusValue.PendingApproval,
            ScreeningStatus.Cleared => ScreeningCaseView.StatusValue.Cleared,
            _ => ScreeningCaseView.StatusValue.TrueMatch,
        },
        PartyId = row.PartyId,
        SubjectName = row.SubjectName,
        Country = row.Country,
        ListVersions = row.ListVersions.Length == 0 ? [] : row.ListVersions.Split(','),
        Evidence = JsonSerializer.Deserialize<List<ScreeningCaseView.EvidenceItem>>(row.Evidence, Json) ?? [],
        CallerRef = new ObjectRef(Enum.Parse<ModuleCode>(row.CallerModule), row.CallerType, row.CallerId),
        CreatedAt = row.CreatedAt,
        ProposedDecision = row.ProposedDecision switch
        {
            "CLEAR" => ScreeningCaseView.ProposedDecisionValue.Clear,
            "TRUE_MATCH" => ScreeningCaseView.ProposedDecisionValue.TrueMatch,
            _ => null,
        },
        ProposedBy = row.ProposedBy,
        ProposedAt = row.ProposedAt,
        ProposalComment = row.ProposalComment,
        ApprovalRequestId = row.ApprovalRequestId is { } request ? new ApprovalRequestId(request) : null,
        DecidedBy = row.DecidedBy,
        DecidedAt = row.DecidedAt,
        DecisionComment = row.DecisionComment,
    };
}
