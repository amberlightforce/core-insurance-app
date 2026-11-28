using System.Globalization;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.Queries;

/// <summary>Read side of the earning runs (<c>fin.Earning.status</c> and <c>fin.Earning.result</c>, BO-FIN-06b), scoped to the caller's legal entity. Reads only.</summary>
internal sealed class EarningReader(FinanceDbContext db)
{
    /// <summary>The latest completed run of the legal entity, or null before the first one.</summary>
    public async Task<EarningRunView?> LatestAsync(Guid legalEntity, CancellationToken cancellationToken)
    {
        var row = await db.EarningRuns.AsNoTracking().Where(r => r.LegalEntityId == legalEntity).OrderByDescending(r => r.FinishedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToView(row);
    }

    /// <summary>
    /// One run (the latest when <paramref name="runId"/> is null) with a page of all terms it covered, ordered by policy number, term and book. The cursor is the
    /// offset of the next item: the rows of a run never change, so it is stable. Null when the run does not exist for this legal entity.
    /// </summary>
    public async Task<EarningResultResponse?> ResultAsync(Guid legalEntity, Guid? runId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var offset = 0;
        if (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset)))
        {
            throw new FormatException("Invalid cursor.");
        }

        var runs = db.EarningRuns.AsNoTracking().Where(r => r.LegalEntityId == legalEntity);
        var run = runId is { } id
            ? await runs.SingleOrDefaultAsync(r => r.RunId == id, cancellationToken).ConfigureAwait(false)
            : await runs.OrderByDescending(r => r.FinishedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return null;
        }

        var rows = await db.EarnedPremiums.AsNoTracking().Where(r => r.RunId == run.RunId && r.LegalEntityId == legalEntity)
            .OrderBy(r => r.PolicyNumber).ThenBy(r => r.PolicyTermId).ThenBy(r => r.Book).ThenBy(r => r.Currency)
            .Skip(offset).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new EarningResultResponse
        {
            Run = ToView(run),
            Items = [.. rows.Take(limit).Select(ToResult)],
            NextCursor = rows.Count > limit ? (offset + limit).ToString(CultureInfo.InvariantCulture) : null,
            Limit = limit,
        };
    }

    private static EarningRunView ToView(EarningRunRow row) => new()
    {
        Id = row.RunId,
        Period = row.PeriodCode,
        AsOf = row.AsOf,
        State = EarningRunView.StateValue.Done,
        Simulated = false,
        StartedAt = row.StartedAt,
        FinishedAt = row.FinishedAt,
        PoliciesProcessed = row.PoliciesProcessed,
        TermsSkipped = row.TermsSkipped,
        EarnedToDate = [Money.Of(row.EarnedToDate, row.Currency)],
        PostedDelta = [Money.Of(row.PostedDelta, row.Currency)],
        JournalIds = [.. row.JournalIds.Select(j => new JournalId(j))],
    };

    private static EarningTermResult ToResult(EarnedPremiumRow r) => new()
    {
        PolicyTermId = new PolicyTermId(r.PolicyTermId),
        PolicyNumber = r.PolicyNumber,
        Book = r.Book,
        TermStart = r.TermStart,
        TermEnd = r.TermEnd,
        WrittenPremium = Money.Of(r.WrittenNet, r.Currency),
        EarnedToDate = Money.Of(r.EarnedToDate, r.Currency),
        PreviouslyEarned = Money.Of(r.EarnedToDate - r.Delta, r.Currency),
        Delta = Money.Of(r.Delta, r.Currency),
        DaysEarned = r.DaysEarned,
        TermDays = r.TermDays,
    };
}
