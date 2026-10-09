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

/// <summary>The content a state is built from: its manifest, the core defaults and the content of every pack version it names.</summary>
/// <param name="Manifest">The state manifest.</param>
/// <param name="Core">The core defaults.</param>
/// <param name="Packs">One content record per pack version in the manifest.</param>
internal sealed record CatalogueState(ConfigStateManifest Manifest, PackVersionContent Core, IReadOnlyList<PackVersionContent> Packs)
{
    /// <summary>The genesis state of the shipped sources: the newest version of each (D-SL5-06).</summary>
    public static CatalogueState Genesis(IEnumerable<IPackConfigurationSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var packs = sources.Select(s => PackVersionContent.Of(s.PackId, s.PackVersion, s.Country, s.Values)).ToList();
        return new CatalogueState(ConfigStateManifest.Genesis(packs, CoreDefaults.Content.Digest), CoreDefaults.Content, packs);
    }
}

/// <summary>
/// One configuration state (REQ-MKT-046, D-SL5-06): core defaults (L0) plus the country-layer values (L3) of the pack versions the
/// state manifest names, validated against the key registry. Immutable once built, so it can be cached by hash for ever; the hash is
/// the hash of the state manifest (<see cref="ConfigStateManifest"/>), independent of registration order and changing with any pack
/// version, digest, core digest or parent. Layers L1, L2, L4 and L5 hold no values in the slice.
/// </summary>
internal sealed class ConfigurationCatalogue
{
    private readonly List<ConfigEntry> _entries = [];

    /// <summary>Builds the genesis state of <paramref name="sources"/> (the newest version of each), active from <paramref name="activatedAt"/>.</summary>
    public ConfigurationCatalogue(IEnumerable<IPackConfigurationSource> sources, Instant activatedAt)
        : this(CatalogueState.Genesis(sources), activatedAt)
    {
    }

    /// <summary>Builds the state described by <paramref name="state"/>; every digest the manifest names must match the content given.</summary>
    public ConfigurationCatalogue(CatalogueState state, Instant activatedAt)
    {
        ArgumentNullException.ThrowIfNull(state);
        ActivatedAt = activatedAt;
        Manifest = state.Manifest;
        Hash = state.Manifest.Hash;
        if (state.Core.Digest != state.Manifest.CoreDigest || PackVersionContent.DigestOf(state.Core.Values) != state.Manifest.CoreDigest)
        {
            throw new InvalidOperationException("The core defaults do not match the core digest of the state manifest.");
        }

        foreach (var core in state.Core.Values)
        {
            Add(core, ConfigEntry.CoreNode, packId: null, packVersion: null);
        }

        foreach (var named in state.Manifest.Packs)
        {
            var content = state.Packs.SingleOrDefault(p => p.PackId == named.PackId && p.Version == named.Version)
                ?? throw new InvalidOperationException($"Pack version {named.PackId}@{named.Version} named by the state manifest is not registered.");
            if (content.Digest != named.Digest || PackVersionContent.DigestOf(content.Values) != named.Digest)
            {
                throw new InvalidOperationException($"Pack version {named.PackId}@{named.Version} does not match the content digest of the state manifest.");
            }

            foreach (var value in content.Values)
            {
                Add(value, "country:" + named.Country, named.PackId, named.Version);
            }
        }
    }

    /// <summary>The manifest this state was built from.</summary>
    public ConfigStateManifest Manifest { get; }

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
}

/// <summary>
/// Core-layer defaults (L0, REQ-MKT-069). Only values the PRDs state: the rounding defaults of REQ-MKT-192/194 and
/// the core order of operations (PRD-17 section 7.6). Tax-line rounding is flagged Unverified because no Greek rounding
/// value exists (PRD-17 section 16.5 decision 10, still to confirm with finance).
/// </summary>
internal static class CoreDefaults
{
    /// <summary>Id under which the core defaults are registered in <c>mkt.pack_version</c> (not a country pack).</summary>
    public const string PackId = "core";

    /// <summary>
    /// Version of the core defaults. They are code, but a state records their digest and must be able to rebuild them (REQ-MKT-048):
    /// changing any default below without bumping this version fails the startup digest check.
    /// </summary>
    public const string Version = "1.0.0";

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

    /// <summary>The core defaults as a registrable version (declared after <see cref="Values"/>: static initialisers run in order).</summary>
    public static PackVersionContent Content { get; } = PackVersionContent.Of(PackId, Version, null, Values);
}
