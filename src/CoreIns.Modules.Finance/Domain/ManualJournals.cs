using System.Globalization;
using System.Text.RegularExpressions;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Finance.Domain;

/// <summary>Manual journal statuses (PRD-09 maker-checker; DRAFT is the state of a journal created but not yet submitted).</summary>
internal static class ManualJournalStatus
{
    public const string Draft = "DRAFT";
    public const string Submitted = "SUBMITTED";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Withdrawn = "WITHDRAWN";
    public const string Posted = "POSTED";

    /// <summary>The maker may take a journal back until it is decided.</summary>
    public static bool CanWithdraw(string status) => status is Draft or Submitted;
}

/// <summary>One line of a manual journal request (the wire shape of the web form).</summary>
internal sealed record ManualJournalLineInput(string? Account, string? Side, string? Amount);

/// <summary>The evidence of a manual journal: a document or ticket reference (REQ-FIN-216).</summary>
internal sealed record ManualJournalEvidenceInput(string? Reference);

/// <summary><c>fin.ManualJournal.create</c> request.</summary>
internal sealed record ManualJournalCreateInput(
    string? Description, string? Book, string? AccountingDate, string? Currency, IReadOnlyList<ManualJournalLineInput>? Lines, ManualJournalEvidenceInput? Evidence);

/// <summary><c>fin.ManualJournal.withdraw</c> request.</summary>
internal sealed record ManualJournalWithdrawInput(Guid? Id);

/// <summary>A manual journal as the API returns it (amounts as invariant decimal strings).</summary>
internal sealed record ManualJournalLineView(string Account, string Side, string Amount);

internal sealed record ManualJournalView(
    Guid Id, string Number, string Status, string Description, string Book, string AccountingDate, string Currency,
    IReadOnlyList<ManualJournalLineView> Lines, string EvidenceRef, string CreatedBy, string? SubmittedBy, string? SubmittedAt, string? DecidedBy, string? DecisionReason);

/// <summary>The <c>manualJournal</c> + <c>postedIds</c> envelope of create, get, list and withdraw.</summary>
internal sealed record ManualJournalEnvelope(ManualJournalView ManualJournal, IReadOnlyList<Guid> PostedIds);

/// <summary>A create request after its shape checks (accounts, book and period are checked against FIN data by the handler).</summary>
internal sealed record ValidManualJournal(
    string Description, string Book, BusinessDate AccountingDate, string Currency, string Evidence, IReadOnlyList<(string Account, string Side, decimal Amount)> Lines);

/// <summary>Shape and balance rules of a manual journal (BO-FIN-01a); pure, no database.</summary>
internal static partial class ManualJournals
{
    public const int MaxLines = 200;
    public const int MaxText = 200;

    [GeneratedRegex(@"^\d{1,15}(\.\d{1,2})?$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AmountPattern();

    public static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    public static Result<ValidManualJournal> Validate(ManualJournalCreateInput? input)
    {
        if (input is null)
        {
            return Invalid("A request body is required.");
        }

        var description = input.Description?.Trim() ?? string.Empty;
        if (description.Length is 0 or > MaxText)
        {
            return Invalid($"description is required, at most {MaxText} characters.");
        }

        var book = input.Book?.Trim() ?? string.Empty;
        if (book.Length is 0 or > 32)
        {
            return Invalid("book is required, at most 32 characters.");
        }

        if (!DateOnly.TryParseExact(input.AccountingDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return Invalid("accountingDate must be a date (yyyy-MM-dd).");
        }

        var currency = input.Currency?.Trim() ?? string.Empty;
        if (currency.Length != 3 || !currency.All(c => c is >= 'A' and <= 'Z'))
        {
            return Invalid("currency must be a three-letter ISO code.");
        }

        var evidence = input.Evidence?.Reference?.Trim() ?? string.Empty;
        if (evidence.Length is 0 or > MaxText)
        {
            return DomainError.Of(ModuleCode.FIN, "EVIDENCE-REQUIRED", $"A manual journal needs an evidence reference (document or ticket), at most {MaxText} characters (REQ-FIN-216).");
        }

        if (input.Lines is null || input.Lines.Count < 2 || input.Lines.Count > MaxLines)
        {
            return Invalid($"a manual journal needs 2..{MaxLines} lines.");
        }

        var lines = new List<(string, string, decimal)>(input.Lines.Count);
        decimal debit = 0m, credit = 0m;
        foreach (var line in input.Lines)
        {
            var account = line.Account?.Trim() ?? string.Empty;
            if (account.Length is 0 or > 32)
            {
                return Invalid("every line needs an account (at most 32 characters).");
            }

            var side = line.Side?.Trim().ToUpperInvariant();
            if (side is not (Sides.Debit or Sides.Credit))
            {
                return Invalid("every line needs a side DEBIT or CREDIT.");
            }

            // Money is decimal with at most two places; anything else (exponents, signs, more places) is refused, never rounded (PITFALLS 11, 42).
            if (line.Amount is null || !AmountPattern().IsMatch(line.Amount)
                || !decimal.TryParse(line.Amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) || amount <= 0m)
            {
                return Invalid("every line amount must be a positive decimal with at most two decimals.");
            }

            if (side == Sides.Debit)
            {
                debit += amount;
            }
            else
            {
                credit += amount;
            }

            lines.Add((account, side, amount));
        }

        if (debit != credit)
        {
            return DomainError.Of(ModuleCode.FIN, "UNBALANCED", $"Debits ({Money(debit)}) must equal credits ({Money(credit)}) (REQ-FIN-068).");
        }

        return new ValidManualJournal(description, book, new BusinessDate(date), currency, evidence, lines);
    }

    private static DomainError Invalid(string detail) => DomainError.Of(ModuleCode.FIN, "VALIDATION", detail);
}
