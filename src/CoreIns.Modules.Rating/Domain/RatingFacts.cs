using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Platform.Errors;
using CoreIns.Rules;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Rating.Domain;

/// <summary>A driver in the motor risk tree.</summary>
internal sealed record DriverInput(string ElementId, DateOnly BirthDate, int ClaimsLast3Years);

/// <summary>The motor private car risk tree RAT rates (the artefact's input schema, REQ-RAT-031).</summary>
internal sealed record MotorRisk(
    string VehicleElementId,
    DateOnly FirstRegistrationDate,
    int EngineCc,
    decimal VehicleValue,
    string Usage,
    string? Territory,
    IReadOnlyList<DriverInput> Drivers,
    IReadOnlyList<string> Coverages)
{
    /// <summary>
    /// Parses and validates a risk tree. Attributes the schema does not declare are rejected (REQ-RAT-033, RAT-ERR-INPUT-UNDECLARED);
    /// missing or malformed values are RAT-ERR-INPUT. The result is the normalised input.
    /// </summary>
    public static MotorRisk Parse(JsonElement tree, string segmentId)
    {
        var path = $"segments[{segmentId}].riskTree";
        var root = Object(tree, path);
        Declared(root, path, "vehicle", "drivers", "coverages");
        var vehicle = Object(Required(root, "vehicle", path), path + ".vehicle");
        Declared(vehicle, path + ".vehicle", "elementId", "firstRegistrationDate", "engineCc", "value", "usage", "territory");
        var drivers = new List<DriverInput>();
        var driverArray = Required(root, "drivers", path);
        if (driverArray.ValueKind != JsonValueKind.Array || driverArray.GetArrayLength() == 0)
        {
            throw Input(path + ".drivers", "At least one driver is required.");
        }

        var index = 0;
        foreach (var d in driverArray.EnumerateArray())
        {
            var dp = $"{path}.drivers[{index}]";
            var driver = Object(d, dp);
            Declared(driver, dp, "elementId", "birthDate", "claimsLast3Years");
            drivers.Add(new DriverInput(
                OptionalString(driver, "elementId") ?? $"driver-{index + 1}",
                Date(Required(driver, "birthDate", dp), dp + ".birthDate"),
                Integer(Required(driver, "claimsLast3Years", dp), dp + ".claimsLast3Years", 0, 50)));
            index++;
        }

        var coverages = new List<string>();
        var coverageArray = Required(root, "coverages", path);
        if (coverageArray.ValueKind != JsonValueKind.Array || coverageArray.GetArrayLength() == 0)
        {
            throw Input(path + ".coverages", "Select at least one coverage.");
        }

        foreach (var c in coverageArray.EnumerateArray())
        {
            var code = c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(code) || coverages.Contains(code, StringComparer.Ordinal))
            {
                throw Input(path + ".coverages", "Coverage codes must be non-empty and listed once.");
            }

            coverages.Add(code);
        }

        return new MotorRisk(
            OptionalString(vehicle, "elementId") ?? "vehicle-1",
            Date(Required(vehicle, "firstRegistrationDate", path + ".vehicle"), path + ".vehicle.firstRegistrationDate"),
            Integer(Required(vehicle, "engineCc", path + ".vehicle"), path + ".vehicle.engineCc", 1, 20000),
            Money(Required(vehicle, "value", path + ".vehicle"), path + ".vehicle.value"),
            OptionalString(vehicle, "usage") ?? "PRIVATE",
            OptionalString(vehicle, "territory"),
            drivers,
            [.. coverages.Order(StringComparer.Ordinal)]);
    }

    /// <summary>The normalised input as canonical-JSON friendly nodes (decimals as strings), for the input hash (REQ-RAT-032).</summary>
    public JsonObject ToNormalised() => new()
    {
        ["vehicle"] = new JsonObject
        {
            ["elementId"] = VehicleElementId,
            ["firstRegistrationDate"] = FirstRegistrationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["engineCc"] = EngineCc,
            ["value"] = VehicleValue.ToString(CultureInfo.InvariantCulture),
            ["usage"] = Usage,
            ["territory"] = Territory,
        },
        ["drivers"] = new JsonArray(Drivers.Select(d => (JsonNode)new JsonObject
        {
            ["elementId"] = d.ElementId,
            ["birthDate"] = d.BirthDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["claimsLast3Years"] = d.ClaimsLast3Years,
        }).ToArray()),
        ["coverages"] = new JsonArray(Coverages.OrderBy(c => c, StringComparer.Ordinal).Select(c => (JsonNode)c).ToArray()),
    };

    private static JsonElement Object(JsonElement element, string path) =>
        element.ValueKind == JsonValueKind.Object ? element : throw Input(path, "An object is expected.");

    private static JsonElement Required(JsonElement parent, string name, string path) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : throw Input($"{path}.{name}", "The value is required.");

    private static string? OptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void Declared(JsonElement obj, string path, params string[] names)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new DomainException(DomainError.Of(
                    ModuleCode.RAT, "INPUT-UNDECLARED", $"{path}.{property.Name} is not declared in the rating input schema."));
            }
        }
    }

    private static DateOnly Date(JsonElement element, string path) =>
        element.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(element.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw Input(path, "A date (yyyy-MM-dd) is expected.");

    private static int Integer(JsonElement element, string path, int min, int max) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) && value >= min && value <= max
            ? value
            : throw Input(path, $"A whole number from {min} to {max} is expected.");

    private static decimal Money(JsonElement element, string path)
    {
        decimal value;
        var ok = element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(element.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value),
            _ => (value = 0m) != 0m,
        };
        return ok && value is > 0m and <= 100_000_000m ? value : throw Input(path, "A positive amount is expected.");
    }

    private static DomainException Input(string path, string message) =>
        new(DomainError.Of(ModuleCode.RAT, "INPUT", $"{path}: {message}"));
}

/// <summary>The facts the rate tables and step expressions read (one rating of one coverage).</summary>
internal static class RatingFacts
{
    public static InputSchema Schema { get; } = InputSchema.Define()
        .Variable("coverage", RuleType.String)
        .Variable("effectiveDate", RuleType.Date)
        .Variable("vehicleFirstRegistration", RuleType.Date)
        .Variable("vehicleValue", RuleType.Decimal)
        .Variable("engineCc", RuleType.Int)
        .Variable("usage", RuleType.String)
        .Variable("driverBirthDates", RuleType.ListOf(RuleType.Date))
        .Variable("claimsLast3Years", RuleType.Int)
        .Build();

    public static InputSchema StepSchema { get; } = InputSchema.Define()
        .Variable("coverage", RuleType.String)
        .Variable("vehicleValue", RuleType.Decimal)
        .Variable("running", RuleType.Decimal)
        .Variable("value", RuleType.Decimal)
        .Variable("basis", RuleType.String)
        .Build();

    public static RuleInputs For(MotorRisk risk, string coverage, DateOnly effectiveDate) => Schema.NewInputs()
        .Set("coverage", coverage)
        .Set("effectiveDate", effectiveDate)
        .Set("vehicleFirstRegistration", risk.FirstRegistrationDate)
        .Set("vehicleValue", risk.VehicleValue)
        .Set("engineCc", risk.EngineCc)
        .Set("usage", risk.Usage)
        .Set("driverBirthDates", RuleValue.List(risk.Drivers.Select(d => (RuleValue)d.BirthDate)))
        .Set("claimsLast3Years", risk.Drivers.Sum(d => d.ClaimsLast3Years))
        .Build();
}
