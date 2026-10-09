namespace CoreIns.Modules.Market.Contracts.Spi;

/// <summary>
/// SPI 4 <c>TaxCalculator</c> (contract §3.5.8; PRD-17 §9.4.4; spi.md §4): taxes, levies and stamps as charge types,
/// and the <c>treatment</c> of each line on credits, cancellations and voids (D2, REQ-MKT-330). Binding axis
/// RISK_LOCATION (+ subdivision). Mode S, pure (REQ-MKT-117): rates from pack data, rounding through
/// <c>mkt.Rounding.apply</c> (REQ-MKT-193, 194). Timeout 50 ms (treatment 20 ms); fail closed. Core default: none.
/// Single-call rule (REQ-MKT-332): the originating module calls <c>calculate</c> once per charge; every tax line carries
/// the rule id and version. Errors: VALIDATION (missing tax class; missing cancellation source for CANCELLATION or
/// VOID), RULE_MISSING (no rate or treatment rule for the date), NOT_APPLICABLE.
/// Types only in F-1e: rates and treatment rules are pack data delivered by W1-MKT with their legal status (D-REG-01).
/// </summary>
public interface ITaxCalculator
{
    /// <summary><c>calculate(request) → result</c>.</summary>
    ValueTask<TaxCalculationResult> CalculateAsync(
        TaxCalculationRequest request, CancellationToken cancellationToken = default);

    /// <summary><c>treatment(request) → result</c> (D2, REQ-MKT-330). One schema for every caller (FZ-03).</summary>
    ValueTask<TaxTreatmentResult> TreatmentAsync(
        TaxTreatmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Optional <c>calculateReinsurancePremiumTax(riPremiumLines, jurisdiction, date)</c> (R-49, §9.4.43). Default
    /// NOT_APPLICABLE.
    /// </summary>
    ValueTask<TaxCalculationResult> CalculateReinsurancePremiumTaxAsync(
        IReadOnlyList<TaxChargeLine> riPremiumLines, string jurisdiction, DateOnly validAt,
        CancellationToken cancellationToken = default) =>
        throw new SpiException(
            new SpiError(SpiErrorCategory.NotApplicable, "RI_PREMIUM_TAX_NOT_BOUND"),
            "calculateReinsurancePremiumTax is not provided by this pack.");
}

/// <summary>Tax line category (PRD-17 §9.4.4).</summary>
public enum TaxCategory
{
    Tax,
    Levy,
    Stamp,
}

/// <summary>Policyholder type of the request.</summary>
public enum PolicyholderType
{
    Consumer,
    Business,
}

/// <summary>Module that originated the charge (single-call rule, REQ-MKT-332).</summary>
public enum ChargeOrigin
{
    Pol,
    Bil,
}

/// <summary>Request of <c>calculate</c> (PRD-17 §9.4.4).</summary>
public sealed record TaxCalculationRequest
{
    public required Guid LegalEntityId { get; init; }

    /// <summary>ISO 3166-1 risk jurisdiction.</summary>
    public required string RiskJurisdiction { get; init; }

    /// <summary>ISO 3166-2 subdivision, when the pack taxes by subdivision.</summary>
    public string? RiskSubdivision { get; init; }

    public required DateOnly TaxPointDate { get; init; }

    public required IReadOnlyList<TaxChargeLine> ChargeLines { get; init; }

    public required PolicyholderType PolicyholderType { get; init; }

    /// <summary>Business basis (for example establishment or freedom of services), code-list value.</summary>
    public required string BusinessBasis { get; init; }

    /// <summary>
    /// The action <c>treatment</c> returned for this charge (single-call rule, REQ-MKT-332). <c>calculate</c> prices only Apply,
    /// ReduceProRata and ReverseAsVoid; null is read as Apply. A credit (negative) base is accepted only for ReduceProRata or
    /// ReverseAsVoid, so a credit that treatment would KEEP never receives a credit tax.
    /// </summary>
    public TreatmentAction? TreatmentAction { get; init; }
}

/// <summary>Kind of a charge line to tax (PRD-17 section 9.4.4). IPT is priced only on Premium.</summary>
public enum ChargeLineCategory
{
    Premium,
    Fee,
    Tax,
    Levy,
    Stamp,
}

/// <summary>A charge line to tax (PRD-17 §9.4.4).</summary>
public sealed record TaxChargeLine
{
    /// <summary>Element (coverage or fee) reference.</summary>
    public required string Element { get; init; }

    public required string ChargeType { get; init; }

    /// <summary>
    /// Kind of the charge. Allow-list: only Premium is taxed with IPT; every other kind has no rate row and is RULE_MISSING (fail closed).
    /// Null is a VALIDATION error: the kind is never guessed from the charge type name.
    /// </summary>
    public ChargeLineCategory? ChargeCategory { get; init; }

    public required string ProductLine { get; init; }

    /// <summary>Tax class of the element; missing → VALIDATION.</summary>
    public required string TaxClass { get; init; }

    public required SpiMoney PremiumAmount { get; init; }

    public required DateOnly PeriodStart { get; init; }

    public required DateOnly PeriodEnd { get; init; }

    public required string TransactionType { get; init; }

    /// <summary>Unit counts such as vehicles (keys are pack-defined unit codes).</summary>
    public IReadOnlyDictionary<string, int> UnitCounts { get; init; } = new Dictionary<string, int>();
}

/// <summary>Result of <c>calculate</c>.</summary>
/// <param name="Lines">Tax lines per element.</param>
/// <param name="DocumentLines">Document-level lines (for example per-policy or per-receipt stamps).</param>
public sealed record TaxCalculationResult(IReadOnlyList<TaxLine> Lines, IReadOnlyList<TaxLine> DocumentLines);

/// <summary>A calculated tax line (PRD-17 §9.4.4).</summary>
public sealed record TaxLine
{
    /// <summary>Element reference; null for document-level lines.</summary>
    public string? Element { get; init; }

    public required string ChargeType { get; init; }

    public required TaxCategory Category { get; init; }

    public required string TaxClass { get; init; }

    public required SpiMoney Base { get; init; }

    /// <summary>Rate as a decimal fraction (0.01 = 1 %); null when a fixed amount applies.</summary>
    public decimal? Rate { get; init; }

    public SpiMoney? FixedAmount { get; init; }

    public required SpiMoney Amount { get; init; }

    public required string RoundingRuleId { get; init; }

    public required string RuleId { get; init; }

    public required string RuleVersion { get; init; }

    public required string LegalSourceRef { get; init; }

    /// <summary>Legal status of the value used (D-REG-01).</summary>
    public required LegalStatus LegalStatus { get; init; }

    /// <summary>SHA-256 of the configuration the rate was resolved under (PRD-17 section 7.5). Optional so existing producers compile.</summary>
    public string? ConfigurationHash { get; init; }

    /// <summary>True when <see cref="LegalStatus"/> is anything but Settled; such a line is provisional (D-REG-02, PITFALLS 36).</summary>
    public bool Provisional => LegalStatus != LegalStatus.Settled;
}

/// <summary>Transaction kind of a treatment request (PRD-17 §9.4.4).</summary>
public enum TaxTransactionKind
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

/// <summary>Request of <c>treatment</c> (D2, REQ-MKT-330).</summary>
public sealed record TaxTreatmentRequest
{
    public required Guid LegalEntityId { get; init; }

    public required string RiskJurisdiction { get; init; }

    public string? RiskSubdivision { get; init; }

    public required DateOnly TaxPointDate { get; init; }

    public required string ChargeType { get; init; }

    public required TaxCategory Category { get; init; }

    public required ChargeOrigin ChargeOrigin { get; init; }

    public required TaxTransactionKind TransactionKind { get; init; }

    /// <summary>Cancellation source (shared code list, R-84); required for CANCELLATION and VOID.</summary>
    public string? CancellationSource { get; init; }

    public required PolicyholderType PolicyholderType { get; init; }

    public required string BusinessBasis { get; init; }
}

/// <summary>Treatment action (REQ-MKT-330).</summary>
public enum TreatmentAction
{
    Apply,
    ReduceProRata,
    ReverseAsVoid,
    KeepNotReduced,
    InsurerBears,
}

/// <summary>Customer credit of a treatment.</summary>
public enum CustomerCredit
{
    ProRata,
    Full,
    None,
}

/// <summary>Authority liability of a treatment.</summary>
public enum AuthorityLiability
{
    Reduce,
    NotReduce,
}

/// <summary>Fiscal document of a treatment.</summary>
public enum FiscalDocumentTreatment
{
    CreditNote,
    None,
}

/// <summary>Legal status of a treatment rule (PRD-17 §9.4.4: Settled or Pending the D2 opinion).</summary>
public enum TreatmentLegalStatus
{
    Settled,
    Pending,
}

/// <summary>Result of <c>treatment</c>. <c>Action</c> is consistent with the three detail fields by definition.</summary>
public sealed record TaxTreatmentResult
{
    public required string ChargeType { get; init; }

    public required TreatmentAction Action { get; init; }

    public required CustomerCredit CustomerCredit { get; init; }

    public required AuthorityLiability AuthorityLiability { get; init; }

    public required FiscalDocumentTreatment FiscalDocument { get; init; }

    public required string RuleId { get; init; }

    public required string RuleVersion { get; init; }

    public required TreatmentLegalStatus LegalStatus { get; init; }

    public required string LegalSourceRef { get; init; }

    /// <summary>
    /// SHA-256 of the configuration the rule was resolved under (PRD-17 §7.5: every result row carries the
    /// configuration hash). Optional so existing producers compile; MKT sets it on every result from slice 3.
    /// </summary>
    public string? ConfigurationHash { get; init; }

    /// <summary>True when <see cref="LegalStatus"/> is not Settled; such a result is provisional (D-REG-02).</summary>
    public bool Provisional => LegalStatus != TreatmentLegalStatus.Settled;
}
