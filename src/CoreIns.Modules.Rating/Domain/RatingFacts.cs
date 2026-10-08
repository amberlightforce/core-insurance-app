using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Platform.Errors;
using CoreIns.Rules;
using CoreIns.SharedKernel;

namespace CoreIns.Modules.Rating.Domain;

/// <summary>
/// The motor private car risk tree RAT rates (the artefact's input schema, REQ-RAT-031). It follows the PFC product MOTOR-GR
/// elements and field codes (<c>vehicle</c> and <c>driver</c>); the coverage codes are the product's (MTPL, OWN-DAMAGE, WINDSCREEN).
/// Fields the product declares but pricing does not use are accepted and ignored (and never reach the worksheet or its hash);
/// anything else is rejected.
/// </summary>
internal sealed record MotorRisk(
    string VehicleElementId,
    DateOnly FirstRegistrationDate,
    int EngineCc,
    decimal? VehicleValue,
    string Usage,
    DateOnly DriverBirthDate,
    int ClaimsLast5Years,
    IReadOnlyList<string> Coverages)
{
    private static readonly string[] VehicleFields =
    [
        "elementId", "registrationNumber", "make", "model", "firstRegistrationYear", "engineCapacityCc", "powerKw", "fuelType", "vehicleValue",
        "usage", "garagingPostcode", "ownerType",
    ];

    private static readonly string[] DriverFields = ["elementId", "dateOfBirth", "licenceIssueDate", "bonusMalusClass", "claimsLast5Years"];

    /// <summary>
    /// Parses and validates a risk tree. Attributes the schema does not declare are rejected (REQ-RAT-033, RAT-ERR-INPUT-UNDECLARED);
    /// missing or malformed values are RAT-ERR-INPUT. The result is the normalised input.
    /// </summary>
    public static MotorRisk Parse(JsonElement tree, string segmentId)
    {
        var path = $"segments[{segmentId}].riskTree";
        var root = Object(tree, path);
        Declared(root, path, "vehicle", "driver", "coverages");
        var vehicle = Object(Required(root, "vehicle", path), path + ".vehicle");
        Declared(vehicle, path + ".vehicle", VehicleFields);
        var driver = Object(Required(root, "driver", path), path + ".driver");
        Declared(driver, path + ".driver", DriverFields);

        var coverageArray = Required(root, "coverages", path);
        if (coverageArray.ValueKind != JsonValueKind.Array || coverageArray.GetArrayLength() == 0)
        {
            throw Input(path + ".coverages", "Select at least one coverage.");
        }

        var coverages = new List<string>();
        foreach (var c in coverageArray.EnumerateArray())
        {
            var code = c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(code) || coverages.Contains(code, StringComparer.Ordinal))
            {
                throw Input(path + ".coverages", "Coverage codes must be non-empty and listed once.");
            }

            coverages.Add(code);
        }

        var year = Integer(Required(vehicle, "firstRegistrationYear", path + ".vehicle"), path + ".vehicle.firstRegistrationYear", 1950, 2100);
        return new MotorRisk(
            OptionalString(vehicle, "elementId") ?? "vehicle-1",
            new DateOnly(year, 1, 1),
            Integer(Required(vehicle, "engineCapacityCc", path + ".vehicle"), path + ".vehicle.engineCapacityCc", 1, 20000),
            vehicle.TryGetProperty("vehicleValue", out var value) && value.ValueKind != JsonValueKind.Null ? Money(value, path + ".vehicle.vehicleValue") : null,
            OptionalString(vehicle, "usage") ?? "PRIVATE",
            Date(Required(driver, "dateOfBirth", path + ".driver"), path + ".driver.dateOfBirth"),
            driver.TryGetProperty("claimsLast5Years", out var claims) && claims.ValueKind != JsonValueKind.Null
                ? Integer(claims, path + ".driver.claimsLast5Years", 0, 50)
                : 0,
            [.. coverages.Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// The normalised input as canonical-JSON friendly nodes, for the input hash (REQ-RAT-032): only what pricing uses, decimals at a canonical
    /// scale, and the derived ages on <paramref name="basis"/> instead of birth date and registration year (the price depends on the age, not the date).
    /// </summary>
    public JsonObject ToNormalised(DateOnly basis) => new()
    {
        ["vehicle"] = new JsonObject
        {
            ["elementId"] = VehicleElementId,
            ["ageYears"] = WholeYears(FirstRegistrationDate, basis),
            ["engineCapacityCc"] = EngineCc,
            ["vehicleValue"] = VehicleValue?.ToString("F2", CultureInfo.InvariantCulture),
            ["usage"] = Usage,
        },
        ["driver"] = new JsonObject
        {
            ["ageYears"] = WholeYears(DriverBirthDate, basis),
            ["claimsLast5Years"] = ClaimsLast5Years,
        },
        ["coverages"] = new JsonArray(Coverages.Select(c => (JsonNode)c).ToArray()),
    };

    private static int WholeYears(DateOnly from, DateOnly to)
    {
        var years = to.Year - from.Year;
        return from.AddYears(years) > to ? years - 1 : years;
    }

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

    /// <summary>An amount as a number, a decimal string, or a money object with an <c>amount</c> (the wire form of a PFC MONEY field).</summary>
    private static decimal Money(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("amount", out var amount))
        {
            element = amount;
        }

        var text = element.ValueKind switch
        {
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.String => element.GetString(),
            _ => null,
        };

        // Exact or rejected (D-ARC-27): plain decimal digits only, at most two decimals, no rounding on the way in.
        // The value is normalised to scale 2 so that 70004 and 70004.00 are the same input.
        if (text is null || !IsPlainAmount(text)
            || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value is <= 0m or > 100_000_000m)
        {
            throw Input(path, "A positive amount with at most two decimals is expected.");
        }

        return decimal.Round(value, 2) + 0.00m;
    }

    /// <summary>
    /// Plain decimal text: 1–9 digits, optionally a point and 1–2 decimals followed only by zeros (same language as
    /// <c>^[0-9]{1,9}(\.[0-9]{1,2}0*)?$</c>). Hand-written so that no regex timeout can turn a valid request into a 500 under load.
    /// </summary>
    private static bool IsPlainAmount(string text)
    {
        var point = text.IndexOf('.', StringComparison.Ordinal);
        var whole = point < 0 ? text : text[..point];
        if (whole.Length is < 1 or > 9 || !whole.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (point < 0)
        {
            return true;
        }

        var fraction = text[(point + 1)..];
        return fraction.Length >= 1 && fraction.All(char.IsAsciiDigit) && (fraction.Length <= 2 || fraction[2..].All(c => c == '0'));
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
        .Variable("claimsLast5Years", RuleType.Int)
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
        .Set("vehicleValue", risk.VehicleValue ?? 0m)
        .Set("engineCc", risk.EngineCc)
        .Set("usage", risk.Usage)
        .Set("driverBirthDates", RuleValue.List((RuleValue)risk.DriverBirthDate))
        .Set("claimsLast5Years", risk.ClaimsLast5Years)
        .Build();
}
