using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Services;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Billing.Commands;

/// <summary><c>bil.Reconciliation.bank</c> (BO-BIL-11): import a statement of an own bank account and reconcile it.</summary>
internal sealed record ImportBankStatement(ReconciliationBankRequest Request) : ICommand<ReconciliationBankResponse>;

internal sealed class ImportBankStatementValidator : AbstractValidator<ImportBankStatement>
{
    public ImportBankStatementValidator()
    {
        RuleFor(c => c.Request.BankAccount).NotEmpty().MaximumLength(60).Must(a => Iban.TryParse(a, out _)).WithErrorCode("IBAN_INVALID").WithMessage("The bank account is not a valid IBAN.");
        RuleFor(c => c.Request.Content).NotEmpty().MaximumLength(4_000_000);
    }
}

/// <summary>Views and breaks of a stored statement; the same for a fresh import and for a repeated one.</summary>
internal static class BankStatementViews
{
    public static ReconciliationBankResponse Response(BankStatementRow statement, IReadOnlyList<BankStatementLineRow> lines, bool duplicate)
    {
        var breaks = Breaks(statement, lines);
        return new ReconciliationBankResponse
        {
            Status = breaks.Count == 0 ? ReconciliationBankResponse.StatusValue.Matched : ReconciliationBankResponse.StatusValue.Breaks,
            Duplicate = duplicate,
            Statement = Statement(statement, lines),
            Lines = [.. lines.OrderBy(l => l.LineNo).Select(Line)],
            Breaks = breaks,
        };
    }

    private static Money Eur(decimal amount, string currency) => new(amount, Currency.FromCode(currency));

    private static BankStatementView Statement(BankStatementRow s, IReadOnlyList<BankStatementLineRow> lines) => new()
    {
        StatementId = s.StatementId,
        MaskedBankAccount = IbanMask.Mask(s.BankAccount[^4..]),
        StatementRef = s.StatementRef,
        Format = s.Format == "CSV" ? BankStatementView.FormatValue.Csv : BankStatementView.FormatValue.Camt053,
        Currency = s.Currency,
        FromDate = s.FromDate,
        ToDate = s.ToDate,
        OpeningBalance = s.OpeningBalance is { } o ? Eur(o, s.Currency) : null,
        ClosingBalance = s.ClosingBalance is { } c ? Eur(c, s.Currency) : null,
        LineCount = s.LineCount,
        MatchedCount = lines.Count(l => l.Status == BankLineStates.Matched),
        PartialCount = lines.Count(l => l.Status == BankLineStates.Partial),
        SuspenseCount = lines.Count(l => l.Status == BankLineStates.Suspense),
        UnmatchedCount = lines.Count(l => l.Status == BankLineStates.Unmatched),
        FileRef = s.FileRef,
        ImportedBy = s.ImportedBy,
        ImportedAt = s.ImportedAt,
    };

    private static BankStatementLineView Line(BankStatementLineRow l) => new()
    {
        LineNo = l.LineNo,
        Direction = l.Direction == BankLineStates.Credit ? BankStatementLineView.DirectionValue.Credit : BankStatementLineView.DirectionValue.Debit,
        BookingDate = l.BookingDate,
        ValueDate = l.ValueDate,
        Amount = Eur(l.Amount, l.Currency),
        Reference = l.Reference,
        EndToEndId = l.EndToEndId,
        Status = l.Status switch
        {
            BankLineStates.Matched => BankStatementLineView.StatusValue.Matched,
            BankLineStates.Partial => BankStatementLineView.StatusValue.Partial,
            BankLineStates.Suspense => BankStatementLineView.StatusValue.Suspense,
            _ => BankStatementLineView.StatusValue.Unmatched,
        },
        MatchedAmount = l.MatchedAmount > 0 ? Eur(l.MatchedAmount, l.Currency) : null,
        ReceiptId = l.ReceiptId,
        DisbursementId = l.DisbursementId,
        SuspenseItemId = l.SuspenseItemId,
        Note = l.Note,
    };

    private static List<BankReconciliationBreak> Breaks(BankStatementRow s, IReadOnlyList<BankStatementLineRow> lines)
    {
        var breaks = new List<BankReconciliationBreak>();
        foreach (var l in lines.Where(l => l.Status != BankLineStates.Matched).OrderBy(l => l.LineNo))
        {
            var kind = l.Status switch
            {
                BankLineStates.Partial => BankReconciliationBreak.KindValue.PartialMatch,
                BankLineStates.Suspense => BankReconciliationBreak.KindValue.InSuspense,
                _ when l.Direction == BankLineStates.Debit => BankReconciliationBreak.KindValue.UnmatchedDebit,
                _ => BankReconciliationBreak.KindValue.UnmatchedCredit,
            };
            breaks.Add(new BankReconciliationBreak
            {
                Kind = kind,
                LineNo = l.LineNo,
                Amount = Eur(l.Amount, l.Currency),
                Difference = l.Status == BankLineStates.Partial && l.MatchedAmount > 0 && l.MatchedAmount < l.Amount ? Eur(l.Amount - l.MatchedAmount, l.Currency) : null,
                Reference = l.EndToEndId ?? l.Reference,
                ReceiptId = l.ReceiptId,
                SuspenseItemId = l.SuspenseItemId,
                Note = l.Note,
            });
        }

        if (s.OpeningBalance is { } opening && s.ClosingBalance is { } closing)
        {
            var expected = opening + lines.Where(l => l.Direction == BankLineStates.Credit).Sum(l => l.Amount) - lines.Where(l => l.Direction == BankLineStates.Debit).Sum(l => l.Amount);
            if (expected != closing)
            {
                breaks.Add(new BankReconciliationBreak
                {
                    Kind = BankReconciliationBreak.KindValue.BalanceMismatch,
                    Amount = Eur(closing, s.Currency),
                    Difference = Eur(closing - expected, s.Currency),
                    Note = "The closing balance is not the opening balance plus the credits minus the debits of the lines.",
                });
            }
        }

        return breaks;
    }
}

/// <summary>
/// Imports and reconciles one statement. Steps: the bank-file store must be bound (Production fails closed until a real adapter exists);
/// the file is parsed whole or refused; a camt.053 file must name the same own account; the same content is one statement (the first import
/// is returned, nothing is posted twice) and the same statement reference with other content is a conflict; then every line is matched
/// (<see cref="BankReconciler"/>) and the file is kept in the store.
/// </summary>
internal sealed class ImportBankStatementHandler(
    BillingDbContext db, BankReconciler reconciler, LedgerWriter ledger, RequestContext context, IClock clock, IServiceProvider services)
    : ICommandHandler<ImportBankStatement, ReconciliationBankResponse>
{
    public async Task<Result<ReconciliationBankResponse>> HandleAsync(ImportBankStatement command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (services.GetService<IBankFileStore>() is not { } store)
        {
            return DomainError.Of(ModuleCode.BIL, "METHOD-UNAVAILABLE", "No bank-file store is bound to keep the statement; it is not imported (fail closed).");
        }

        var account = Iban.Parse(request.BankAccount).Value;
        var hash = Sha256Hash.ComputeUtf8(request.Content).Value;
        var camt = request.Format == ReconciliationBankRequest.FormatValue.Camt053;
        var parsed = camt ? BankStatementParser.ParseCamt053(request.Content) : BankStatementParser.ParseCsv(request.Content, hash);
        if (parsed.IsFailure)
        {
            return parsed.Error!;
        }

        var statement = parsed.Value;
        if (camt && (!Iban.TryParse(statement.Iban, out var named) || named.Value != account))
        {
            return DomainError.Of(ModuleCode.BIL, "STATEMENT-INVALID", "The statement names another bank account than the one given.");
        }

        var legalEntity = ledger.LegalEntityId;

        // One import of an account at a time, so a repeated or concurrent upload cannot post twice.
        var lockKey = "bil.bank-statement:" + legalEntity.Value.ToString("D") + ":" + account;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken).ConfigureAwait(false);

        var existing = await db.BankStatements.AsNoTracking()
            .Where(s => s.LegalEntityId == legalEntity && s.BankAccount == account && (s.ContentHash == hash || s.StatementRef == statement.StatementRef))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (existing.FirstOrDefault(s => s.ContentHash == hash) is { } same)
        {
            var storedLines = await db.BankStatementLines.AsNoTracking().Where(l => l.StatementId == same.StatementId).ToListAsync(cancellationToken).ConfigureAwait(false);
            return BankStatementViews.Response(same, storedLines, duplicate: true);
        }

        if (existing.Count > 0)
        {
            return DomainError.Of(ModuleCode.BIL, "STATEMENT-CONFLICT", $"A statement with the reference {statement.StatementRef} was imported before with different content.");
        }

        var id = Guid.CreateVersion7();
        var row = new BankStatementRow
        {
            StatementId = id,
            LegalEntityId = legalEntity,
            BankAccount = account,
            StatementRef = statement.StatementRef,
            Format = camt ? "CAMT053" : "CSV",
            ContentHash = hash,
            FileRef = $"bil/bank-statements/{id:N}/statement-{hash[..12]}.{(camt ? "xml" : "csv")}",
            Currency = statement.Currency,
            FromDate = statement.From,
            ToDate = statement.To,
            OpeningBalance = statement.Opening,
            ClosingBalance = statement.Closing,
            LineCount = statement.Lines.Count,
            ImportedAt = clock.Now,
            ImportedBy = context.Actor.ToString(),
        };
        db.BankStatements.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var lines = await reconciler.MatchAsync(row, statement, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The file (third-party IBANs and names) is kept outside the tables; the reference is idempotent.
        await store.StoreAsync(row.FileRef, System.Text.Encoding.UTF8.GetBytes(request.Content), cancellationToken).ConfigureAwait(false);
        return BankStatementViews.Response(row, lines, duplicate: false);
    }
}

internal sealed class ImportBankStatementAuditor : ICommandAuditor<ImportBankStatement, ReconciliationBankResponse>
{
    public CommandAuditFacts Describe(ImportBankStatement command, Result<ReconciliationBankResponse>? result)
    {
        var id = result is { IsSuccess: true } ok ? ok.Value.Statement.StatementId.ToString("D") : "new";
        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.BIL, "BankStatement", id),
            BusinessKeys = BusinessKeys.Empty.With("statementId", id),
            Changes = result is { IsSuccess: true } done
                ? AuditDiff.Compute(null, new { format = command.Request.Format.ToString(), lines = done.Value.Statement.LineCount, duplicate = done.Value.Duplicate })
                : [],
        };
    }
}
