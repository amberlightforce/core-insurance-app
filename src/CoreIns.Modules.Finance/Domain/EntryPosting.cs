using CoreIns.SharedKernel;

namespace CoreIns.Modules.Finance.Domain;

/// <summary>One sub-ledger line of a posting source (BillingEntryPosted line: account, side, amount, dimensions).</summary>
internal sealed record SourceLine(string Account, string Side, Money Amount, IReadOnlyDictionary<string, string?> Dimensions)
{
    public string? Dimension(string key) => Dimensions.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    public Guid? Id(string key) => Guid.TryParse(Dimension(key), out var id) ? id : null;
}

/// <summary>A posting-source fact normalised for posting (REQ-FIN-034): entry id, entry type, dates and lines.</summary>
internal sealed record SourceEntry(Guid EntryId, string EntryType, BusinessDate AccountingDate, BusinessDate BusinessDate, IReadOnlyList<SourceLine> Lines);

/// <summary>Policy context FIN keeps from POL PolicyBound (CONTEXT relevance): business key, product and artefact.</summary>
internal sealed record PolicyContext(
    Guid PolicyId, string PolicyNumber, Guid TermId, Guid TransactionId, string ProductCode, string ProductVersion, string ArtefactHash, bool ChargeTypesLoaded);

/// <summary>An account of a book's chart.</summary>
internal sealed record ChartAccount(string Code, string NameEl, string NameEn, bool Active);

/// <summary>What posting into one book needs: functional currency, the rule set in force, chart and GL-key derivations.</summary>
internal sealed record BookSetup(
    string Book, Currency FunctionalCurrency, RuleSet RuleSet, IReadOnlyDictionary<string, ChartAccount> Accounts, IReadOnlyDictionary<string, string> Derivations);

/// <summary>The journal for one book, or why there is none (intake-exception reason, or <see cref="ExceptionReasons.Empty"/>).</summary>
internal sealed record PostingOutcome(JournalDraft? Journal, string? Reason, string? Detail)
{
    public static PostingOutcome Fail(string reason, string detail) => new(null, reason, detail);
}

/// <summary>
/// Maps a source entry to a journal of one book with the book's rule set (REQ-FIN-036, -048, -049, -050). Pure: the
/// policy context and the GL-key lookup are passed in. Every source line maps to one journal line with the same side
/// and amount (a negative amount posts on the opposite side), so a balanced entry gives a balanced journal; the result
/// is checked anyway (REQ-FIN-068). Amounts arrive rounded: FIN never rounds, and a line that is not rounded to the
/// currency's minor units is refused as a precision error. Without FX rates in the slice, a line in another currency
/// than the book's functional currency is refused (FIN-ERR-RATE-MISSING).
/// </summary>
internal static class EntryPosting
{
    /// <summary>Term and policy ids the entry's lines refer to (the policy context they need for dimensions and GL keys).</summary>
    public static IReadOnlyList<(Guid? TermId, Guid? PolicyId)> PolicyReferences(SourceEntry entry) =>
        [.. entry.Lines
            .Select(l => (TermId: l.Id(LineDimensionKeys.PolicyTermId), PolicyId: l.Id(LineDimensionKeys.PolicyId)))
            .Where(r => r.TermId is not null || r.PolicyId is not null)
            .Distinct()];

    public static PostingOutcome Map(
        string sourceEvent,
        SourceEntry entry,
        BookSetup book,
        Func<SourceLine, PolicyContext?> contextOf,
        Func<string, string, string?> glKeyOf)
    {
        var lines = new List<JournalLineDraft>();
        var ruleCodes = new List<string>();
        foreach (var line in entry.Lines)
        {
            var category = line.Dimension(LineDimensionKeys.ChargeCategory);
            var chargeType = line.Dimension(LineDimensionKeys.ChargeType);
            var match = PostingRules.Resolve(book.RuleSet.Rules, sourceEvent, entry.EntryType, line.Account, category, chargeType);
            if (match.Rule is not { } rule)
            {
                return PostingOutcome.Fail(match.Reason!, match.Detail!);
            }

            var context = contextOf(line);
            string? glKey = null;
            string account;
            if (rule.DeriveFrom == DeriveFrom.GlKey)
            {
                glKey = chargeType is not null && context is { ChargeTypesLoaded: true } ? glKeyOf(context.ArtefactHash, chargeType) : null;
                if (glKey is null)
                {
                    return PostingOutcome.Fail(ExceptionReasons.NoChargeType,
                        $"Charge type {chargeType ?? "-"} has no PFC GL key in the policy's product artefact (rule {rule.Code}).");
                }

                if (!book.Derivations.TryGetValue(glKey, out var derived))
                {
                    return PostingOutcome.Fail(ExceptionReasons.NoAccountDerivation, $"GL key {glKey} has no account in book {book.Book} (rule {rule.Code}).");
                }

                account = derived;
            }
            else
            {
                account = rule.Account!;
            }

            if (!book.Accounts.TryGetValue(account, out var chartAccount) || !chartAccount.Active)
            {
                return PostingOutcome.Fail(ExceptionReasons.AccountInactive, $"Account {account} is not an active account of book {book.Book}.");
            }

            if (line.Amount.Currency != book.FunctionalCurrency)
            {
                return PostingOutcome.Fail(ExceptionReasons.RateMissing,
                    $"Line in {line.Amount.Currency.Code}; book {book.Book} is kept in {book.FunctionalCurrency.Code} and no FX rate source exists yet.");
            }

            if (!line.Amount.IsRoundedToMinorUnits)
            {
                return PostingOutcome.Fail(ExceptionReasons.Precision, $"Amount {line.Amount} is not rounded to the currency's minor units; FIN does not round.");
            }

            if (line.Side is not (Sides.Debit or Sides.Credit))
            {
                return PostingOutcome.Fail(ExceptionReasons.Unbalanced, $"Source line side {line.Side} is unknown.");
            }

            if (line.Amount.IsZero)
            {
                continue;
            }

            var side = line.Amount.IsNegative ? Sides.Opposite(line.Side) : line.Side;
            var amount = line.Amount.Abs();
            lines.Add(new JournalLineDraft(lines.Count + 1, account, side, amount, amount, rule.Code, Dimensions(line, context, glKey)));
            if (!ruleCodes.Contains(rule.Code, StringComparer.Ordinal))
            {
                ruleCodes.Add(rule.Code);
            }
        }

        if (lines.Count == 0)
        {
            return PostingOutcome.Fail(ExceptionReasons.Empty, "The entry has no non-zero line.");
        }

        var problems = Journals.Check(lines);
        if (problems.Count > 0)
        {
            return PostingOutcome.Fail(ExceptionReasons.Unbalanced, string.Join(" ", problems));
        }

        return new PostingOutcome(
            new JournalDraft(book.Book, entry.AccountingDate, entry.BusinessDate, SourceTypes.Event, book.RuleSet.RuleSetId, book.RuleSet.Version, ruleCodes, lines),
            null,
            null);
    }

    private static LineDimensions Dimensions(SourceLine line, PolicyContext? context, string? glKey) => new()
    {
        ProductCode = line.Dimension(LineDimensionKeys.ProductCode) ?? context?.ProductCode,
        ProductVersion = context?.ProductVersion,
        CoverageCode = line.Dimension(LineDimensionKeys.CoverageCode),
        ChargeType = line.Dimension(LineDimensionKeys.ChargeType),
        ChargeCategory = line.Dimension(LineDimensionKeys.ChargeCategory),
        GlKey = glKey,
        PolicyId = line.Id(LineDimensionKeys.PolicyId) ?? context?.PolicyId,
        PolicyNumber = context?.PolicyNumber,
        PolicyTermId = line.Id(LineDimensionKeys.PolicyTermId) ?? context?.TermId,
        PolicyTransactionId = line.Id(LineDimensionKeys.TransactionId),
        ChargeId = line.Id(LineDimensionKeys.ChargeId),
        BillingAccountId = line.Id(LineDimensionKeys.BillingAccountId),
        InvoiceId = line.Id(LineDimensionKeys.InvoiceId),
        ReceiptId = line.Id(LineDimensionKeys.ReceiptId),
    };
}
