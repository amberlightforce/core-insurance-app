using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Modules.Finance.Posting;
using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreIns.Modules.Finance.MigrationImport;

/// <summary>Object type and journal markers of the FIN importer.</summary>
internal static class FinanceImportTypes
{
    public const string OpeningBalance = "OpeningBalance";

    /// <summary>Rule code of the opening-balance journal lines (the journal is not produced by a posting rule).</summary>
    public const string RuleCode = "MIG-OPENING";

    public const string SourceModule = "MIG";

    public const string SourceEventType = "OpeningBalanceImported";
}

/// <summary>One account of the legacy trial balance: exactly one of debit and credit is positive.</summary>
internal sealed record ImportBalanceLine(string AccountCode, decimal Debit, decimal Credit);

/// <summary>The legacy trial-balance totals the lines must reconcile to.</summary>
internal sealed record ImportTrialBalance(decimal Debit, decimal Credit);

/// <summary>The legacy opening trial balance as at <c>accountingDate</c> (SL6-MIG-BIL-IMPORT).</summary>
internal sealed record ImportOpeningBalancePayload(
    string LegacyId,
    DateOnly AccountingDate,
    string? Book,
    string? Currency,
    List<ImportBalanceLine>? Lines,
    ImportTrialBalance? TrialBalance);

/// <summary>
/// <c>IMigrationImporter</c> for module FIN (SL6-MIG-BIL-IMPORT): the opening-balance journal (one balanced, append-only journal for a
/// legacy trial balance, source MIG). It reconciles to the payload's legacy trial-balance totals (the difference must be 0.00, else
/// the import is refused) and touches balance-sheet accounts only: no revenue or expense is recognised and nothing is new business.
/// </summary>
internal sealed class FinanceMigrationImporter(
    FinanceDbContext db,
    ReferenceData reference,
    JournalWriter writer,
    IEventPublisher events,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock) : IMigrationImporter
{
    private const string Validation = "MIG-ERR-VALIDATION";

    public string ModuleName => "FIN";

    public async Task<ImportOutcome> ImportAsync(MigrationImportItem item, MigrationImportContext importContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(importContext);
        if (!string.Equals(item.ObjectType, FinanceImportTypes.OpeningBalance, StringComparison.Ordinal))
        {
            return Failed(Validation, $"FIN imports object type OpeningBalance, not '{item.ObjectType}'.");
        }

        ImportOpeningBalancePayload? payload;
        try
        {
            payload = item.Payload.Deserialize<ImportOpeningBalancePayload>(SharedKernelJson.Options);
        }
        catch (JsonException ex)
        {
            return Failed(Validation, "The payload is not valid: " + ex.Message);
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.LegacyId) || payload.Lines is not { Count: > 0 } || payload.TrialBalance is null)
        {
            return Failed(Validation, "An opening balance needs legacyId, accountingDate, lines and trialBalance.");
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var legalEntityCode = context.LegalEntity!.Value.Value;
        var jurisdiction = (context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.")).Value;
        var profile = await reference.BookProfileAsync(legalEntityCode, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return Failed("MIG-ERR-DEPENDENCY", $"Legal entity {legalEntityCode} has no book profile.");
        }

        var book = payload.Book ?? profile.StatutoryBook;
        var date = new BusinessDate(payload.AccountingDate);
        var setup = await reference.BookSetupAsync(profile, book, date, cancellationToken).ConfigureAwait(false);
        if (setup is null)
        {
            return Failed("MIG-ERR-DEPENDENCY", $"Book {book} has no posting rule set in force on {payload.AccountingDate:yyyy-MM-dd}.");
        }

        // TODO(hardening): foreign-currency trial balances (would need functional conversion at the migration rate).
        var currency = Currency.FromCode(payload.Currency ?? profile.FunctionalCurrency.Code);
        if (currency != setup.FunctionalCurrency)
        {
            return Failed(Validation, $"Only the functional currency {setup.FunctionalCurrency.Code} is imported.");
        }

        var types = await db.Accounts.AsNoTracking().Where(a => a.LegalEntityCode == legalEntityCode && a.Book == book)
            .ToDictionaryAsync(a => a.AccountCode, a => a.AccountType, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var drafts = new List<JournalLineDraft>();
        decimal debit = 0m, credit = 0m;
        foreach (var line in payload.Lines)
        {
            if (line.Debit < 0 || line.Credit < 0 || (line.Debit > 0) == (line.Credit > 0))
            {
                return Failed(Validation, $"Account {line.AccountCode}: exactly one of debit and credit must be positive.");
            }

            var amount = new Money(line.Debit > 0 ? line.Debit : line.Credit, currency);
            if (!amount.IsRoundedToMinorUnits)
            {
                return Failed(Validation, $"Account {line.AccountCode}: more decimals than the currency allows.");
            }

            if (!setup.Accounts.TryGetValue(line.AccountCode ?? string.Empty, out var account) || !account.Active)
            {
                return Failed("MIG-ERR-UNKNOWN-ACCOUNT", $"Account {line.AccountCode} is not an active account of the {book} chart.");
            }

            // No revenue recognition: only balance-sheet accounts carry an opening balance.
            if (types.GetValueOrDefault(line.AccountCode!) is "INCOME" or "EXPENSE")
            {
                return Failed("MIG-ERR-PNL-ACCOUNT", $"Account {line.AccountCode} is an income or expense account: no revenue is recognised from a migrated balance.");
            }

            var side = line.Debit > 0 ? Sides.Debit : Sides.Credit;
            debit += side == Sides.Debit ? amount.Amount : 0m;
            credit += side == Sides.Credit ? amount.Amount : 0m;
            drafts.Add(new JournalLineDraft(drafts.Count + 1, line.AccountCode!, side, amount, amount, FinanceImportTypes.RuleCode, LineDimensions.None));
        }

        // Reconciliation to the legacy trial balance: the difference must be exactly 0.00 on both sides.
        var debitDifference = debit - payload.TrialBalance.Debit;
        var creditDifference = credit - payload.TrialBalance.Credit;
        if (debitDifference != 0m || creditDifference != 0m)
        {
            return Failed(
                "MIG-ERR-RECONCILIATION",
                $"The lines do not reconcile to the legacy trial balance: debit difference {debitDifference.ToString("0.00", CultureInfo.InvariantCulture)}, credit difference {creditDifference.ToString("0.00", CultureInfo.InvariantCulture)}.");
        }

        if (debit != credit)
        {
            return Failed("MIG-ERR-UNBALANCED", $"The legacy trial balance is not balanced: debit {debit.ToString("0.00", CultureInfo.InvariantCulture)}, credit {credit.ToString("0.00", CultureInfo.InvariantCulture)}.");
        }

        if (await db.MigrationImports.AnyAsync(m => m.LegalEntityId == legalEntity.Value && m.LegacyId == payload.LegacyId, cancellationToken).ConfigureAwait(false))
        {
            return Failed("MIG-ERR-DUPLICATE", $"Legacy opening balance {payload.LegacyId} is already imported.");
        }

        if (importContext.DryRun)
        {
            return new ImportOutcome(ImportStatus.Imported, null, null, $"Would post opening balance {payload.LegacyId}: debit {debit.ToString("0.00", CultureInfo.InvariantCulture)} = credit {credit.ToString("0.00", CultureInfo.InvariantCulture)}.");
        }

        var draft = new JournalDraft(book, date, date, SourceTypes.Event, setup.RuleSet.RuleSetId, setup.RuleSet.Version, [FinanceImportTypes.RuleCode], drafts);
        var source = new JournalSource(legalEntity, legalEntityCode, jurisdiction, FinanceImportTypes.SourceModule, FinanceImportTypes.SourceEventType, [], "migration:" + payload.LegacyId);
        WrittenJournal written;
        try
        {
            written = await writer.WriteAsync(draft, source, setup.FunctionalCurrency, null, null, cancellationToken).ConfigureAwait(false);
            db.MigrationImports.Add(new MigrationImportRow
            {
                JournalId = written.JournalId, LegalEntityId = legalEntity.Value, BatchId = importContext.BatchId, LegacyId = payload.LegacyId,
                Book = book, AccountingDate = payload.AccountingDate, TrialBalanceDebit = payload.TrialBalance.Debit, TrialBalanceCredit = payload.TrialBalance.Credit,
                ImportedAt = clock.Now,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Failed("MIG-ERR-DUPLICATE", $"Legacy opening balance {payload.LegacyId} conflicts with an existing record.");
        }

        events.Publish(written.Posted);
        return new ImportOutcome(ImportStatus.Imported, written.JournalId, null, $"Journal {written.JournalNumber}.");
    }

    private static ImportOutcome Failed(string code, string message) => new(ImportStatus.Failed, null, code, message);
}
