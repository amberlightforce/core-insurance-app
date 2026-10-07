using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Policy.Domain;

/// <summary>
/// The draft risk tree of a quote version (REQ-POL-010): vehicles, drivers, coverage selections and answered question
/// sets. Instructions edit it immutably; POL assigns every element a static UUIDv7 locator (REQ-POL-036) and normalises
/// plates through the bound <see cref="IIdValidator"/> (<c>VEHICLE_PLATE</c>, REQ-POL-279: no plate rule in core).
/// </summary>
internal sealed class RiskTrees(IIdValidator idValidator)
{
    /// <summary>An empty tree.</summary>
    public static RiskTree Empty { get; } = new() { Vehicles = [], Drivers = [], Coverages = [], QuestionSets = [] };

    /// <summary>Applies the instructions in order; a reference to an unknown element is a validation failure.</summary>
    public async Task<Result<RiskTree>> ApplyAsync(RiskTree tree, IReadOnlyList<DraftInstruction> instructions, string jurisdiction, CancellationToken cancellationToken)
    {
        var vehicles = tree.Vehicles.ToList();
        var drivers = tree.Drivers.ToList();
        var coverages = tree.Coverages.ToList();
        var questionSets = tree.QuestionSets.ToList();

        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            var field = $"instructions[{i}]";
            switch (instruction.Op)
            {
                case DraftInstruction.OpValue.SetVehicle when instruction.Vehicle is { } vehicle:
                {
                    if (vehicle.Locator is { } locator && vehicles.FindIndex(v => v.Locator == locator) < 0)
                    {
                        return Invalid(field + ".vehicle.locator", "UNKNOWN_LOCATOR", "The vehicle locator is not on this draft; omit it to add a vehicle.");
                    }

                    var normalised = await NormalisePlateAsync(vehicle.Plate, jurisdiction, cancellationToken).ConfigureAwait(false);
                    var stored = vehicle with { Locator = vehicle.Locator ?? NewLocator(), Plate = vehicle.Plate.Trim(), PlateNormalised = normalised };
                    Upsert(vehicles, stored, v => v.Locator);
                    break;
                }

                case DraftInstruction.OpValue.SetDriver when instruction.Driver is { } driver:
                {
                    if (driver.Locator is { } locator && drivers.FindIndex(d => d.Locator == locator) < 0)
                    {
                        return Invalid(field + ".driver.locator", "UNKNOWN_LOCATOR", "The driver locator is not on this draft; omit it to add a driver.");
                    }

                    Upsert(drivers, driver with { Locator = driver.Locator ?? NewLocator() }, d => d.Locator);
                    break;
                }

                case DraftInstruction.OpValue.RemoveVehicle when instruction.Locator is { } locator:
                    if (vehicles.RemoveAll(v => v.Locator == locator) == 0)
                    {
                        return Invalid(field + ".locator", "UNKNOWN_LOCATOR", "No vehicle has this locator.");
                    }

                    // Selections and assignments of the removed vehicle go with it.
                    coverages.RemoveAll(c => c.ElementLocator == locator);
                    drivers = [.. drivers.Select(d => d.VehicleLocator == locator ? d with { VehicleLocator = null } : d)];
                    break;

                case DraftInstruction.OpValue.RemoveDriver when instruction.Locator is { } locator:
                    if (drivers.RemoveAll(d => d.Locator == locator) == 0)
                    {
                        return Invalid(field + ".locator", "UNKNOWN_LOCATOR", "No driver has this locator.");
                    }

                    break;

                case DraftInstruction.OpValue.SetCoverages when instruction.Coverages is { } selections:
                    coverages = [.. selections];
                    break;

                case DraftInstruction.OpValue.SetAnswers when instruction.QuestionSet is { } answers:
                    Upsert(questionSets, answers, q => q.QuestionSetCode);
                    break;

                default:
                    return Invalid(field, "INSTRUCTION_INCOMPLETE", $"{instruction.Op} needs its element (vehicle, driver, locator, coverages or questionSet).");
            }
        }

        return new RiskTree { Vehicles = vehicles, Drivers = drivers, Coverages = coverages, QuestionSets = questionSets };
    }

    /// <summary>
    /// Findings that block quoting but not saving a draft: at least one vehicle and one selected coverage, assignments
    /// and coverage attachments point at vehicles on the draft, usage per vehicle at most 100 %.
    /// </summary>
    public static IReadOnlyList<JobUpdateDraftResponse.ValidationItem> Findings(RiskTree tree)
    {
        var findings = new List<JobUpdateDraftResponse.ValidationItem>();
        var vehicles = tree.Vehicles.Select(v => v.Locator).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (vehicles.Count == 0)
        {
            findings.Add(Item("riskTree.vehicles", "VEHICLE_REQUIRED", "Add at least one vehicle."));
        }

        if (!tree.Coverages.Any(c => c.Selected))
        {
            findings.Add(Item("riskTree.coverages", "COVERAGE_REQUIRED", "Select at least one coverage."));
        }

        foreach (var (driver, index) in tree.Drivers.Select((d, i) => (d, i)))
        {
            if (driver.VehicleLocator is { } locator && !vehicles.Contains(locator))
            {
                findings.Add(Item($"riskTree.drivers[{index}].vehicleLocator", "UNKNOWN_VEHICLE", "The driver is assigned to a vehicle that is not on the draft."));
            }
        }

        foreach (var (coverage, index) in tree.Coverages.Select((c, i) => (c, i)))
        {
            if (coverage.ElementLocator is { } locator && !vehicles.Contains(locator))
            {
                findings.Add(Item($"riskTree.coverages[{index}].elementLocator", "UNKNOWN_VEHICLE", "The coverage is attached to a vehicle that is not on the draft."));
            }
        }

        foreach (var group in tree.Drivers.Where(d => d.VehicleLocator is not null && d.UsagePercent is not null).GroupBy(d => d.VehicleLocator!))
        {
            if (group.Sum(d => d.UsagePercent!.Value) > 100)
            {
                findings.Add(Item("riskTree.drivers", "USAGE_OVER_100", $"Driver usage of vehicle {group.Key} exceeds 100 %."));
            }
        }

        return findings;
    }

    private async Task<string?> NormalisePlateAsync(string plate, string jurisdiction, CancellationToken cancellationToken)
    {
        try
        {
            var result = await idValidator.NormaliseAsync(plate, new IdValidationContext(jurisdiction), cancellationToken).ConfigureAwait(false);
            return result.Normalised;
        }
        catch (SpiException ex) when (ex.Category == SpiErrorCategory.NotApplicable)
        {
            // The bound pack has no plate rule: keep the plate as entered (no plate rule in core, REQ-POL-279).
            return null;
        }
    }

    private static string NewLocator() => Guid.CreateVersion7().ToString();

    private static void Upsert<T>(List<T> items, T item, Func<T, string?> key)
    {
        var index = items.FindIndex(existing => string.Equals(key(existing), key(item), StringComparison.Ordinal));
        if (index >= 0)
        {
            items[index] = item;
        }
        else
        {
            items.Add(item);
        }
    }

    private static JobUpdateDraftResponse.ValidationItem Item(string field, string code, string message) => new() { Field = field, Code = code, Message = message };

    private static DomainError Invalid(string field, string code, string message) =>
        new(ErrorCode.For(ModuleCode.POL, "VALIDATION"), message) { FieldErrors = [new FieldError(field, code, "pol." + code.ToLowerInvariant(), message)] };
}
