using CoreIns.SharedKernel;

namespace CoreIns.Modules.Rating.Contracts.Servicing;

// SL3-RAT-PRORATE: tax lines for servicing premium deltas (REQ-RAT-009 subset, REQ-POL-124, D-SL3-05). The treatment of a
// credit comes only from TaxCalculator.treatment (REQ-MKT-330/331); RAT never decides it. Hand-written until SL3-CONTRACTS
// types the operation (see ProrationContract.cs).

/// <summary>Transaction kind of a delta, as the TaxCalculator treatment request knows it (PRD-17 §9.4.4).</summary>
public enum ServicingTransactionKind
{
    NewBusiness,
    EndorsementDebit,
    EndorsementCredit,
    Cancellation,
    DistanceWithdrawalVoid,
    Void,
    ReturnPremium,
    Reinstatement,
    Fee,
    Refund,
}

/// <summary>Category of the tax charge to compute on a delta.</summary>
public enum ServicingTaxCategory
{
    Tax,
    Levy,
    Stamp,
}

/// <summary>Treatment action RAT reports back (REQ-MKT-330).</summary>
public enum ServicingTreatmentAction
{
    Apply,
    ReduceProRata,
    ReverseAsVoid,
    KeepNotReduced,
    InsurerBears,
}

/// <summary>What the customer is credited (REQ-MKT-330).</summary>
public enum ServicingCustomerCredit
{
    ProRata,
    Full,
    None,
}

/// <summary>Whether the authority liability is reduced (REQ-MKT-330).</summary>
public enum ServicingAuthorityLiability
{
    Reduce,
    NotReduce,
}

/// <summary>Fiscal document of the treatment (REQ-MKT-330).</summary>
public enum ServicingFiscalDocument
{
    CreditNote,
    None,
}

/// <summary>A premium delta to tax. The caller supplies one item per (delta x tax charge type) it wants a line for.</summary>
/// <param name="DeltaRef">Caller's reference, echoed back.</param>
/// <param name="Element">Element (coverage) the delta belongs to.</param>
/// <param name="PremiumChargeType">Charge type of the premium delta.</param>
/// <param name="TaxChargeType">Tax charge type to compute (for example GR-IPT).</param>
/// <param name="Category">Tax, levy or stamp.</param>
/// <param name="TaxClass">Tax class of the element; required.</param>
/// <param name="Delta">Signed premium delta (credit negative).</param>
/// <param name="PeriodFrom">Start of the delta's period.</param>
/// <param name="PeriodTo">End (exclusive) of the delta's period.</param>
/// <param name="TransactionKind">Decides the treatment.</param>
/// <param name="CancellationSource">Required for Cancellation and Void.</param>
public sealed record ServicingDelta(
    string DeltaRef,
    string Element,
    string PremiumChargeType,
    string TaxChargeType,
    ServicingTaxCategory Category,
    string TaxClass,
    Money Delta,
    BusinessDate PeriodFrom,
    BusinessDate PeriodTo,
    ServicingTransactionKind TransactionKind,
    string? CancellationSource = null);

/// <summary>Request for the tax lines of a list of deltas. All-or-nothing.</summary>
/// <param name="RiskJurisdiction">ISO 3166-1 risk jurisdiction.</param>
/// <param name="RiskSubdivision">ISO 3166-2 subdivision, when the pack taxes by subdivision.</param>
/// <param name="TaxPointDate">Tax point of the transaction.</param>
/// <param name="TransactionType">Transaction type string passed to the calculator (for example ENDORSEMENT).</param>
/// <param name="ProductLine">Product line code passed to the calculator (for example MOTOR).</param>
/// <param name="Deltas">The deltas.</param>
/// <param name="PolicyholderIsBusiness">False (consumer) by default.</param>
/// <param name="BusinessBasis">Business basis code-list value (for example ESTABLISHMENT).</param>
public sealed record ServicingTaxLinesRequest(
    string RiskJurisdiction,
    string? RiskSubdivision,
    BusinessDate TaxPointDate,
    string TransactionType,
    string ProductLine,
    IReadOnlyList<ServicingDelta> Deltas,
    bool PolicyholderIsBusiness = false,
    string BusinessBasis = "ESTABLISHMENT");

/// <summary>One tax line of a delta.</summary>
/// <param name="CalculationRuleId">Rule of the calculation; null when no calculation ran (KEEP_NOT_REDUCED, INSURER_BEARS).</param>
/// <param name="CustomerCredit">What the customer is credited, from the treatment.</param>
/// <param name="AuthorityLiability">Whether the authority liability is reduced, from the treatment.</param>
/// <param name="FiscalDocument">Fiscal document of the treatment.</param>
/// <param name="LegalStatus">Weakest status of calculation and treatment (PendingOpinion beats Settled; NotRegulatory is never reported as Settled).</param>
/// <param name="Provisional">True unless the weakest status is exactly Settled.</param>
public sealed record ServicingTaxLine(
    string DeltaRef,
    string Element,
    string TaxChargeType,
    ServicingTaxCategory Category,
    string TaxClass,
    Money Base,
    decimal? Rate,
    Money Amount,
    ServicingTreatmentAction TreatmentAction,
    ServicingCustomerCredit CustomerCredit,
    ServicingAuthorityLiability AuthorityLiability,
    ServicingFiscalDocument FiscalDocument,
    string? CalculationRuleId,
    string? CalculationRuleVersion,
    string TreatmentRuleId,
    string TreatmentRuleVersion,
    string LegalStatus,
    bool Provisional,
    string? LegalSourceRef);

/// <summary>Result: one line per delta, in request order.</summary>
public sealed record ServicingTaxLinesResult(IReadOnlyList<ServicingTaxLine> Lines);

/// <summary>Tax lines for servicing deltas. Errors: <c>RAT-ERR-TAX</c> (RULE_MISSING or anything the calculator refuses, no partial output), <c>RAT-ERR-INPUT</c>.</summary>
public interface IRatingServicingTax
{
    /// <summary>Computes the tax line of every delta through TaxCalculator.treatment (and calculate where the treatment applies a tax).</summary>
    Task<ServicingTaxLinesResult> TaxLinesAsync(ServicingTaxLinesRequest request, CancellationToken cancellationToken = default);
}
