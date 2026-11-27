using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Modules.Finance.Posting;
using CoreIns.Modules.Finance.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.Commands;

internal sealed record CreateManualJournal(ManualJournalCreateInput Request) : ICommand<ManualJournalEnvelope>;

internal sealed record WithdrawManualJournal(Guid Id) : ICommand<ManualJournalEnvelope>;

/// <summary>Resolves the caller's legal entity without throwing when this stamp does not serve it.</summary>
internal static class ManualJournalScope
{
    public static (LegalEntityId Id, LegalEntityCode Code)? Of(RequestContext context, ILegalEntityDirectory legalEntities)
    {
        if (context.LegalEntity is not { } code)
        {
            return null;
        }

        try
        {
            return (legalEntities.Resolve(code), code);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// <c>fin.ManualJournal.create</c> (REQ-FIN-215..-226): validates the proposal against FIN's own data (balanced, evidence,
/// book of the legal entity, every account active and postable in the chart, period not locked) and stores it as a DRAFT.
/// Nothing is posted: the ledger changes only when BO-FIN-01b approves it.
/// </summary>
internal sealed class CreateManualJournalHandler(FinanceDbContext db, ReferenceData reference, FinancePeriods periods, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities)
    : ICommandHandler<CreateManualJournal, ManualJournalEnvelope>
{
    public async Task<Result<ManualJournalEnvelope>> HandleAsync(CreateManualJournal command, CancellationToken cancellationToken)
    {
        var valid = ManualJournals.Validate(command.Request);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var journal = valid.Value;
        if (ManualJournalScope.Of(context, legalEntities) is not (var legalEntity, var code))
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "The legal entity is not served by this stamp.");
        }

        var profile = await reference.BookProfileAsync(code.Value, cancellationToken).ConfigureAwait(false);
        if (profile is null || !profile.ActiveBooks.Contains(journal.Book, StringComparer.Ordinal))
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", $"The book '{journal.Book}' is not an active book of the legal entity.");
        }

        // One functional currency while the slice is EUR-only: fail closed instead of guessing a rate (PITFALLS 10).
        if (!string.Equals(journal.Currency, profile.FunctionalCurrency.Code, StringComparison.Ordinal))
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", $"Manual journals are posted in the functional currency {profile.FunctionalCurrency.Code}.");
        }

        var accounts = journal.Lines.Select(l => l.Account).Distinct(StringComparer.Ordinal).ToList();
        var chart = await db.Accounts.AsNoTracking().Where(a => a.LegalEntityCode == code.Value && a.Book == journal.Book && accounts.Contains(a.AccountCode))
            .ToDictionaryAsync(a => a.AccountCode, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        foreach (var account in accounts)
        {
            // System-only accounts are written by posting rules only; a person posting there would bypass the rule set.
            if (!chart.TryGetValue(account, out var chartAccount) || chartAccount.Status != "ACTIVE" || chartAccount.SystemOnly)
            {
                return DomainError.Of(ModuleCode.FIN, "TEMPLATE-ACCOUNT", $"Account {account} is not an active, manually postable account of book {journal.Book}.");
            }
        }

        if (await periods.IsLockedAsync(legalEntity.Value, journal.AccountingDate, cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.FIN, "PERIOD-LOCKED", $"The accounting period {FinancePeriods.CodeOf(journal.AccountingDate)} is locked.");
        }

        var row = new ManualJournalRow
        {
            ManualJournalId = Guid.CreateVersion7(),
            LegalEntityId = legalEntity.Value,
            Status = ManualJournalStatus.Draft,
            Description = journal.Description,
            Book = journal.Book,
            AccountingDate = journal.AccountingDate,
            Currency = journal.Currency,
            EvidenceRef = journal.Evidence,
            CreatedBy = context.Actor.Id,
            CreatedAt = clock.Now,
            RecordVersion = 1,
        };
        var lines = journal.Lines.Select((l, i) => new ManualJournalLineRow
        {
            LineId = Guid.CreateVersion7(), ManualJournalId = row.ManualJournalId, LineNo = i + 1, AccountCode = l.Account, Side = l.Side, Amount = l.Amount,
        }).ToList();
        db.ManualJournals.Add(row);
        db.ManualJournalLines.AddRange(lines);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ManualJournalReader.ToEnvelope(row, lines);
    }
}

/// <summary>
/// <c>fin.ManualJournal.withdraw</c>: the maker takes a journal back before it is decided. Only the maker may (FIN-ERR-SOD);
/// a decided, posted or already withdrawn journal answers FIN-ERR-STATE. The lines stay for the audit trail.
/// </summary>
internal sealed class WithdrawManualJournalHandler(FinanceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities)
    : ICommandHandler<WithdrawManualJournal, ManualJournalEnvelope>
{
    public async Task<Result<ManualJournalEnvelope>> HandleAsync(WithdrawManualJournal command, CancellationToken cancellationToken)
    {
        var scope = ManualJournalScope.Of(context, legalEntities);
        var row = scope is { } s
            ? await db.ManualJournals.SingleOrDefaultAsync(r => r.ManualJournalId == command.Id && r.LegalEntityId == s.Id.Value, cancellationToken).ConfigureAwait(false)
            : null;
        if (row is null)
        {
            return DomainError.Of(ModuleCode.FIN, "NOT-FOUND", "The manual journal does not exist.");
        }

        var actor = context.Actor.Id;
        if (!string.Equals(row.CreatedBy, actor, StringComparison.Ordinal) && !string.Equals(row.SubmittedBy, actor, StringComparison.Ordinal))
        {
            return DomainError.Of(ModuleCode.FIN, "SOD", "Only the maker of a manual journal can withdraw it.");
        }

        if (!ManualJournalStatus.CanWithdraw(row.Status))
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", $"A {row.Status} manual journal cannot be withdrawn.");
        }

        row.Status = ManualJournalStatus.Withdrawn;
        row.RecordVersion++;
        row.DecidedAt = clock.Now;
        row.DecidedBy = actor;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", "The manual journal changed meanwhile; load it and try again.");
        }

        var lines = await db.ManualJournalLines.AsNoTracking().Where(l => l.ManualJournalId == row.ManualJournalId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ManualJournalReader.ToEnvelope(row, lines);
    }
}
