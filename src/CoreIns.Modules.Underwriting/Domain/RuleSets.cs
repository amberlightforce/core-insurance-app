using System.Globalization;
using System.Text.Json;
using CoreIns.Platform.Errors;
using CoreIns.Rules;
using CoreIns.Rules.DecisionTables;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Underwriting.Domain;

/// <summary>
/// The risk facts the underwriting rules read, taken from the POL risk snapshot (REQ-UW-055). The snapshot is the same motor
/// risk tree RAT rates: PFC product MOTOR-GR elements <c>vehicle</c> and <c>driver</c>. Fields the rules do not use are ignored.
/// </summary>
internal sealed record UwRisk(
    string VehicleElementId,
    DateOnly FirstRegistrationDate,
    decimal VehicleValue,
    int EngineCc,
    string Usage,
    IReadOnlyList<DateOnly> DriverBirthDates,
    int ClaimsLast5Years)
{
    /// <summary>Reads the snapshot. Missing or malformed values are UW-ERR-SNAPSHOT. A missing vehicle value counts as 0 (no value-based referral).</summary>
    public static UwRisk Parse(JsonElement tree)
    {
        try
        {
            var vehicle = tree.GetProperty("vehicle");
            var driver = tree.GetProperty("driver");
            return new UwRisk(
                vehicle.TryGetProperty("elementId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString()! : "vehicle-1",
                new DateOnly(vehicle.GetProperty("firstRegistrationYear").GetInt32(), 1, 1),
                vehicle.TryGetProperty("vehicleValue", out var value) && value.ValueKind != JsonValueKind.Null ? ReadDecimal(value) : 0m,
                vehicle.GetProperty("engineCapacityCc").GetInt32(),
                vehicle.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.String ? usage.GetString()! : "PRIVATE",
                [DateOnly.ParseExact(driver.GetProperty("dateOfBirth").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture)],
                driver.TryGetProperty("claimsLast5Years", out var claims) && claims.ValueKind == JsonValueKind.Number ? claims.GetInt32() : 0);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentNullException or JsonException or ArgumentOutOfRangeException)
        {
            throw new DomainException(DomainError.Of(ModuleCode.UW, "SNAPSHOT", $"The risk snapshot is not a valid motor risk: {ex.Message}"));
        }
    }

    private static decimal ReadDecimal(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("amount", out var amount))
        {
            element = amount;
        }

        return element.ValueKind == JsonValueKind.String
            ? decimal.Parse(element.GetString()!, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)
            : element.GetDecimal();
    }
}

internal sealed record RuleVariableDto(string Name, string Type, string Expression);

internal sealed record RuleColumnDto(string Name, string Type, string Expression);

internal sealed record RuleOutputDto(string Name, string Type);

internal sealed record RuleRowDto(string Id, List<string> Conditions, List<string> Outputs);

/// <summary>One test case of a rule-set version (REQ-UW-038): a risk and the rule ids that must hit.</summary>
internal sealed record RuleTestCaseDto(
    string Name, string FirstRegistrationYear, string VehicleValue, int EngineCc, string Usage, List<string> DriverBirthDates,
    int ClaimsLast5Years, string EffectiveDate, List<string> ExpectedRuleIds);

/// <summary>An underwriting rule set version as stored: a decision table, its metadata and its test cases (REQ-UW-030..032, -038).</summary>
internal sealed record RuleSetDto(
    string Code,
    string Version,
    string ProductCode,
    string Checkpoint,
    string EffectiveFrom,
    string DataStatus,
    string Note,
    string HitPolicy,
    List<RuleVariableDto> Variables,
    List<RuleColumnDto> Inputs,
    List<RuleOutputDto> Outputs,
    List<RuleRowDto> Rules,
    List<RuleTestCaseDto> TestCases);

/// <summary>A rule set compiled on the shared rule engine.</summary>
internal sealed class CompiledRuleSet
{
    public static readonly InputSchema Schema = InputSchema.Define()
        .Variable("effectiveDate", RuleType.Date)
        .Variable("vehicleFirstRegistration", RuleType.Date)
        .Variable("vehicleValue", RuleType.Decimal)
        .Variable("engineCc", RuleType.Int)
        .Variable("usage", RuleType.String)
        .Variable("driverBirthDates", RuleType.ListOf(RuleType.Date))
        .Variable("claimsLast5Years", RuleType.Int)
        .Build();

    private CompiledRuleSet(RuleSetDto dto, CompiledDecisionTable table)
    {
        Dto = dto;
        Table = table;
    }

    public RuleSetDto Dto { get; }

    public CompiledDecisionTable Table { get; }

    public string Hash => Table.ContentHash;

    public static RuleInputs Facts(UwRisk risk, DateOnly effectiveDate) => Schema.NewInputs()
        .Set("effectiveDate", effectiveDate)
        .Set("vehicleFirstRegistration", risk.FirstRegistrationDate)
        .Set("vehicleValue", risk.VehicleValue)
        .Set("engineCc", risk.EngineCc)
        .Set("usage", risk.Usage)
        .Set("driverBirthDates", RuleValue.List(risk.DriverBirthDates.Select(d => (RuleValue)d)))
        .Set("claimsLast5Years", risk.ClaimsLast5Years)
        .Build();

    /// <summary>Compiles the table and refuses a version whose test cases do not pass or do not cover every rule (REQ-UW-036, -038, -040).</summary>
    public static CompiledRuleSet Compile(RuleSetDto dto)
    {
        try
        {
            var environment = RuleEnvironment.Create(Schema);
            var definition = new DecisionTableDefinition(
                new DecisionTableMetadata(dto.Code, dto.Version, DecisionTableStatus.Active, DateOnly.ParseExact(dto.EffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
                Enum.Parse<HitPolicy>(dto.HitPolicy, ignoreCase: true),
                [.. dto.Inputs.Select(i => new InputColumn(i.Name, ParseType(i.Type), i.Expression))],
                [.. dto.Outputs.Select(o => new OutputColumn(o.Name, ParseType(o.Type)))],
                [.. dto.Rules.Select(r => new DecisionRule(r.Id, r.Conditions, r.Outputs))])
            {
                Variables = [.. dto.Variables.Select(v => new TableVariable(v.Name, ParseType(v.Type), v.Expression))],
            };
            var table = CompiledDecisionTable.Compile(definition, environment);
            var cases = dto.TestCases.Select(c => new DecisionTableTestCase(
                c.Name,
                Facts(
                    new UwRisk(
                        "vehicle-1", new DateOnly(int.Parse(c.FirstRegistrationYear, CultureInfo.InvariantCulture), 1, 1),
                        decimal.Parse(c.VehicleValue, CultureInfo.InvariantCulture), c.EngineCc, c.Usage,
                        [.. c.DriverBirthDates.Select(d => DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture))], c.ClaimsLast5Years),
                    DateOnly.ParseExact(c.EffectiveDate, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
                c.ExpectedRuleIds)).ToList();
            var readiness = table.CheckActivationReadiness(cases, requireEveryRuleCovered: true);
            if (!readiness.CanActivate)
            {
                throw new DomainException(DomainError.Of(ModuleCode.UW, "RULESET-UNRESOLVED", $"Rule set {dto.Code} {dto.Version} cannot be activated: {readiness.RefusalReason}"));
            }

            return new CompiledRuleSet(dto, table);
        }
        catch (RuleCompileException ex)
        {
            throw new DomainException(DomainError.Of(ModuleCode.UW, "RULESET-UNRESOLVED", $"Rule set {dto.Code} does not compile: {ex.Message}"));
        }
    }

    private static RuleType ParseType(string type) => type switch
    {
        "string" => RuleType.String,
        "int" => RuleType.Int,
        "decimal" => RuleType.Decimal,
        "date" => RuleType.Date,
        "bool" => RuleType.Bool,
        _ => throw new DomainException(DomainError.Of(ModuleCode.UW, "RULESET-UNRESOLVED", $"Unknown column type '{type}'.")),
    };
}

internal static class RuleSetJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(RuleSetDto dto) => JsonSerializer.Serialize(dto, Options);

    public static RuleSetDto Deserialize(string json) => JsonSerializer.Deserialize<RuleSetDto>(json, Options)!;
}

/// <summary>
/// The rule sets of PFC product MOTOR-GR 1.0: UW-MOTOR-GR-Q (PRE_QUOTE: eligibility declines) and UW-MOTOR-GR-B (PRE_BIND: the declines
/// again plus the referrals), the codes its <c>uwRuleSets</c> reference names. <b>The thresholds are illustrative</b>, like the rating
/// tables (D-SLC-04): they exercise the referral and decline paths and are not an underwriting guideline.
/// </summary>
internal static class BuiltInRuleSets
{
    public const string QuoteCode = "UW-MOTOR-GR-Q";
    public const string BindCode = "UW-MOTOR-GR-B";
    public const string Note = "ILLUSTRATIVE TEST DATA. These thresholds are made up for development and tests; they are not an approved underwriting guideline.";

    private static RuleRowDto R(string id, string[] conditions, params string[] outputs) => new(id, [.. conditions], [.. outputs]);

    private static readonly RuleRowDto[] Declines =
    [
        R("DECLINE-UNDERAGE-DRIVER", ["< 18", "-", "-", "-", "-"], "\"DECLINE\"", "\"DRIVER_UNDERAGE\"", "\"DECLINE\"", "\"PRE_QUOTE\"",
            "\"The driver is under 18.\"", "\"Ο οδηγός είναι κάτω των 18 ετών.\""),
        R("DECLINE-CLAIMS-HISTORY", ["-", "-", "-", ">= 5", "-"], "\"DECLINE\"", "\"CLAIMS_HISTORY\"", "\"DECLINE\"", "\"PRE_QUOTE\"",
            "\"Five or more claims in the last five years.\"", "\"Πέντε ή περισσότερες ζημιές την τελευταία πενταετία.\""),
        R("DECLINE-USAGE", ["-", "-", "-", "-", "== \"BUSINESS\""], "\"DECLINE\"", "\"USAGE_NOT_ELIGIBLE\"", "\"DECLINE\"", "\"PRE_QUOTE\"",
            "\"Business use is not eligible for the private car product.\"", "\"Η επαγγελματική χρήση δεν είναι επιλέξιμη για το ιδιωτικό αυτοκίνητο.\""),
    ];

    private static readonly RuleRowDto[] Referrals =
    [
        R("REFER-YOUNG-DRIVER", ["[18..20]", "-", "-", "-", "-"], "\"REFER\"", "\"DRIVER_AGE_REFERRAL\"", "\"REFER\"", "\"PRE_BIND\"",
            "\"The driver is under 21.\"", "\"Ο οδηγός είναι κάτω των 21 ετών.\""),
        R("REFER-OLD-VEHICLE", ["-", "> 20", "-", "-", "-"], "\"REFER\"", "\"VEHICLE_AGE_REFERRAL\"", "\"REFER\"", "\"PRE_BIND\"",
            "\"The vehicle is older than 20 years.\"", "\"Το όχημα είναι παλαιότερο των 20 ετών.\""),
        R("REFER-HIGH-VALUE", ["-", "-", "> 100000", "-", "-"], "\"REFER\"", "\"VEHICLE_VALUE_REFERRAL\"", "\"REFER\"", "\"PRE_BIND\"",
            "\"The vehicle value is above 100,000.\"", "\"Η αξία του οχήματος υπερβαίνει τις 100.000.\""),
    ];

    /// <summary>The PRE_QUOTE rule set (declines only).</summary>
    public static RuleSetDto Quote(string productCode) => Build(QuoteCode, productCode, "PRE_QUOTE", Declines, DeclineCases());

    /// <summary>The PRE_BIND rule set (declines and referrals).</summary>
    public static RuleSetDto Bind(string productCode) => Build(BindCode, productCode, "PRE_BIND", [.. Declines, .. Referrals], [.. DeclineCases(), .. ReferralCases()]);

    private static RuleSetDto Build(string code, string productCode, string checkpoint, RuleRowDto[] rules, List<RuleTestCaseDto> cases) => new(
        code, "1.0", productCode, checkpoint, "2026-01-01", "ILLUSTRATIVE_TEST_DATA", Note, "Collect",
        [
            new("youngestDriverAge", "int", "ageAt(max(driverBirthDates), effectiveDate)"),
            new("vehicleAgeYears", "int", "yearsBetween(vehicleFirstRegistration, effectiveDate)"),
        ],
        [
            new("driverAgeIn", "int", "youngestDriverAge"),
            new("vehicleAgeIn", "int", "vehicleAgeYears"),
            new("vehicleValueIn", "decimal", "vehicleValue"),
            new("claimsIn", "int", "claimsLast5Years"),
            new("usageIn", "string", "usage"),
        ],
        [new("ruleType", "string"), new("issueType", "string"), new("severity", "string"), new("blockingPoint", "string"), new("messageEn", "string"), new("messageEl", "string")],
        [.. rules],
        cases);

    private static List<RuleTestCaseDto> DeclineCases() =>
    [
        Case("accept-typical", "2020", "15000.00", 1400, "PRIVATE", ["1985-06-15"], 0, []),
        Case("decline-underage", "2020", "15000.00", 1400, "PRIVATE", ["2009-06-15"], 0, ["DECLINE-UNDERAGE-DRIVER"]),
        Case("decline-claims", "2020", "15000.00", 1400, "PRIVATE", ["1985-06-15"], 5, ["DECLINE-CLAIMS-HISTORY"]),
        Case("decline-usage", "2020", "15000.00", 1400, "BUSINESS", ["1985-06-15"], 0, ["DECLINE-USAGE"]),
    ];

    private static List<RuleTestCaseDto> ReferralCases() =>
    [
        Case("refer-young-driver", "2020", "15000.00", 1400, "PRIVATE", ["2007-06-15"], 0, ["REFER-YOUNG-DRIVER"]),
        Case("refer-old-vehicle", "2001", "3000.00", 1400, "PRIVATE", ["1985-06-15"], 0, ["REFER-OLD-VEHICLE"]),
        Case("refer-high-value", "2025", "120000.00", 3000, "PRIVATE", ["1985-06-15"], 0, ["REFER-HIGH-VALUE"]),
    ];

    private static RuleTestCaseDto Case(
        string name, string firstRegistrationYear, string value, int cc, string usage, string[] birthDates, int claims, string[] expected) =>
        new(name, firstRegistrationYear, value, cc, usage, [.. birthDates], claims, "2026-11-01", [.. expected]);
}
