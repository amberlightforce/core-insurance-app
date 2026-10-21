using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.CountryPacks.GR.Configuration;

/// <summary>
/// Greece <c>TaxCalculator.treatment</c> rule rows (D2, PRD-17 REQ-MKT-330/331, section 9.4.4). All rows are legal status
/// PendingOpinion: they are provisional outside Production and refused in Production (D-REG-02, D-SLC-09) until the D2 tax
/// and legal opinion settles them. Rows exist for the IPT charge category and, since NX-HD-06, for the levy lines of a distance-withdrawal void only. Other levy
/// kinds and the stamp category have no row, and so do cancellation sources other than Policyholder and NonPayment (D-SL6-15): the PRDs give no refundability or reduction for them, so
/// <c>treatment</c> returns RULE_MISSING and a credit carrying such a line fails closed (D-SL3-06). Key scheme:
/// <c>tax.treatment.rule.&lt;CATEGORY&gt;.&lt;KIND&gt;.&lt;SOURCE|ANY&gt;</c>.
/// </summary>
internal static class GrTreatmentRules
{
    private const string RuleVersion = "0.1.0";

    private const string Keep = "PRD-17 REQ-MKT-331 (D2 Greece treatment defaults); ΠΟΛ 1028/2017 (applicability after Law 5177/2025 unverified, F-218); legalStatus PendingOpinion (OI-MKT-19)";
    private const string Apply = "PRD-17 REQ-MKT-331 (D2 Greece treatment defaults); Law 5177/2025 Art. 43; legalStatus PendingOpinion (OI-MKT-19)";
    private const string Void = "PRD-17 REQ-MKT-331 (D2 Greece treatment defaults); Law 5317/2026 Art. 72 (full refund including tax and levy lines on distance withdrawal); legalStatus PendingOpinion (OI-MKT-19)";

    /// <summary>
    /// F-401 / D-SL6-02: the customer is refunded in full (secondary source Law 5317/2026 Art. 72, legalStatus SECONDARY_SOURCE); only the
    /// insurer's tax position is open (tax_position TAX_OPINION_PENDING). The pack enum keeps PendingOpinion (provisional outside Production).
    /// </summary>
    private const string WithdrawalNote = "legalStatus SECONDARY_SOURCE (Law 5317/2026 Art. 72); tax_position TAX_OPINION_PENDING (F-401, D-SL6-02): customer refunded in full, insurer tax position open.";

    public static IReadOnlyList<PackConfigValue> Values { get; } =
    [
        Row("CANCELLATION", "Policyholder", "KEEP_NOT_REDUCED", "GR-TRT-IPT-CANCEL-POLICYHOLDER", Keep),
        Row("CANCELLATION", "NonPayment", "KEEP_NOT_REDUCED", "GR-TRT-IPT-CANCEL-NONPAYMENT", Keep),
        Row("ENDORSEMENT_CREDIT", "ANY", "KEEP_NOT_REDUCED", "GR-TRT-IPT-ENDORSEMENT-CREDIT", Keep),
        Row("RETURN_PREMIUM", "ANY", "KEEP_NOT_REDUCED", "GR-TRT-IPT-RETURN-PREMIUM", Keep),
        Row("REFUND", "ANY", "KEEP_NOT_REDUCED", "GR-TRT-IPT-REFUND", Keep),
        Row("NEW_BUSINESS", "ANY", "APPLY", "GR-TRT-IPT-NEW-BUSINESS", Apply),
        Row("ENDORSEMENT_DEBIT", "ANY", "APPLY", "GR-TRT-IPT-ENDORSEMENT-DEBIT", Apply),
        Row("FEE", "ANY", "APPLY", "GR-TRT-IPT-FEE", Apply),
        Row("VOID", "DistanceWithdrawal", "REVERSE_AS_VOID", "GR-TRT-IPT-VOID-DISTANCE-WITHDRAWAL", Void),
        Row("DISTANCE_WITHDRAWAL_VOID", "ANY", "REVERSE_AS_VOID", "GR-TRT-IPT-WITHDRAWAL-VOID", Void),

        // NX-HD-06: levy lines of a distance-withdrawal void are refunded in full too (Law 5317/2026 Art. 72). Only these two
        // kinds have a levy row; every other levy credit stays RULE_MISSING and fails closed (D-SL3-06). PendingOpinion: refused in Production.
        Row("VOID", "DistanceWithdrawal", "REVERSE_AS_VOID", "GR-TRT-LEVY-VOID-DISTANCE-WITHDRAWAL", Void, "LEVY"),
        Row("DISTANCE_WITHDRAWAL_VOID", "ANY", "REVERSE_AS_VOID", "GR-TRT-LEVY-WITHDRAWAL-VOID", Void, "LEVY"),
    ];

    private static PackConfigValue Row(string kind, string source, string action, string ruleId, string sourceRef, string category = "TAX") =>
        new($"tax.treatment.rule.{category}.{kind}.{source}", ConfigValueType.Json,
            $$"""{"action":"{{action}}","ruleId":"{{ruleId}}","ruleVersion":"{{RuleVersion}}"}""",
            LegalStatus.PendingOpinion, sourceRef, MotorPath: true,
            Note: kind == "DISTANCE_WITHDRAWAL_VOID"
                ? "Alternative INSURER_BEARS selectable via tax.treatment.withdrawal_void_routing (registered, not used by the slice). " + WithdrawalNote
                : kind == "CANCELLATION" && source == "NonPayment" ? "mirrors Policyholder; TAX_OPINION_PENDING (D-SL6-15)"
                : kind == "VOID" && source == "DistanceWithdrawal" ? WithdrawalNote : null);
}
