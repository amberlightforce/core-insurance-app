using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.CountryPacks.GR.Configuration;

/// <summary>
/// Data-only contribution of the Greece pack to the country layer: premium tax (IPT), the Auxiliary Fund contribution
/// readings and the IPT liability point, for motor (SL-MKT, E2E-01). Every value is taken from the PRDs and carries its
/// legal status and source (D-REG-01). What the PRDs do not state is absent and fails closed: the Auxiliary Fund
/// components, the insurer/policyholder split (D-REG-06a: 70/30 contradicts REQ-FIN-190), establishment condition, policyholder stamp-duty rate, mid-term base and cancellation treatment (REQ-MKT-322),
/// the stamp-duty rate (stated nowhere) and any road-safety or other levy (no PRD names one). Greek rounding rules
/// per tax class are also absent (PRD-17 section 16.5 decision 10: no Greek value is given).
/// </summary>
public sealed class GrPackConfiguration : IPackConfigurationSource
{
    private const string Ipt = "PRD-17 GR-01 / REQ-MKT-257 / section 9.4.4; Law 5177/2025 Art. 43 (peer-verified, PRD-06 S-3, PRD-09 S-07)";
    private const string AuxFund = "PRD-17 GR-02 / REQ-MKT-322 / REQ-MKT-331(b); Law 5113/2024 Art. 14 amending P.D. 237/1986 Art. 20";

    /// <summary>Version of this pack data (semantic, REQ-MKT-129).</summary>
    public const string Version = "0.1.0";

    /// <inheritdoc />
    public string PackId => GrPack.PackId;

    /// <inheritdoc />
    public string PackVersion => Version;

    /// <inheritdoc />
    public string Country => GrPack.Country;

    /// <inheritdoc />
    public IReadOnlyList<PackConfigValue> Values { get; } = [.. Core, .. GrTreatmentRules.Values];

    private static IReadOnlyList<PackConfigValue> Core { get; } =
    [
        // IPT: rates Settled (GR-01 "rates only"). The start of validity is not stated in the PRDs: open.
        new("tax.ipt.rate.general", ConfigValueType.ExactDecimal, "0.15", LegalStatus.Settled, Ipt + ": 15% general", MotorPath: true),
        new("tax.ipt.rate.fire", ConfigValueType.ExactDecimal, "0.20", LegalStatus.Settled, Ipt + ": 20% fire", MotorPath: false),
        new("tax.ipt.motor_class", ConfigValueType.Text, "general", LegalStatus.Verify,
            "PRD-17 REQ-MKT-309 example (premium 420.00 of which IPT 54.78 = 15% of 365.22); GR-01 names classes general and fire only",
            MotorPath: true,
            Note: "Reading: motor liability premium is taxed in class 'general'. The PRDs give no class table for motor; confirm with ROLE-26."),

        // IPT liability point: default DUE, Pending (REQ-MKT-328, 331a).
        new("tax.ipt.liability_point", ConfigValueType.Text, "DUE", LegalStatus.PendingOpinion,
            "PRD-17 REQ-MKT-328 / REQ-MKT-331(a); Law 5177/2025 Art. 43 (premiums due); OI-MKT-19, OI-FIN-03",
            MotorPath: true,
            Note: "Alternative WRITTEN selectable by data only after the D2 opinion."),

        // Auxiliary Fund: the GR-02 row is Pending opinion as a whole, so the 6% ceiling carries that status too.
        new("tax.levy.auxfund.ceiling_rate", ConfigValueType.ExactDecimal, "0.06", LegalStatus.PendingOpinion,
            AuxFund + ": 6% ceiling of gross written MTPL premium, adjustable by Bank of Greece decision",
            MotorPath: true),
        new("tax.levy.auxfund.split_basis", ConfigValueType.Text, "WHOLE_CEILING", LegalStatus.PendingOpinion,
            "PRD-17 REQ-MKT-322 provisional reading (MTPL 300.00 gives PH 5.40, INS 12.60)",
            MotorPath: true,
            Note: "Alternative COMPONENT_4_5 selectable by data after the opinion."),
        new("tax.levy.auxfund.base", ConfigValueType.Text, "WRITTEN_PREMIUM", LegalStatus.PendingOpinion,
            "PRD-17 REQ-MKT-322 (.base: written premium as defined of record under D5, unless the opinion says otherwise)",
            MotorPath: true),

        // Currency roles of the Greek entity (REQ-MKT-190: transaction default EUR, functional EUR). Not a statutory rule.
        new("cur.transaction", ConfigValueType.Text, "EUR", LegalStatus.NotRegulatory, "PRD-17 REQ-MKT-190 / section 10 (EUR)", MotorPath: false),
        new("cur.functional", ConfigValueType.Text, "EUR", LegalStatus.NotRegulatory, "PRD-17 REQ-MKT-190 / section 10 (cur.functional pack_required, final at L4)", MotorPath: false),
    ];
}
