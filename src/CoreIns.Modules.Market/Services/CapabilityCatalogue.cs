using System.Text.Json;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Market.Services;

/// <summary>
/// The capability switches the code looks up (REQ-MKT-004). A switch is code-declared: its default is OFF, and each jurisdiction may
/// turn it on. Load validation (<see cref="Validate"/>) refuses a key the code never looks up (PITFALLS 37), so pack data cannot carry a
/// dead switch. SL4-MKT-FS ships one switch, <c>cap.clm.friendly_settlement</c> (REVERSIBLE, off by default, on for GR). The switch is
/// resolved the same in every environment: in Production the key may resolve on, but the SPI is unbound (country-pack binding), so
/// Friendly Settlement still fails closed (D-SL4-03).
/// TODO(hardening): move the per-jurisdiction state into versioned pack data (needs a GR/CY pack version bump, owned by the MKT state WPs).
/// </summary>
internal static class CapabilityCatalogue
{
    /// <summary>The key of Friendly Settlement.</summary>
    public const string FriendlySettlement = IFriendlySettlementClearing.CapabilityKey;

    /// <summary>Declared switches: key and the jurisdictions on which it is on.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Switches { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [FriendlySettlement] = new HashSet<string>(["GR"], StringComparer.OrdinalIgnoreCase),
        };

    /// <summary>Throws when a key is not a switch the code looks up.</summary>
    public static void Validate(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var key in keys)
        {
            if (!Switches.ContainsKey(key))
            {
                throw new DomainException(DomainError.Of(ModuleCode.MKT, "CFG-UNKNOWN-KEY", $"'{key}' is not a capability switch the code looks up (PITFALLS 37)."));
            }
        }
    }

    /// <summary>Whether <paramref name="key"/> is on for <paramref name="jurisdiction"/>.</summary>
    public static bool IsOn(string key, string jurisdiction)
    {
        Validate([key]);
        return Switches[key].Contains(jurisdiction);
    }

    /// <summary>The <c>state</c> JSON of the switch.</summary>
    public static JsonElement State(string key, string jurisdiction) =>
        JsonSerializer.SerializeToElement(new { @switch = key, enabled = IsOn(key, jurisdiction), reversible = true });
}
