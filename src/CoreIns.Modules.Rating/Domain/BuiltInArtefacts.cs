namespace CoreIns.Modules.Rating.Domain;

/// <summary>
/// The motor private car rating artefact of the thin end-to-end slice.
/// <b>Every number in these tables is illustrative test data</b> (D-SLC-04): it was made up to exercise the engine and is
/// not a tariff, not an actuarial result and not approved by anyone. The artefact metadata, the worksheet header and a
/// response warning all say so. Tax and levy rates are not here at all: they come from MKT configuration.
/// </summary>
internal static class BuiltInArtefacts
{
    public const string DefaultProductCode = "MOTOR_PRIVATE_CAR";
    public const string DefaultProductVersion = "1.0";
    public const string EffectiveFrom = "2026-01-01";
    public const string IllustrativeNote =
        "ILLUSTRATIVE TEST DATA. These rates are made up for development and tests; they are not an approved tariff and must never be used to price real policies.";

    public static IReadOnlyList<TableDto> Tables() => [BaseRate(), DriverAgeFactor(), VehicleAgeFactor(), ClaimsFactor(), MinimumPremium()];

    /// <summary>The artefact definition for a product, pinning the given table hashes (code to hash).</summary>
    public static ArtefactDefinition Definition(string productCode, string productVersion, IReadOnlyDictionary<string, string> tableHashes) => new(
        "rat.artefact/1",
        "MOTOR_PRIVATE_CAR_RATING",
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
            new StepDto("DRIVER_AGE", "FACTOR", "DRIVER_AGE_FACTOR", "Youngest driver age", "Ηλικία νεότερου οδηγού",
                "Multiplied by the factor for the age of the youngest named driver on the effective date.",
                "Πολλαπλασιάζεται με τον συντελεστή για την ηλικία του νεότερου οδηγού κατά την ημερομηνία ισχύος."),
            new StepDto("VEHICLE_AGE", "FACTOR", "VEHICLE_AGE_FACTOR", "Vehicle age", "Ηλικία οχήματος",
                "Multiplied by the factor for the age of the vehicle in whole years since first registration.",
                "Πολλαπλασιάζεται με τον συντελεστή για την ηλικία του οχήματος σε πλήρη έτη από την πρώτη ταξινόμηση."),
            new StepDto("CLAIMS", "FACTOR", "CLAIMS_FACTOR", "Claims in the last 3 years", "Ζημιές τελευταίας τριετίας",
                "Multiplied by the factor for the number of claims reported by the drivers in the last three years.",
                "Πολλαπλασιάζεται με τον συντελεστή για τον αριθμό ζημιών των οδηγών την τελευταία τριετία."),
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
            // Rates are read from MKT configuration under these keys (PRD-17 key catalogue); nothing is hard-coded here.
            new TaxPlanDto("IPT", "TAX", "tax.ipt.rate", Coverages: null, DefaultClass: "GENERAL",
                new Dictionary<string, string> { ["FIRE"] = "FIRE" }, Places: 2, Mode: "HalfUp"),
            new TaxPlanDto("AUX_FUND_LEVY", "LEVY", "tax.levy.auxfund.rate", ["MTPL"], DefaultClass: "MTPL",
                new Dictionary<string, string>(), Places: 2, Mode: "HalfUp"),
        ]);

    private static TableDto Table(string code, string description, string hitPolicy, List<TableVariableDto> variables, List<TableColumnDto> inputs,
        List<TableOutputDto> outputs, List<TableRuleDto> rules) => new(code, "1", hitPolicy, DataStatus.Illustrative,
        $"{description} {IllustrativeNote}", EffectiveFrom, variables, inputs, outputs, rules);

    private static TableRuleDto R(string id, string[] conditions, params string[] outputs) => new(id, [.. conditions], [.. outputs]);

    private static TableDto BaseRate() => Table(
        "BASE_RATE", "Base premium per coverage.", "First", [],
        [new("coverage", "string", "coverage"), new("engineCc", "int", "engineCc")],
        [new("value", "decimal"), new("basis", "string")],
        [
            R("MTPL-CC-1", ["\"MTPL\"", "[0..1199]"], "110.00", "\"FLAT\""),
            R("MTPL-CC-2", ["\"MTPL\"", "[1200..1599]"], "135.00", "\"FLAT\""),
            R("MTPL-CC-3", ["\"MTPL\"", "[1600..1999]"], "165.00", "\"FLAT\""),
            R("MTPL-CC-4", ["\"MTPL\"", ">= 2000"], "210.00", "\"FLAT\""),
            R("OWN-DAMAGE", ["\"OWN_DAMAGE\"", "-"], "0.0210", "\"VEHICLE_VALUE\""),
            R("THEFT", ["\"THEFT\"", "-"], "0.0045", "\"VEHICLE_VALUE\""),
            R("FIRE", ["\"FIRE\"", "-"], "0.0020", "\"VEHICLE_VALUE\""),
            R("WINDSCREEN", ["\"WINDSCREEN\"", "-"], "25.00", "\"FLAT\""),
            R("ROADSIDE", ["\"ROADSIDE_ASSISTANCE\"", "-"], "30.00", "\"FLAT\""),
        ]);

    private static TableDto DriverAgeFactor() => Table(
        "DRIVER_AGE_FACTOR", "Factor by the age of the youngest driver.", "First",
        [new("youngestDriverAge", "int", "ageAt(max(driverBirthDates), effectiveDate)")],
        [new("coverage", "string", "coverage"), new("driverAge", "int", "youngestDriverAge")],
        [new("value", "decimal")],
        [
            R("AGE-0-24", ["in [\"MTPL\", \"OWN_DAMAGE\"]", "[0..24]"], "1.4500"),
            R("AGE-25-29", ["in [\"MTPL\", \"OWN_DAMAGE\"]", "[25..29]"], "1.1500"),
            R("AGE-30-64", ["in [\"MTPL\", \"OWN_DAMAGE\"]", "[30..64]"], "1.0000"),
            R("AGE-65-UP", ["in [\"MTPL\", \"OWN_DAMAGE\"]", ">= 65"], "1.1000"),
        ]);

    private static TableDto VehicleAgeFactor() => Table(
        "VEHICLE_AGE_FACTOR", "Factor by the age of the vehicle.", "First",
        [new("vehicleAge", "int", "yearsBetween(vehicleFirstRegistration, effectiveDate)")],
        [new("coverage", "string", "coverage"), new("vehicleAge", "int", "vehicleAge")],
        [new("value", "decimal")],
        [
            R("VAGE-0-2", ["in [\"OWN_DAMAGE\", \"THEFT\", \"FIRE\"]", "[0..2]"], "1.1000"),
            R("VAGE-3-7", ["in [\"OWN_DAMAGE\", \"THEFT\", \"FIRE\"]", "[3..7]"], "1.0000"),
            R("VAGE-8-14", ["in [\"OWN_DAMAGE\", \"THEFT\", \"FIRE\"]", "[8..14]"], "0.8500"),
            R("VAGE-15-UP", ["in [\"OWN_DAMAGE\", \"THEFT\", \"FIRE\"]", ">= 15"], "0.7000"),
        ]);

    private static TableDto ClaimsFactor() => Table(
        "CLAIMS_FACTOR", "Factor by claims in the last three years.", "First", [],
        [new("coverage", "string", "coverage"), new("claims", "int", "claimsLast3Years")],
        [new("value", "decimal")],
        [
            R("CLAIMS-0", ["in [\"MTPL\", \"OWN_DAMAGE\"]", "== 0"], "0.9000"),
            R("CLAIMS-1", ["in [\"MTPL\", \"OWN_DAMAGE\"]", "== 1"], "1.0000"),
            R("CLAIMS-2", ["in [\"MTPL\", \"OWN_DAMAGE\"]", "== 2"], "1.2500"),
            R("CLAIMS-3-UP", ["in [\"MTPL\", \"OWN_DAMAGE\"]", ">= 3"], "1.5000"),
        ]);

    private static TableDto MinimumPremium() => Table(
        "MINIMUM_PREMIUM", "Minimum premium per coverage.", "First", [],
        [new("coverage", "string", "coverage")],
        [new("value", "decimal")],
        [
            R("MIN-MTPL", ["\"MTPL\""], "80.00"),
            R("MIN-OWN-DAMAGE", ["\"OWN_DAMAGE\""], "90.00"),
            R("MIN-THEFT", ["\"THEFT\""], "25.00"),
            R("MIN-FIRE", ["\"FIRE\""], "15.00"),
        ]);
}
