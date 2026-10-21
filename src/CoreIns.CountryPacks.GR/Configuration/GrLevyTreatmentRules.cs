using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.CountryPacks.GR.Configuration;

/// <summary>
/// The levy treatment rows added by pack version 0.3.0 (NX-HD-06; a shipped version never changes, REQ-MKT-129, D-SL5-07): the levy lines
/// of a distance-withdrawal void are refunded in full too (Law 5317/2026 Art. 72). Only these two kinds have a levy row; every other levy
/// credit stays RULE_MISSING and fails closed (D-SL3-06). PendingOpinion: refused in Production (D-REG-02). Key scheme as
/// <see cref="GrTreatmentRules"/>: <c>tax.treatment.rule.LEVY.&lt;KIND&gt;.&lt;SOURCE|ANY&gt;</c>.
/// </summary>
internal static class GrLevyTreatmentRules
{
    private const string RuleVersion = "0.1.0";

    private const string Void = "PRD-17 REQ-MKT-331 (D2 Greece treatment defaults); Law 5317/2026 Art. 72 (full refund including tax and levy lines on distance withdrawal); legalStatus PendingOpinion (OI-MKT-19)";

    private const string WithdrawalNote = "legalStatus SECONDARY_SOURCE (Law 5317/2026 Art. 72); tax_position TAX_OPINION_PENDING (F-401, D-SL6-02): customer refunded in full, insurer tax position open.";

    public static IReadOnlyList<PackConfigValue> Values { get; } =
    [
        Row("VOID", "DistanceWithdrawal", "GR-TRT-LEVY-VOID-DISTANCE-WITHDRAWAL", WithdrawalNote),
        Row("DISTANCE_WITHDRAWAL_VOID", "ANY", "GR-TRT-LEVY-WITHDRAWAL-VOID",
            "Alternative INSURER_BEARS selectable via tax.treatment.withdrawal_void_routing (registered, not used by the slice). " + WithdrawalNote),
    ];

    private static PackConfigValue Row(string kind, string source, string ruleId, string note) =>
        new($"tax.treatment.rule.LEVY.{kind}.{source}", ConfigValueType.Json,
            $$"""{"action":"REVERSE_AS_VOID","ruleId":"{{ruleId}}","ruleVersion":"{{RuleVersion}}"}""",
            LegalStatus.PendingOpinion, Void, MotorPath: true, Note: note);
}
