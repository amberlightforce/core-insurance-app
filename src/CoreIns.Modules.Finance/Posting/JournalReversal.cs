using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.Posting;

/// <summary>
/// Corrections are reversals, never updates (REQ-FIN-002, -072, -073): a new journal with every line on the opposite
/// side, linked to the original; a journal is reversed at most once (checked here and by a unique index).
/// The slice exposes no API for it: journals sourced from other modules' events are corrected by the source module
/// (fin.Journal.reverse answers FIN-ERR-SOURCE-OWNED for them, W5-FIN-02).
/// </summary>
internal sealed class JournalReversal(FinanceDbContext db, JournalWriter writer, IEventPublisher events)
{
    public async Task<Result<WrittenJournal>> ReverseAsync(LegalEntityId legalEntity, Guid journalId, BusinessDate accountingDate, string reason, CancellationToken cancellationToken)
    {
        var loaded = await JournalRows.LoadAsync(db, legalEntity, journalId, cancellationToken).ConfigureAwait(false);
        if (loaded is not (var entry, var lines))
        {
            return DomainError.Of(ModuleCode.FIN, "NOT-FOUND", "The journal does not exist.");
        }

        if (entry.SourceType == SourceTypes.Reversal)
        {
            return DomainError.Of(ModuleCode.FIN, "ALREADY-REVERSED", $"Journal {entry.JournalNumber} is itself a reversal and cannot be reversed (REQ-FIN-073).");
        }

        if (await db.Journals.AnyAsync(j => j.ReversesJournalId == journalId, cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.FIN, "ALREADY-REVERSED", $"Journal {entry.JournalNumber} is already reversed.");
        }

        var draft = new JournalDraft(entry.Book, accountingDate, accountingDate, SourceTypes.Reversal, entry.RuleSetId, entry.RuleSetVersion, entry.RuleCodes, Journals.Reverse(lines));
        var source = new JournalSource(legalEntity, entry.LegalEntityCode, entry.Jurisdiction, entry.SourceModule, entry.SourceEventType, entry.SourceEventIds, entry.SourceRef);
        var written = await writer.WriteAsync(draft, source, Currency.FromCode(entry.FunctionalCurrency.Trim()), journalId, reason, cancellationToken).ConfigureAwait(false);
        events.Publish(written.Posted);
        return written;
    }
}
