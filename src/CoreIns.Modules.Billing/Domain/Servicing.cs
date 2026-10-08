using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Platform.Contracts.Common;

namespace CoreIns.Modules.Billing.Domain;

/// <summary>
/// The POL transaction kinds BIL understands (the MKT <c>TaxTransactionKind</c> codes, PRD-17 §7.5) and what each allows.
/// The code is stored on charges, items and ledger lines as the wire code (<c>ENDORSEMENT_CREDIT</c>).
/// </summary>
internal static class TransactionKinds
{
    public const string NewBusiness = "NEW_BUSINESS";
    public const string Cancellation = "CANCELLATION";
    public const string Void = "VOID";

    /// <summary>
    /// A servicing transaction: anything but new business (a renewal term is new business, contract note on
    /// <c>transactionKind</c>). The ledger dimensions <c>transactionKind</c>, <c>cancellationSource</c> and
    /// <c>treatmentRuleId</c> are always set on servicing entries and null otherwise (LedgerDimensions).
    /// </summary>
    public static bool IsServicing(string? code) => code is not null && !string.Equals(code, NewBusiness, StringComparison.Ordinal);

    /// <summary>Kinds whose premium lines may be negative (credits). A negative line under any other kind is quarantined.</summary>
    public static bool AllowsCredit(string code) => code is "ENDORSEMENT_CREDIT" or "CANCELLATION" or "DISTANCE_WITHDRAWAL_VOID" or "VOID" or "RETURN_PREMIUM";

    /// <summary>Kinds BIL accepts on a POL charge delta. REFUND is BIL's own kind for refunds (SL3-BIL-REFUND), never a POL delta.</summary>
    public static bool IsAccepted(string code) => code is "NEW_BUSINESS" or "ENDORSEMENT_DEBIT" or "ENDORSEMENT_CREDIT" or "CANCELLATION"
        or "DISTANCE_WITHDRAWAL_VOID" or "VOID" or "RETURN_PREMIUM" or "REINSTATEMENT" or "FEE";

    /// <summary>The treatment request needs a cancellation source for these kinds (REQ-MKT-330: missing → VALIDATION).</summary>
    public static bool NeedsCancellationSource(string code) => code is Cancellation or Void;

    /// <summary>The MKT kind of a stored code.</summary>
    public static TaxTransactionKind ToTax(string code) => code switch
    {
        "NEW_BUSINESS" => TaxTransactionKind.NewBusiness,
        "ENDORSEMENT_DEBIT" => TaxTransactionKind.EndorsementDebit,
        "ENDORSEMENT_CREDIT" => TaxTransactionKind.EndorsementCredit,
        "CANCELLATION" => TaxTransactionKind.Cancellation,
        "DISTANCE_WITHDRAWAL_VOID" => TaxTransactionKind.DistanceWithdrawalVoid,
        "VOID" => TaxTransactionKind.Void,
        "RETURN_PREMIUM" => TaxTransactionKind.ReturnPremium,
        "REINSTATEMENT" => TaxTransactionKind.Reinstatement,
        "FEE" => TaxTransactionKind.Fee,
        "REFUND" => TaxTransactionKind.Refund,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown transaction kind."),
    };

    /// <summary>The event dimension of a stored code.</summary>
    public static LedgerDimensions.TransactionKindValue ToDimension(string code) => code switch
    {
        "NEW_BUSINESS" => LedgerDimensions.TransactionKindValue.NewBusiness,
        "ENDORSEMENT_DEBIT" => LedgerDimensions.TransactionKindValue.EndorsementDebit,
        "ENDORSEMENT_CREDIT" => LedgerDimensions.TransactionKindValue.EndorsementCredit,
        "CANCELLATION" => LedgerDimensions.TransactionKindValue.Cancellation,
        "DISTANCE_WITHDRAWAL_VOID" => LedgerDimensions.TransactionKindValue.DistanceWithdrawalVoid,
        "VOID" => LedgerDimensions.TransactionKindValue.Void,
        "RETURN_PREMIUM" => LedgerDimensions.TransactionKindValue.ReturnPremium,
        "REINSTATEMENT" => LedgerDimensions.TransactionKindValue.Reinstatement,
        "FEE" => LedgerDimensions.TransactionKindValue.Fee,
        "REFUND" => LedgerDimensions.TransactionKindValue.Refund,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown transaction kind."),
    };
}

/// <summary>The servicing dimensions of a ledger line (always set on servicing entries, null otherwise).</summary>
internal static class ServicingDimensions
{
    /// <summary>Adds the transaction kind, cancellation source and treatment rule to <paramref name="dimensions"/> when the kind is a servicing kind.</summary>
    public static LineDimensions Apply(LineDimensions dimensions, string? kind, string? cancellationSource, string? treatmentRuleId) =>
        TransactionKinds.IsServicing(kind)
            ? dimensions with { TransactionKind = kind, CancellationSource = cancellationSource, TreatmentRuleId = treatmentRuleId }
            : dimensions;
}

/// <summary>Reason codes under which a delta (or the set it belongs to) is quarantined by the servicing checks.</summary>
internal static class QuarantineReasons
{
    public const string TransactionKindMissing = "TRANSACTION-KIND-MISSING";
    public const string TransactionKindUnsupported = "TRANSACTION-KIND-UNSUPPORTED";
    public const string KindAmountMismatch = "KIND-AMOUNT-MISMATCH";
    public const string CancellationSourceMissing = "CANCELLATION-SOURCE-MISSING";
    public const string TermCancelled = "TERM-CANCELLED";
    public const string TreatmentUnavailable = "TREATMENT-UNAVAILABLE";
    public const string TreatmentRuleMissing = "TREATMENT-RULE-MISSING";
    public const string TreatmentInvalid = "TREATMENT-INVALID";
    public const string TreatmentNotSettled = "TREATMENT-NOT-SETTLED";
    public const string TreatmentMismatch = "TREATMENT-MISMATCH";
    public const string TreatmentRuleMismatch = "TREATMENT-RULE-MISMATCH";
    public const string TreatmentRefMissing = "TREATMENT-REF-MISSING";
    public const string ProvisionalMismatch = "PROVISIONAL-FLAG-MISMATCH";
    public const string TaxCreditNotSupported = "TAX-CREDIT-NOT-SUPPORTED";
    public const string CreditExceedsBilled = "CREDIT-EXCEEDS-BILLED";
}

/// <summary>
/// REQ-BIL-079: the check of a consumed tax or levy delta against <c>TaxCalculator.treatment</c> (D2), pure and fail
/// closed. The delta must follow the result: what the customer is credited (the sign of the amount), the rule id and
/// version that decided it, and the provisional flag. Anything else is a mismatch and the whole set is quarantined.
/// </summary>
internal static class TreatmentCheck
{
    /// <summary>The quarantine reason when <paramref name="result"/> and the delta disagree; null when the delta follows the treatment.</summary>
    /// <param name="result">The treatment decided by MKT for this line.</param>
    /// <param name="amount">The delta amount (negative = credit).</param>
    /// <param name="ruleId">Rule id the delta carries (POL's <c>treatmentRuleId</c>).</param>
    /// <param name="ruleVersion">Rule version the delta carries.</param>
    /// <param name="provisional">The delta's provisional flag, as POL set it.</param>
    /// <param name="servicing">True for a servicing transaction, where the rule id and version are always set.</param>
    public static string? Mismatch(TaxTreatmentResult result, decimal amount, string? ruleId, string? ruleVersion, bool provisional, bool servicing)
    {
        ArgumentNullException.ThrowIfNull(result);

        // What the treatment lets the customer be credited decides the sign: APPLY charges, NONE credits nothing, the rest credit.
        var followsTreatment = result.Action switch
        {
            TreatmentAction.Apply => amount >= 0m,
            TreatmentAction.KeepNotReduced => amount == 0m,
            TreatmentAction.ReduceProRata or TreatmentAction.ReverseAsVoid or TreatmentAction.InsurerBears => amount <= 0m,
            _ => false,
        };
        if (!followsTreatment)
        {
            return QuarantineReasons.TreatmentMismatch;
        }

        if (servicing && (string.IsNullOrWhiteSpace(ruleId) || string.IsNullOrWhiteSpace(ruleVersion)))
        {
            return QuarantineReasons.TreatmentRefMissing;
        }

        if ((ruleId is not null && !string.Equals(ruleId, result.RuleId, StringComparison.Ordinal))
            || (ruleVersion is not null && !string.Equals(ruleVersion, result.RuleVersion, StringComparison.Ordinal)))
        {
            return QuarantineReasons.TreatmentRuleMismatch;
        }

        return provisional == result.Provisional ? null : QuarantineReasons.ProvisionalMismatch;
    }
}
