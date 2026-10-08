using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Billing.Domain;

/// <summary>
/// Entry types of the billing sub-ledger (the <c>eventType</c> of <c>BillingEntryPosted</c>, REQ-BIL-011, REQ-BIL-313).
/// FIN posts journals only from these entries (D-SLC-12).
/// </summary>
internal static class EntryTypes
{
    /// <summary>Written state of one accepted charge delta (REQ-BIL-066).</summary>
    public const string Written = "WRITTEN";

    /// <summary>Accrual of an ACCRUED_NOT_BILLED charge (levy insurer share, REQ-BIL-066); not produced in the slice.</summary>
    public const string Accrued = "ACCRUED";

    /// <summary>Written unbilled → billed receivable when an invoice is billed (REQ-BIL-071).</summary>
    public const string Billed = "BILLED";

    /// <summary>IPT written-not-yet-due → IPT payable at the due date (liability point DUE, PRD-06 §4.13, LA-27 → LA-06).</summary>
    public const string IptDue = "IPT_DUE";

    /// <summary>
    /// Written state of a credit (negative delta) reversed: premium or fee clearing → written unbilled (PRD-06 §4.13 step 16,
    /// REQ-BIL-073). Never posts tax or levy: a tax or levy credit has no rule and is quarantined (D-SL3-05/06).
    /// </summary>
    public const string CreditWritten = "CREDIT_WRITTEN";

    /// <summary>A credit note billed: written unbilled → billed receivable credited (PRD-06 §4.13 step 17, REQ-BIL-074, REQ-BIL-091).</summary>
    public const string CreditBilled = "CREDIT_BILLED";

    /// <summary>Cash received into unapplied cash (REQ-BIL-126, REQ-BIL-131).</summary>
    public const string Received = "RECEIVED";

    /// <summary>Unapplied cash allocated to billed receivable (REQ-BIL-129, REQ-BIL-130).</summary>
    public const string Allocated = "ALLOCATED";

    /// <summary>A disbursement released to the bank: source payable or clearing (LA-17 for claim payments) → disbursements in transit LA-13 (REQ-BIL-211).</summary>
    public const string DisbursementReleased = "DISBURSEMENT_RELEASED";

    /// <summary>A disbursement's statement debit: disbursements in transit LA-13 → cash at bank LA-10 (REQ-BIL-211).</summary>
    public const string DisbursementCleared = "DISBURSEMENT_CLEARED";
}

/// <summary>Sub-ledger accounts used by the slice (PRD-06 §7.1.4; the whole chart LA-01…LA-27 is seeded as data, REQ-BIL-282).</summary>
internal static class LedgerAccounts
{
    public const string WrittenUnbilled = "LA-01";
    public const string BilledReceivable = "LA-02";
    public const string IptPayable = "LA-06";
    public const string CashAtBank = "LA-10";
    public const string Suspense = "LA-11";
    public const string RefundsPayable = "LA-12";
    public const string DisbursementsInTransit = "LA-13";
    public const string ClaimPaymentsClearing = "LA-17";
    public const string IptWrittenNotDue = "LA-27";
}

/// <summary>Qualifiers of billing-ledger rules (the IPT liability point, MKT key <c>tax.ipt.liability_point</c>).</summary>
internal static class RuleQualifiers
{
    public const string Any = "*";
    public const string IptLiabilityDue = "IPT_LIABILITY_DUE";
    public const string IptLiabilityWritten = "IPT_LIABILITY_WRITTEN";

    /// <summary>The qualifier of a liability point value served by MKT; null when the value is not one the rules know.</summary>
    public static string? ForLiabilityPoint(string? value) => value switch
    {
        "DUE" => IptLiabilityDue,
        "WRITTEN" => IptLiabilityWritten,
        _ => null,
    };
}

/// <summary>Charge categories the ledger rules distinguish (PFC catalogue categories).</summary>
internal static class ChargeCategories
{
    public const string Tax = "TAX";
}

/// <summary>Debit or credit.</summary>
internal enum LedgerSide
{
    Debit,
    Credit,
}

/// <summary>
/// One row of the billing-ledger rule table (REQ-BIL-286): key event type × charge category × bill mode × jurisdiction
/// (× qualifier), naming a debit and a credit account and an amount expression. <c>*</c> is a wildcard.
/// </summary>
internal sealed record LedgerRule(
    string RuleId,
    int Version,
    string EventType,
    string ChargeCategory,
    string BillMode,
    string Jurisdiction,
    string Qualifier,
    string DebitAccount,
    string CreditAccount,
    string AmountExpression);

/// <summary>The key of a posting looked up in the rule table.</summary>
internal sealed record PostingKey(string EventType, string ChargeCategory, string BillMode, string Jurisdiction, string Qualifier);

/// <summary>Dimensions of a ledger line (REQ-BIL-283); not-applicable dimensions are null by rule.</summary>
internal sealed record LineDimensions
{
    public BillingAccountId? BillingAccountId { get; init; }

    public PolicyId? PolicyId { get; init; }

    public PolicyTermId? PolicyTermId { get; init; }

    public PolicyTransactionId? TransactionId { get; init; }

    public ChargeId? ChargeId { get; init; }

    public string? ChargeType { get; init; }

    public string? ChargeCategory { get; init; }

    public string? CoverageCode { get; init; }

    public string? ProductCode { get; init; }

    public string? BillMode { get; init; }

    public InvoiceId? InvoiceId { get; init; }

    public Guid? InvoiceItemId { get; init; }

    public PaymentId? ReceiptId { get; init; }

    public Guid? AllocationId { get; init; }

    public DisbursementId? DisbursementId { get; init; }

    /// <summary>Disbursement source type (REQ-BIL-354).</summary>
    public string? SourceType { get; init; }

    /// <summary>Id of the disbursement's source object.</summary>
    public string? SourceId { get; init; }

    public ClaimId? ClaimId { get; init; }

    /// <summary>Transaction kind code of the POL transaction (MKT TaxTransactionKind); set on servicing entries, null otherwise (contract LedgerDimensions).</summary>
    public string? TransactionKind { get; init; }

    /// <summary>Cancellation source; set on lines of cancellation-sourced entries, null otherwise.</summary>
    public string? CancellationSource { get; init; }

    /// <summary>Treatment rule of a tax or levy line of a servicing entry, null otherwise.</summary>
    public string? TreatmentRuleId { get; init; }
}

/// <summary>One leg to post: an amount under a rule, with the line dimensions.</summary>
internal sealed record PostingLeg(LedgerRule Rule, Money Amount, LineDimensions Dimensions);

/// <summary>A ledger line before it is stored.</summary>
internal sealed record DraftLine(string Account, LedgerSide Side, Money Amount, string RuleId, LineDimensions Dimensions);

/// <summary>
/// Pure posting logic: rule matching (exactly one most specific match, never a default account, REQ-BIL-287), the
/// amount expression (only <c>AMOUNT</c> until the CEL-compatible expression engine is decided, D10), and the
/// double-entry lines of a set of legs, balanced per currency (REQ-BIL-280).
/// </summary>
internal static class Posting
{
    /// <summary>The only amount expression the slice evaluates: the leg amount itself.</summary>
    public const string AmountExpression = "AMOUNT";

    /// <summary>The single most specific rule matching <paramref name="key"/>; BIL-ERR-NO-RULE when none or several.</summary>
    public static Result<LedgerRule> Match(IReadOnlyList<LedgerRule> rules, PostingKey key)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(key);
        var candidates = rules
            .Where(r => r.EventType == key.EventType
                        && Fits(r.ChargeCategory, key.ChargeCategory)
                        && Fits(r.BillMode, key.BillMode)
                        && Fits(r.Jurisdiction, key.Jurisdiction)
                        && Fits(r.Qualifier, key.Qualifier))
            .GroupBy(Specificity)
            .OrderByDescending(g => g.Key)
            .FirstOrDefault()?
            .ToList();
        return candidates switch
        {
            null or { Count: 0 } => NoRule($"No billing-ledger rule matches {Describe(key)} (REQ-BIL-287)."),
            { Count: > 1 } => NoRule($"{candidates.Count} billing-ledger rules match {Describe(key)} equally; exactly one must (REQ-BIL-286)."),
            _ => candidates[0],
        };
    }

    /// <summary>Evaluates the rule's amount expression on the leg amount; refuses expressions it does not know.</summary>
    public static Result<Money> Amount(LedgerRule rule, Money amount)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return string.Equals(rule.AmountExpression, AmountExpression, StringComparison.Ordinal)
            ? amount
            : NoRule($"Rule {rule.RuleId} uses amount expression '{rule.AmountExpression}', which this release cannot evaluate (D10 open).");
    }

    /// <summary>
    /// The lines of an entry: a debit and a credit per leg with a non-zero amount. Amounts must be positive and rounded
    /// to minor units (a negative leg is a programming error: credits post through reversal rules, REQ-BIL-288).
    /// </summary>
    public static IReadOnlyList<DraftLine> Lines(IEnumerable<PostingLeg> legs)
    {
        ArgumentNullException.ThrowIfNull(legs);
        var lines = new List<DraftLine>();
        foreach (var leg in legs)
        {
            if (leg.Amount.IsZero)
            {
                continue;
            }

            if (leg.Amount.IsNegative || !leg.Amount.IsRoundedToMinorUnits)
            {
                throw new InvalidOperationException($"Ledger amounts are positive and rounded to minor units; got {leg.Amount} under {leg.Rule.RuleId}.");
            }

            lines.Add(new DraftLine(leg.Rule.DebitAccount, LedgerSide.Debit, leg.Amount, leg.Rule.RuleId, leg.Dimensions));
            lines.Add(new DraftLine(leg.Rule.CreditAccount, LedgerSide.Credit, leg.Amount, leg.Rule.RuleId, leg.Dimensions));
        }

        return lines;
    }

    /// <summary>True when debits equal credits in every currency (the same invariant the database enforces at commit).</summary>
    public static bool IsBalanced(IEnumerable<DraftLine> lines) =>
        lines.GroupBy(l => l.Amount.Currency)
            .All(g => g.Sum(l => l.Side == LedgerSide.Debit ? l.Amount.Amount : -l.Amount.Amount) == 0m);

    private static bool Fits(string pattern, string value) => pattern == RuleQualifiers.Any || string.Equals(pattern, value, StringComparison.Ordinal);

    private static int Specificity(LedgerRule rule) =>
        new[] { rule.ChargeCategory, rule.BillMode, rule.Jurisdiction, rule.Qualifier }.Count(p => p != RuleQualifiers.Any);

    private static string Describe(PostingKey key) =>
        $"{key.EventType} / {key.ChargeCategory} / {key.BillMode} / {key.Jurisdiction} / {key.Qualifier}";

    private static DomainError NoRule(string detail) => DomainError.Of(ModuleCode.BIL, "NO-RULE", detail);
}
