using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Modules.Market.Domain;

/// <summary>One stored version of a configuration value at a layer node (REQ-MKT-031, REQ-MKT-042).</summary>
internal sealed record ConfigEntry(
    ConfigKeyDescriptor Descriptor,
    string Key,
    string Node,
    ConfigValueType Type,
    string Value,
    LegalStatus LegalStatus,
    string SourceRef,
    bool MotorPath,
    BusinessDate? ValidFrom,
    BusinessDate? ValidTo,
    string? PackId,
    string? PackVersion,
    string? Note)
{
    /// <summary>Layer node name: <c>core</c> or <c>country:GR</c>.</summary>
    public bool IsCore => Node == CoreNode;

    public const string CoreNode = "core";

    /// <summary>The validity as a half-open date range (open start = the earliest representable date).</summary>
    public DateRange Validity => new(ValidFrom ?? BusinessDate.MinValue, ValidTo);

    public bool Contains(BusinessDate at) => Validity.Contains(at);

    /// <summary>Stable id of this version: SHA-256 of node, key, pack version and start of validity.</summary>
    public Guid VersionId
    {
        get
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{Node}|{Key}|{PackVersion}|{ValidFrom}|{ValidTo}|{Value}"));
            return new Guid(hash.AsSpan(0, 16));
        }
    }

    /// <summary>The value as JSON: decimals as strings (never numbers), objects parsed.</summary>
    public JsonElement ToJsonElement() => Type switch
    {
        ConfigValueType.WholeNumber => JsonSerializer.SerializeToElement(long.Parse(Value, CultureInfo.InvariantCulture)),
        ConfigValueType.Flag => JsonSerializer.SerializeToElement(bool.Parse(Value)),
        ConfigValueType.Json => JsonDocument.Parse(Value).RootElement.Clone(),
        _ => JsonSerializer.SerializeToElement(Value),
    };

    /// <summary>True when the value may be served in Production (D-REG-02, REQ-MKT-343).</summary>
    public bool IsSettled => LegalStatus is LegalStatus.Settled or LegalStatus.NotRegulatory;
}

/// <summary>
/// The configuration state of the stamp: core defaults (L0) plus the country-layer values (L3) of every bound pack source,
/// validated against the key registry. Immutable; the hash is SHA-256 over the RFC 8785 canonical JSON of the manifest of
/// all value versions (REQ-MKT-046), so it is independent of registration order and changes with any value or pack version.
/// Layers L1, L2, L4 and L5 hold no values in the slice.
/// </summary>
internal sealed class ConfigurationCatalogue
{
    private readonly List<ConfigEntry> _entries = [];

    public ConfigurationCatalogue(IEnumerable<IPackConfigurationSource> sources, Instant activatedAt)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ActivatedAt = activatedAt;
        foreach (var core in CoreDefaults.Values)
        {
            Add(core, ConfigEntry.CoreNode, packId: null, packVersion: null);
        }

        foreach (var source in sources)
        {
            foreach (var value in source.Values)
            {
                Add(value, "country:" + source.Country, source.PackId, source.PackVersion);
            }
        }

        Hash = ComputeHash();
    }

    public Instant ActivatedAt { get; }

    public ConfigurationHash Hash { get; }

    public IReadOnlyList<ConfigEntry> Entries => _entries;

    /// <summary>The value of <paramref name="key"/> valid on <paramref name="at"/>: the country node beats core (REQ-MKT-030, REQ-MKT-036 REPLACE).</summary>
    public ConfigEntry? Find(string key, string country, BusinessDate at) =>
        _entries.Where(e => e.Key == key && e.Contains(at) && (e.IsCore || e.Node == "country:" + country))
            .OrderBy(e => e.IsCore ? 0 : 1)
            .LastOrDefault();

    private void Add(PackConfigValue value, string node, string? packId, string? packVersion)
    {
        var descriptor = ConfigKeys.Find(value.Key)
            ?? throw new InvalidOperationException($"Pack value '{value.Key}' is not a registered configuration key (REQ-MKT-033).");
        if (descriptor.Type != value.Type)
        {
            throw new InvalidOperationException($"Pack value '{value.Key}' has type {value.Type}, the key registry says {descriptor.Type}.");
        }

        Validate(value);
        if (value.Key.StartsWith(TaxTreatmentRules.KeyPrefix, StringComparison.Ordinal))
        {
            TaxTreatmentRules.ValidateRow(value.Key, value.Value, value.LegalStatus);
        }

        if (string.IsNullOrWhiteSpace(value.SourceRef))
        {
            throw new InvalidOperationException($"Pack value '{value.Key}' has no source reference (D-REG-01).");
        }

        var entry = new ConfigEntry(
            descriptor, value.Key, node, value.Type, value.Value, value.LegalStatus, value.SourceRef, value.MotorPath,
            value.ValidFrom, value.ValidTo, packId, packVersion, value.Note);
        if (_entries.Any(e => e.Key == entry.Key && e.Node == entry.Node && e.Validity.Overlaps(entry.Validity)))
        {
            throw new InvalidOperationException($"Overlapping validity for '{value.Key}' at {node} (REQ-MKT-042).");
        }

        _entries.Add(entry);
    }

    private static void Validate(PackConfigValue value)
    {
        var ok = value.Type switch
        {
            ConfigValueType.ExactDecimal => decimal.TryParse(value.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out _) && !value.Value.Contains('E', StringComparison.OrdinalIgnoreCase),
            ConfigValueType.WholeNumber => long.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            ConfigValueType.Flag => bool.TryParse(value.Value, out _),
            ConfigValueType.Json => IsJson(value.Value),
            _ => !string.IsNullOrEmpty(value.Value),
        };
        if (!ok)
        {
            throw new InvalidOperationException($"Pack value '{value.Key}' is not a valid {value.Type}.");
        }
    }

    private static bool IsJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private ConfigurationHash ComputeHash()
    {
        var manifest = new JsonArray();
        foreach (var e in _entries
                     .OrderBy(e => e.Key, StringComparer.Ordinal)
                     .ThenBy(e => e.Node, StringComparer.Ordinal)
                     .ThenBy(e => e.ValidFrom?.ToString(), StringComparer.Ordinal))
        {
            manifest.Add(new JsonObject
            {
                ["key"] = e.Key,
                ["node"] = e.Node,
                ["type"] = e.Type.ToString(),
                ["value"] = e.Value,
                ["legalStatus"] = e.LegalStatus.ToString(),
                ["sourceRef"] = e.SourceRef,
                ["motorPath"] = e.MotorPath,
                ["validFrom"] = e.ValidFrom?.ToString(),
                ["validTo"] = e.ValidTo?.ToString(),
                ["pack"] = e.PackId is null ? null : $"{e.PackId}@{e.PackVersion}",
            });
        }

        return new ConfigurationHash(CanonicalJson.Hash(new JsonObject { ["manifestVersion"] = "1", ["values"] = manifest }));
    }
}

/// <summary>
/// Core-layer defaults (L0, REQ-MKT-069). Only values the PRDs state: the rounding defaults of REQ-MKT-192/194 and
/// the core order of operations (PRD-17 section 7.6). Tax-line rounding is flagged Unverified because no Greek rounding
/// value exists (PRD-17 section 16.5 decision 10, still to confirm with finance).
/// </summary>
internal static class CoreDefaults
{
    private const string RoundingJson = """{"mode":"HALF_UP","scale":null,"level":"LINE"}""";

    public static IReadOnlyList<PackConfigValue> Values { get; } =
    [
        new(ConfigKeys.RoundingDefault, ConfigValueType.Json, RoundingJson, LegalStatus.NotRegulatory,
            "PRD-17 REQ-MKT-191/192 (minor units from ISO 4217; HALF_UP, example G EUR charge.line HALF_UP scale 2); scale null = currency minor units",
            MotorPath: false),
        new(ConfigKeys.RoundingPrefix + "charge.line", ConfigValueType.Json, RoundingJson, LegalStatus.NotRegulatory,
            "PRD-17 REQ-MKT-192 (G EUR purpose charge.line HALF_UP scale 2, W 2.345, T 2.35)", MotorPath: false),
        new(ConfigKeys.RoundingPrefix + "tax.line", ConfigValueType.Json, RoundingJson, LegalStatus.Unverified,
            "PRD-17 REQ-MKT-193 and section 16.5 decision 10: core default only; no Greek tax-line rounding value is stated",
            MotorPath: true,
            Note: "Confirm Greek rounding with finance. Per-tax-class rules cur.rounding.tax.<class> are absent."),
        new(CancellationSources.CodeList, ConfigValueType.Json,
            JsonSerializer.Serialize(CancellationSources.All), LegalStatus.NotRegulatory,
            "PRD-17 shared cancellation-source code list R-84; REQ-POL-205 (Policyholder, Insurer, NonPayment, DistanceWithdrawal, LongTermWithdrawal, Objection, Statutory)",
            MotorPath: false),
        new(ConfigKeys.OrderOfOperations, ConfigValueType.Json,
            """{"roundPremiumPerElement":true,"taxOn":"ROUNDED_BASE","documentTotal":"ROUND_THEN_SUM","instalmentRemainder":"FIRST"}""",
            LegalStatus.NotRegulatory,
            "PRD-17 section 7.6 / REQ-MKT-194 core default: round per line, tax on the rounded base, round-then-sum, remainder to the first instalment",
            MotorPath: false),
    ];
}
