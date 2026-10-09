using CoreIns.SharedKernel;

namespace CoreIns.Modules.Finance.Domain;

/// <summary>Dimensions of one journal line (REQ-FIN-075, slice subset). Ids only; no personal data.</summary>
internal sealed record LineDimensions
{
    public string? ProductCode { get; init; }

    public string? ProductVersion { get; init; }

    public string? CoverageCode { get; init; }

    public string? ChargeType { get; init; }

    public string? ChargeCategory { get; init; }

    public string? GlKey { get; init; }

    public Guid? PolicyId { get; init; }

    public string? PolicyNumber { get; init; }

    public Guid? PolicyTermId { get; init; }

    public Guid? PolicyTransactionId { get; init; }

    public Guid? ChargeId { get; init; }

    public Guid? BillingAccountId { get; init; }

    public Guid? InvoiceId { get; init; }

    public Guid? ReceiptId { get; init; }

    /// <summary>Claim (CLM facts and the BIL disbursement entries of claim payments, D-SL2-08).</summary>
    public Guid? ClaimId { get; init; }

    public Guid? ExposureId { get; init; }

    public Guid? ReserveLineId { get; init; }

    public string? CostType { get; init; }

    public string? CostCategory { get; init; }

    /// <summary>CLM claim payment: GL-2510 nets to zero per claim payment (REQ-FIN-037).</summary>
    public Guid? ClaimPaymentId { get; init; }

    public Guid? DisbursementId { get; init; }

    /// <summary>BIL policy refund: the refund payable nets to zero per refund (SL3-FIN-RULES, like GL-2510 per claim payment).</summary>
    public Guid? RefundId { get; init; }

    /// <summary>Servicing context of the source line (in memory only, not a journal column): what the tax-treatment check needs on the resolved account.</summary>
    public string? TransactionKind { get; init; }

    public string? CancellationSource { get; init; }

    public static LineDimensions None { get; } = new();
}

/// <summary>One line of a journal before it is written: positive amount, a side, transaction and functional amounts.</summary>
internal sealed record JournalLineDraft(int LineNo, string Account, string Side, Money Amount, Money FunctionalAmount, string RuleCode, LineDimensions Dimensions);

/// <summary>A journal ready to be numbered and written (REQ-FIN-067).</summary>
internal sealed record JournalDraft(
    string Book,
    BusinessDate AccountingDate,
    BusinessDate BusinessDate,
    string SourceType,
    Guid RuleSetId,
    int RuleSetVersion,
    IReadOnlyList<string> RuleCodes,
    IReadOnlyList<JournalLineDraft> Lines)
{
    /// <summary>Total debits per transaction currency (equal to total credits when balanced).</summary>
    public IReadOnlyList<Money> Totals =>
        [.. Lines.Where(l => l.Side == Sides.Debit).GroupBy(l => l.Amount.Currency).OrderBy(g => g.Key.Code, StringComparer.Ordinal)
            .Select(g => Money.Sum(g.Select(l => l.Amount), g.Key))];
}

/// <summary>The double-entry invariant and reversal (REQ-FIN-068, REQ-FIN-072), enforced in code before the database checks it again at commit.</summary>
internal static class Journals
{
    /// <summary>
    /// Problems that make a journal unpostable: fewer than two lines, a non-positive amount, an unknown side, a functional
    /// amount in another currency than the others, or debits ≠ credits in any transaction currency or in functional currency.
    /// </summary>
    public static IReadOnlyList<string> Check(IReadOnlyList<JournalLineDraft> lines)
    {
        var problems = new List<string>();
        if (lines.Count < 2)
        {
            problems.Add("A journal has at least two lines.");
        }

        foreach (var line in lines)
        {
            if (!line.Amount.IsPositive || !line.FunctionalAmount.IsPositive)
            {
                problems.Add($"Line {line.LineNo} must have a positive amount.");
            }

            if (line.Side is not (Sides.Debit or Sides.Credit))
            {
                problems.Add($"Line {line.LineNo} has an unknown side {line.Side}.");
            }
        }

        foreach (var currency in lines.Select(l => l.Amount.Currency).Distinct())
        {
            var (debit, credit) = Sums(lines.Where(l => l.Amount.Currency == currency).Select(l => (l.Side, l.Amount)), currency);
            if (debit != credit)
            {
                problems.Add($"Debits {debit} ≠ credits {credit} in {currency.Code}.");
            }
        }

        var functional = lines.Select(l => l.FunctionalAmount.Currency).Distinct().ToList();
        if (functional.Count > 1)
        {
            problems.Add("All functional amounts must be in one currency.");
        }
        else if (functional.Count == 1)
        {
            var (debit, credit) = Sums(lines.Select(l => (l.Side, l.FunctionalAmount)), functional[0]);
            if (debit != credit)
            {
                problems.Add($"Functional debits {debit} ≠ credits {credit}.");
            }
        }

        return problems;
    }

    /// <summary>The reversal of a posted journal: every line on the opposite side, same amounts and dimensions (REQ-FIN-072).</summary>
    public static IReadOnlyList<JournalLineDraft> Reverse(IReadOnlyList<JournalLineDraft> lines) =>
        [.. lines.Select(l => l with { Side = Sides.Opposite(l.Side) })];

    private static (Money Debit, Money Credit) Sums(IEnumerable<(string Side, Money Amount)> lines, Currency currency)
    {
        var all = lines.ToList();
        return (Money.Sum(all.Where(l => l.Side == Sides.Debit).Select(l => l.Amount), currency),
            Money.Sum(all.Where(l => l.Side == Sides.Credit).Select(l => l.Amount), currency));
    }
}
