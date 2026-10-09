using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Modules.Market.Domain;

/// <summary>Why a configuration state was written (<c>mkt.config_state.cause</c>, D-SL5-06).</summary>
internal static class StateCauses
{
    public const string Genesis = "GENESIS";
    public const string PackActivation = "PACK_ACTIVATION";
    public const string PackRollback = "PACK_ROLLBACK";

    public static bool IsKnown(string cause) => cause is Genesis or PackActivation or PackRollback;
}

/// <summary>
/// The content of one registered pack version (<c>mkt.pack_version</c>): immutable once registered (D-SL5-07). The content
/// digest is SHA-256 over the RFC 8785 canonical JSON of the values, sorted by key and validity, so it does not depend on the
/// order a source lists them in.
/// </summary>
/// <param name="PackId">Pack id, or <see cref="CoreDefaults.PackId"/> for the core defaults.</param>
/// <param name="Version">Semantic version.</param>
/// <param name="Country">Country whose L3 node the values populate; null for the core defaults.</param>
/// <param name="Digest">Content digest.</param>
/// <param name="Values">The values.</param>
internal sealed record PackVersionContent(string PackId, string Version, string? Country, Sha256Hash Digest, IReadOnlyList<PackConfigValue> Values)
{
    /// <summary>Builds the content record and computes its digest.</summary>
    public static PackVersionContent Of(string packId, string version, string? country, IReadOnlyList<PackConfigValue> values) =>
        new(packId, version, country, DigestOf(values), values);

    /// <summary>Content digest of a value list (order independent).</summary>
    public static Sha256Hash DigestOf(IReadOnlyList<PackConfigValue> values) => CanonicalJson.Hash(ToJson(values));

    /// <summary>The values as the JSON stored in <c>mkt.pack_version.values</c>: decimals and everything else as the text the source gave.</summary>
    public static JsonArray ToJson(IEnumerable<PackConfigValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var array = new JsonArray();
        foreach (var v in values
                     .OrderBy(v => v.Key, StringComparer.Ordinal)
                     .ThenBy(v => v.ValidFrom?.ToString(), StringComparer.Ordinal)
                     .ThenBy(v => v.ValidTo?.ToString(), StringComparer.Ordinal))
        {
            array.Add(new JsonObject
            {
                ["key"] = v.Key,
                ["type"] = v.Type.ToString(),
                ["value"] = v.Value,
                ["legalStatus"] = v.LegalStatus.ToString(),
                ["sourceRef"] = v.SourceRef,
                ["motorPath"] = v.MotorPath,
                ["validFrom"] = v.ValidFrom?.ToString(),
                ["validTo"] = v.ValidTo?.ToString(),
                ["note"] = v.Note,
            });
        }

        return array;
    }

    /// <summary>Reads the stored JSON back; any malformed row is an error (never skipped).</summary>
    public static List<PackConfigValue> FromJson(JsonNode? node)
    {
        var values = new List<PackConfigValue>();
        foreach (var item in node as JsonArray ?? throw new InvalidOperationException("Pack version values are not a JSON array."))
        {
            var o = item as JsonObject ?? throw new InvalidOperationException("A pack version value is not a JSON object.");
            values.Add(new PackConfigValue(
                Text(o, "key"),
                Enum.Parse<ConfigValueType>(Text(o, "type")),
                Text(o, "value"),
                Enum.Parse<LegalStatus>(Text(o, "legalStatus")),
                Text(o, "sourceRef"),
                (bool?)o["motorPath"] ?? throw new InvalidOperationException("Pack version value has no 'motorPath'."),
                Date(o, "validFrom"),
                Date(o, "validTo"),
                (string?)o["note"]));
        }

        return values;
    }

    private static string Text(JsonObject o, string name) =>
        (string?)o[name] ?? throw new InvalidOperationException($"Pack version value has no '{name}'.");

    private static BusinessDate? Date(JsonObject o, string name) =>
        (string?)o[name] is { } text
            ? (BusinessDate.TryParse(text, out var parsed) ? parsed : throw new InvalidOperationException($"'{text}' is not a date."))
            : null;
}

/// <summary>One pack version named by a state manifest.</summary>
/// <param name="PackId">Pack id.</param>
/// <param name="Version">Semantic version.</param>
/// <param name="Digest">Content digest of that version (a version never changes, so the digest is part of the identity).</param>
/// <param name="Country">Country node the values populate.</param>
internal sealed record ManifestPack(string PackId, string Version, Sha256Hash Digest, string Country);

/// <summary>
/// The manifest of a configuration state (REQ-MKT-046, D-SL5-06): which pack versions and which core defaults are in force,
/// chained to the state before it. Its hash is SHA-256 over the RFC 8785 canonical JSON, so it does not depend on the order the
/// packs are listed in (P-06) and changes with any pack version, digest, core digest, parent or cause. The parent is part of
/// the manifest, so re-activating a combination seen before gives a new state and the chain stays a linear history (REQ-MKT-048).
/// </summary>
/// <param name="Packs">The pack versions in force.</param>
/// <param name="CoreDigest">Content digest of the core defaults.</param>
/// <param name="ParentHash">The state this one replaces; null only for genesis.</param>
/// <param name="Cause">GENESIS, PACK_ACTIVATION or PACK_ROLLBACK.</param>
internal sealed record ConfigStateManifest(IReadOnlyList<ManifestPack> Packs, Sha256Hash CoreDigest, ConfigurationHash? ParentHash, string Cause)
{
    private const string ManifestVersion = "1";

    /// <summary>The state hash.</summary>
    public ConfigurationHash Hash => new(CanonicalJson.Hash(ToJson()));

    /// <summary>The manifest JSON (pack order is normalised here; canonicalisation sorts the keys).</summary>
    public JsonObject ToJson()
    {
        if (!StateCauses.IsKnown(Cause))
        {
            throw new InvalidOperationException($"'{Cause}' is not a state cause.");
        }

        if ((Cause == StateCauses.Genesis) != (ParentHash is null))
        {
            throw new InvalidOperationException("Only the genesis state has no parent.");
        }

        if (Packs.GroupBy(p => p.PackId, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            throw new InvalidOperationException("A state names one version per pack.");
        }

        var packs = new JsonArray();
        foreach (var p in Packs.OrderBy(p => p.PackId, StringComparer.Ordinal))
        {
            packs.Add(new JsonObject
            {
                ["packId"] = p.PackId,
                ["version"] = p.Version,
                ["digest"] = p.Digest.ToString(),
                ["country"] = p.Country,
            });
        }

        return new JsonObject
        {
            ["manifestVersion"] = ManifestVersion,
            ["coreDigest"] = CoreDigest.ToString(),
            ["packVersions"] = packs,
            ["parentHash"] = ParentHash?.ToString(),
            ["cause"] = Cause,
        };
    }

    /// <summary>Reads a stored manifest. The caller checks that its hash is the state's primary key.</summary>
    public static ConfigStateManifest FromJson(JsonNode? node)
    {
        var o = node as JsonObject ?? throw new InvalidOperationException("A state manifest is not a JSON object.");
        if ((string?)o["manifestVersion"] != ManifestVersion)
        {
            throw new InvalidOperationException($"Unknown manifest version '{(string?)o["manifestVersion"]}'.");
        }

        var packs = new List<ManifestPack>();
        foreach (var item in o["packVersions"] as JsonArray ?? throw new InvalidOperationException("A state manifest has no packVersions."))
        {
            var p = item as JsonObject ?? throw new InvalidOperationException("A manifest pack is not an object.");
            packs.Add(new ManifestPack(
                (string?)p["packId"] ?? throw new InvalidOperationException("Manifest pack without id."),
                (string?)p["version"] ?? throw new InvalidOperationException("Manifest pack without version."),
                Sha256Hash.Parse((string?)p["digest"] ?? string.Empty),
                (string?)p["country"] ?? throw new InvalidOperationException("Manifest pack without country.")));
        }

        return new ConfigStateManifest(
            packs,
            Sha256Hash.Parse((string?)o["coreDigest"] ?? string.Empty),
            (string?)o["parentHash"] is { } parent ? ConfigurationHash.Parse(parent) : null,
            (string?)o["cause"] ?? throw new InvalidOperationException("A state manifest has no cause."));
    }

    /// <summary>The genesis manifest over the newest shipped version of each pack.</summary>
    public static ConfigStateManifest Genesis(IEnumerable<PackVersionContent> newestPacks, Sha256Hash coreDigest) =>
        new([.. newestPacks.Select(p => new ManifestPack(p.PackId, p.Version, p.Digest, p.Country ?? throw new InvalidOperationException("A pack has a country.")))],
            coreDigest, null, StateCauses.Genesis);
}
