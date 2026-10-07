namespace CoreIns.Modules.Rating.Domain;

/// <summary>
/// The rating artefact of the thin end-to-end slice for the PFC product MOTOR-GR 1.0 (slot MOTOR-GR-RATING, coverages MTPL,
/// OWN-DAMAGE, WINDSCREEN, charge types PREM-MTPL, PREM-OD, PREM-WINDSCREEN and GR-IPT).
/// <b>Every number in these tables is illustrative test data</b> (D-SLC-04): it was made up to exercise the engine and is
/// not a tariff, not an actuarial result and not approved by anyone. The artefact metadata, the worksheet header and a
/// response warning all say so. Tax rates are not here at all: they come from MKT configuration.
/// </summary>
internal static class BuiltInArtefacts
{
    public const string DefaultProductCode = "MOTOR-GR";
    public const string DefaultProductVersion = "1.0";
    public const string AlgorithmCode = "MOTOR-GR-RATING";
    public const string EffectiveFrom = "2026-01-01";
    public const string IllustrativeNote =
        "ILLUSTRATIVE TEST DATA. These rates are made up for development and tests; they are not an approved tariff and must never be used to price real policies.";

    public static IReadOnlyList<TableDto> Tables() => [BaseRate(), DriverAgeFactor(), VehicleAgeFactor(), ClaimsFactor(), MinimumPremium()];

    /// <summary>The artefact definition for a product, pinning the given table hashes (code to hash).</summary>
    public static ArtefactDefinition Definition(string productCode, string productVersion, IReadOnlyDictionary<string, string> tableHashes) => new(
        "rat.artefact/1",
        AlgorithmCode,
        "1.0.0-illustrative",
        productCode,
        productVersion,
        EngineVersion.Current,
        new ArtefactMetadata(DataStatus.Illustrative, NotATariff: true, IllustrativeNote),
        "EUR",
        AnnualTermsOnly: true,
        [
            new StepDto("BASE_RATE", "BASE", "BASE_RATE", "Base premium", "Βασικό ασφάλιστρο",
                "Starting premium for the coverage from the base table: a fixed amount, or a rate times the vehicle value.",
                "Αρχικό ασφάλιστρο της κάλυψης από τον πίνακα βάσης: σταθερό ποσό ή ποσοστό επί της αξίας του οχήματος."),
            new StepDto("DRIVER_AGE", "FACTOR", "DRIVER_AGE_FACTOR", "Driver age", "Ηλικία οδηγού",
                "Multiplied by the factor for the age of the driver on the effective date.",
                "Πολλαπλασιάζεται με τον συντελεστή για την ηλικία του οδηγού κατά την ημερομηνία ισχύος."),
            new StepDto("VEHICLE_AGE", "FACTOR", "VEHICLE_AGE_FACTOR", "Vehicle age", "Ηλικία οχήματος",
                "Multiplied by the factor for the age of the vehicle in whole years since its year of first registration.",
                "Πολλαπλασιάζεται με τον συντελεστή για την ηλικία του οχήματος σε πλήρη έτη από το έτος πρώτης κυκλοφορίας."),
            new StepDto("CLAIMS", "FACTOR", "CLAIMS_FACTOR", "Claims in the last 5 years", "Ζημιές τελευταίας πενταετίας",
                "Multiplied by the factor for the number of claims the driver reports for the last five years.",
                "Πολλαπλασιάζεται με τον συντελεστή για τον αριθμό ζημιών του οδηγού την τελευταία πενταετία."),
            new StepDto("MINIMUM_PREMIUM", "MIN", "MINIMUM_PREMIUM", "Minimum premium", "Ελάχιστο ασφάλιστρο",
                "The premium is raised to the minimum for the coverage when it is lower.",
                "Το ασφάλιστρο ανεβαίνει στο ελάχιστο της κάλυψης όταν είναι χαμηλότερο."),
            new StepDto("ROUND_PREMIUM", "ROUND", null, "Rounding", "Στρογγυλοποίηση",
                "Rounded to 2 decimals, half up. This is the only place the premium is rounded.",
                "Στρογγυλοποίηση σε 2 δεκαδικά, προς τα πάνω στο μισό. Το μοναδικό σημείο στρογγυλοποίησης του ασφαλίστρου.",
                Places: 2, Mode: "HalfUp"),
        ],
        new Dictionary<string, string>(tableHashes, StringComparer.Ordinal),
        [
            // IPT on every premium line. The rate and the class come from MKT configuration (tax.ipt.rate.<class>, tax.ipt.motor_class); fails closed.
            // The Auxiliary Fund levy is deliberately not produced: the split, the stamp duty and the base are Pending opinion in the GR
            // pack (D-REG-06, D-REG-06a), so there is no settled value to compute a policyholder or insurer share from.
            new TaxPlanDto("GR-IPT", "TAX", "tax.ipt.rate.{class}", Coverages: null, DefaultClass: "general",
                new Dictionary<string, string>(), Places: 2, Mode: "HalfUp", Required: true,
                Classes: ["general"], ClassKey: "tax.ipt.motor_class"),
        ],
        new Dictionary<string, string>
        {
            // The premium charge type of each rated coverage: the ones the PFC product declares.
            ["MTPL"] = "PREM-MTPL",
            ["OWN-DAMAGE"] = "PREM-OD",
            ["WINDSCREEN"] = "PREM-WINDSCREEN",
        });

    private static TableDto Table(string code, string description, string hitPolicy, List<TableVariableDto> variables, List<TableColumnDto> inputs,
        List<TableOutputDto> outputs, List<TableRuleDto> rules) => new(code, "1", hitPolicy, DataStatus.Illustrative,
        $"{description} {IllustrativeNote}", EffectiveFrom, variables, inputs, outputs, rules);

    private static TableRuleDto R(string id, string[] conditions, params string[] outputs) => new(id, [.. conditions], [.. outputs]);

    private static TableDto BaseRate() => Table(
        "BASE_RATE", "Base premium per coverage.", "First", [],
        [new("coverageCode", "string", "coverage"), new("displacement", "int", "engineCc")],
        [new("value", "decimal"), new("basis", "string")],
        [
            R("MTPL-CC-1", ["\"MTPL\"", "[0..1199]"], "110.00", "\"FLAT\""),
            R("MTPL-CC-2", ["\"MTPL\"", "[1200..1599]"], "135.00", "\"FLAT\""),
            R("MTPL-CC-3", ["\"MTPL\"", "[1600..1999]"], "165.00", "\"FLAT\""),
            R("MTPL-CC-4", ["\"MTPL\"", ">= 2000"], "210.00", "\"FLAT\""),
            R("OWN-DAMAGE", ["\"OWN-DAMAGE\"", "-"], "0.0210", "\"VEHICLE_VALUE\""),
            R("WINDSCREEN", ["\"WINDSCREEN\"", "-"], "25.00", "\"FLAT\""),
        ]);

    private static TableDto DriverAgeFactor() => Table(
        "DRIVER_AGE_FACTOR", "Factor by the age of the driver.", "First",
        [new("youngestDriverAge", "int", "ageAt(max(driverBirthDates), effectiveDate)")],
        [new("coverageCode", "string", "coverage"), new("driverAgeBand", "int", "youngestDriverAge")],
        [new("value", "decimal")],
        [
            R("AGE-0-24", ["in [\"MTPL\", \"OWN-DAMAGE\"]", "[0..24]"], "1.4500"),
            R("AGE-25-29", ["in [\"MTPL\", \"OWN-DAMAGE\"]", "[25..29]"], "1.1500"),
            R("AGE-30-64", ["in [\"MTPL\", \"OWN-DAMAGE\"]", "[30..64]"], "1.0000"),
            R("AGE-65-UP", ["in [\"MTPL\", \"OWN-DAMAGE\"]", ">= 65"], "1.1000"),
        ]);

    private static TableDto VehicleAgeFactor() => Table(
        "VEHICLE_AGE_FACTOR", "Factor by the age of the vehicle.", "First",
        [new("vehicleAgeYears", "int", "yearsBetween(vehicleFirstRegistration, effectiveDate)")],
        [new("coverageCode", "string", "coverage"), new("vehicleAgeBand", "int", "vehicleAgeYears")],
        [new("value", "decimal")],
        [
            R("VAGE-0-2", ["\"OWN-DAMAGE\"", "[0..2]"], "1.1000"),
            R("VAGE-3-7", ["\"OWN-DAMAGE\"", "[3..7]"], "1.0000"),
            R("VAGE-8-14", ["\"OWN-DAMAGE\"", "[8..14]"], "0.8500"),
            R("VAGE-15-UP", ["\"OWN-DAMAGE\"", ">= 15"], "0.7000"),
        ]);

    private static TableDto ClaimsFactor() => Table(
        "CLAIMS_FACTOR", "Factor by claims in the last five years.", "First", [],
        [new("coverageCode", "string", "coverage"), new("claimCount", "int", "claimsLast5Years")],
        [new("value", "decimal")],
        [
            R("CLAIMS-0", ["in [\"MTPL\", \"OWN-DAMAGE\"]", "== 0"], "0.9000"),
            R("CLAIMS-1", ["in [\"MTPL\", \"OWN-DAMAGE\"]", "== 1"], "1.0000"),
            R("CLAIMS-2", ["in [\"MTPL\", \"OWN-DAMAGE\"]", "== 2"], "1.2500"),
            R("CLAIMS-3-UP", ["in [\"MTPL\", \"OWN-DAMAGE\"]", ">= 3"], "1.5000"),
        ]);

    private static TableDto MinimumPremium() => Table(
        "MINIMUM_PREMIUM", "Minimum premium per coverage.", "First", [],
        [new("coverageCode", "string", "coverage")],
        [new("value", "decimal")],
        [
            R("MIN-MTPL", ["\"MTPL\""], "80.00"),
            R("MIN-OWN-DAMAGE", ["\"OWN-DAMAGE\""], "90.00"),
        ]);
}
