using CoreIns.SharedKernel.Identifiers;
using System.Text.Json;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Modules.Finance.Posting;
using CoreIns.Modules.Finance.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.Commands;

/// <summary><c>fin.Journal.reverse</c>: reverse one posted journal; the reason is mandatory, the date optional.</summary>
internal sealed record ReverseJournal(Guid JournalId, string Reason, BusinessDate? Date) : ICommand<JournalReverseResponse>;

/// <summary>
/// <c>fin.Journal.reverse</c> (REQ-FIN-002, -072, -073): the correction path for journals FIN owns (manual journals and FIN runs). A journal sourced from
/// another module's events is corrected by that module (FIN-ERR-SOURCE-OWNED). The reversal is dated on the requested date (default: the original accounting
/// date) and, when that month is soft-closed or locked, on the first day of the next OPEN month: a closed period is never written to.
/// </summary>
internal sealed class ReverseJournalHandler(
    FinanceDbContext db, JournalReversal reversal, FinancePeriods periods, JournalReader reader, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities)
    : ICommandHandler<ReverseJournal, JournalReverseResponse>
{
    public async Task<Result<JournalReverseResponse>> HandleAsync(ReverseJournal command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > 500)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "A reason of 1..500 characters is required.");
        }

        var scope = ManualJournalScope.Of(context, legalEntities);
        var original = scope is { } s
            ? await db.Journals.AsNoTracking().SingleOrDefaultAsync(j => j.JournalId == command.JournalId && j.LegalEntityId == s.Id.Value, cancellationToken).ConfigureAwait(false)
            : null;
        if (scope is not { } entity || original is null)
        {
            return DomainError.Of(ModuleCode.FIN, "NOT-FOUND", "The journal does not exist.");
        }

        // Only FIN's own journals are corrected here. Reversals and earning-run style postings carry module FIN; every other source module owns its journals.
        if (!string.Equals(original.SourceModule, ManualJournalPosting.SourceModule, StringComparison.Ordinal))
        {
            return DomainError.Of(ModuleCode.FIN, "SOURCE-OWNED", $"Journal {original.JournalNumber} was posted from {original.SourceModule} events; {original.SourceModule} corrects it.");
        }

        // Earning-run journals (module FIN, event type EarningRun) belong to the earning engine: reversing one would desync the run and its earned_premium rows. A later run corrects them.
        if (string.Equals(original.SourceEventType, Earning.SourceEventType, StringComparison.Ordinal))
        {
            return DomainError.Of(ModuleCode.FIN, "SOURCE-OWNED", $"Journal {original.JournalNumber} was posted by an earning run; the earning engine corrects it, it cannot be reversed.");
        }

        var wanted = command.Date ?? original.AccountingDate;
        var date = await periods.FirstPostableAsync(entity.Id.Value, wanted, cancellationToken).ConfigureAwait(false);
        if (date is null)
        {
            return DomainError.Of(ModuleCode.FIN, "PERIOD-LOCKED", $"No open accounting period on or after {FinancePeriods.CodeOf(wanted)}.");
        }

        var written = await reversal.ReverseAsync(entity.Id, command.JournalId, date.Value, command.Reason.Trim(), cancellationToken).ConfigureAwait(false);
        if (written.IsFailure)
        {
            return written.Error;
        }

        var view = await reader.GetAsync(entity.Id, written.Value.JournalId.ToString("D"), clock.Now, cancellationToken).ConfigureAwait(false);
        return new JournalReverseResponse { ReversalJournal = JsonSerializer.SerializeToElement(view) };
    }
}
