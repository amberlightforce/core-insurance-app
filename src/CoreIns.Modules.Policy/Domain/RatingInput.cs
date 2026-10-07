using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Policy.Domain;

/// <summary>The rated vehicle, the input RAT and UW read (with personal data), and a hash of the input.</summary>
internal sealed record RatingView(string VehicleLocator, JsonElement Input);

/// <summary>
/// Builds the motor risk input RAT rates and UW evaluates (the PFC MOTOR-GR elements <c>vehicle</c>, <c>driver</c> and the
/// selected coverage codes) from POL's draft risk tree. The driver's date of birth is P2 and is held only by PTY
/// (REQ-POL-300): it is read through an audited PTY reveal with purpose RATING for each rating, passed to RAT and UW
/// in-process, and never stored in POL nor returned by a POL command. The slice rates one vehicle with one main driver.
/// </summary>
internal sealed class RatingInput(IPartyPartyService parties, RequestContext context)
{
    public const string RevealPurpose = "RATING";

    public async Task<Result<RatingView>> BuildAsync(RiskTree tree, Instant effectiveAt, CancellationToken cancellationToken)
    {
        if (tree.Vehicles.Count != 1)
        {
            return Invalid("riskTree.vehicles", "ONE_VEHICLE", "The slice rates exactly one vehicle per policy.");
        }

        var vehicle = tree.Vehicles[0];
        var driver = tree.Drivers.FirstOrDefault(d => d.DriverType == Driver.DriverTypeValue.Main && (d.VehicleLocator is null || d.VehicleLocator == vehicle.Locator));
        if (driver is null)
        {
            return Invalid("riskTree.drivers", "MAIN_DRIVER_REQUIRED", "Add the main driver of the vehicle.");
        }

        if (vehicle.FirstRegistrationYear is null || vehicle.EngineCapacityCc is null)
        {
            return Invalid("riskTree.vehicles[0]", "RATING_FIELDS_REQUIRED", "The vehicle needs its first registration year and engine capacity.");
        }

        var birthDate = await BirthDateAsync(driver.PartyId, effectiveAt, cancellationToken).ConfigureAwait(false);
        if (birthDate.IsFailure)
        {
            return birthDate.Error!;
        }

        var input = new JsonObject
        {
            ["vehicle"] = new JsonObject
            {
                ["elementId"] = vehicle.Locator,
                ["firstRegistrationYear"] = vehicle.FirstRegistrationYear,
                ["engineCapacityCc"] = vehicle.EngineCapacityCc,
                ["vehicleValue"] = vehicle.Value?.Amount.ToString(CultureInfo.InvariantCulture),
                ["usage"] = vehicle.Use ?? Answer(tree, "Q-USAGE") ?? "PRIVATE",
            },
            ["driver"] = new JsonObject
            {
                ["elementId"] = driver.Locator,
                ["dateOfBirth"] = birthDate.Value.ToString(),
                ["claimsLast5Years"] = driver.ClaimsLast5Years ?? 0,
            },
            ["coverages"] = new JsonArray([.. tree.Coverages.Where(c => c.Selected).Select(c => c.CoverageCode).Distinct(StringComparer.Ordinal).Select(c => (JsonNode)c)]),
        };
        if (vehicle.Value is null)
        {
            ((JsonObject)input["vehicle"]!).Remove("vehicleValue");
        }

        return new RatingView(vehicle.Locator!, JsonSerializer.SerializeToElement(input));
    }

    /// <summary>The driver's birth date through PTY's audited reveal (permission pty.Party.revealP2, purpose RATING).</summary>
    private async Task<Result<BusinessDate>> BirthDateAsync(PartyId partyId, Instant effectiveAt, CancellationToken cancellationToken)
    {
        // The reveal is a read: in a POL dry-run it still runs (inside the rolled-back unit of work), so it is not refused as a dry run.
        using (context.Use(context.IdempotencyKey ?? IdempotencyKey.New(), dryRun: false))
        {
            try
            {
                var party = await parties.GetAsync(partyId.Value.ToString(), ValidAt.From(effectiveAt), revealPurpose: RevealPurpose, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return party.Party.BirthDate is { } date
                    ? date
                    : Invalid("riskTree.drivers", "DRIVER_BIRTH_DATE_MISSING", "PTY holds no date of birth for the driver.");
            }
            catch (DomainException ex) when (ex.Error.Code.Name == "NOT-FOUND")
            {
                return Invalid("riskTree.drivers", "PARTY_NOT_FOUND", "The driver does not exist in PTY.");
            }
        }
    }

    private static string? Answer(RiskTree tree, string question) =>
        tree.QuestionSets.Select(q => q.Answers).Where(a => a.ValueKind == JsonValueKind.Object)
            .Select(a => a.TryGetProperty(question, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
            .FirstOrDefault(v => v is not null);

    private static DomainError Invalid(string field, string code, string message) =>
        new(ErrorCode.For(ModuleCode.POL, "VALIDATION"), message) { FieldErrors = [new FieldError(field, code, "pol." + code.ToLowerInvariant(), message)] };
}
