using System.Globalization;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.Queries;

/// <summary>Read side of the manual journals (<c>fin.ManualJournal.list</c> and <c>.get</c>), scoped to the caller's legal entity.</summary>
internal sealed class ManualJournalReader(FinanceDbContext db)
{
    public async Task<ManualJournalEnvelope?> GetAsync(LegalEntityId legalEntity, Guid id, CancellationToken cancellationToken)
    {
        var row = await db.ManualJournals.AsNoTracking().SingleOrDefaultAsync(r => r.ManualJournalId == id && r.LegalEntityId == legalEntity.Value, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var lines = await db.ManualJournalLines.AsNoTracking().Where(l => l.ManualJournalId == id).OrderBy(l => l.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ToEnvelope(row, lines);
    }

    /// <summary>Newest first; the cursor is the number sequence of the last item of the previous page.</summary>
    public async Task<OpsPage<ManualJournalEnvelope>> ListAsync(LegalEntityId legalEntity, string? status, string? cursor, int limit, CancellationToken cancellationToken)
    {
        long? before = null;
        if (cursor is not null)
        {
            before = long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new FormatException("Invalid cursor.");
        }

        var query = db.ManualJournals.AsNoTracking().Where(r => r.LegalEntityId == legalEntity.Value);
        if (status is not null)
        {
            query = query.Where(r => r.Status == status);
        }

        if (before is { } b)
        {
            query = query.Where(r => r.NumberSeq < b);
        }

        var rows = await query.OrderByDescending(r => r.NumberSeq).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(limit).ToList();
        var ids = page.Select(r => r.ManualJournalId).ToList();
        var lines = (await db.ManualJournalLines.AsNoTracking().Where(l => ids.Contains(l.ManualJournalId)).OrderBy(l => l.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(l => l.ManualJournalId);
        var items = page.Select(r => ToEnvelope(r, lines[r.ManualJournalId].ToList())).ToList();
        return new OpsPage<ManualJournalEnvelope>(items, rows.Count > limit ? page[^1].NumberSeq.ToString(CultureInfo.InvariantCulture) : null, limit);
    }

    public static ManualJournalEnvelope ToEnvelope(ManualJournalRow row, IReadOnlyList<ManualJournalLineRow> lines) =>
        new(
            new ManualJournalView(
                row.ManualJournalId, row.Number, row.Status, row.Description, row.Book, row.AccountingDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), row.Currency.Trim(),
                [.. lines.OrderBy(l => l.LineNo).Select(l => new ManualJournalLineView(l.AccountCode, l.Side, ManualJournals.Money(l.Amount)))],
                row.EvidenceRef, row.CreatedBy, row.SubmittedBy, row.SubmittedAt?.ToString(), row.DecidedBy, row.DecisionReason),
            row.PostedJournalIds);
}
