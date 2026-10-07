using System.Text.Json.Nodes;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Platform.Configuration;

/// <summary>The six configuration layers, least to most specific (REQ-MKT-030, BR-MKT-001).</summary>
public enum ConfigLayer
{
    /// <summary>L0 core.</summary>
    Core = 0,

    /// <summary>L1 group (e.g. FFH).</summary>
    Group = 1,

    /// <summary>L2 region (EU).</summary>
    Region = 2,

    /// <summary>L3 country.</summary>
    Country = 3,

    /// <summary>L4 legal entity.</summary>
    LegalEntity = 4,

    /// <summary>L5 product / channel.</summary>
    ProductChannel = 5,
}

/// <summary>
/// A layer node (REQ-MKT-031): <c>core</c>, <c>group:FFH</c>, <c>region:EU</c>, <c>country:GR</c>, <c>entity:&lt;id&gt;</c>,
/// <c>product:&lt;code&gt;</c>, <c>channel:&lt;code&gt;</c>, <c>product:&lt;code&gt;+channel:&lt;code&gt;</c>. Inside L5,
/// product+channel beats product, which beats channel (REQ-MKT-032).
/// </summary>
/// <param name="Layer">Layer.</param>
/// <param name="Name">Node name.</param>
public sealed record LayerNode(ConfigLayer Layer, string Name)
{
    /// <summary>The core node.</summary>
    public static LayerNode Core { get; } = new(ConfigLayer.Core, "core");

    /// <summary>A group node.</summary>
    public static LayerNode Group(string code) => new(ConfigLayer.Group, "group:" + code);

    /// <summary>A region node.</summary>
    public static LayerNode Region(string code) => new(ConfigLayer.Region, "region:" + code);

    /// <summary>A country node.</summary>
    public static LayerNode Country(Jurisdiction country) => new(ConfigLayer.Country, "country:" + country.Value);

    /// <summary>A legal-entity node.</summary>
    public static LayerNode Entity(LegalEntityCode entity) => new(ConfigLayer.LegalEntity, "entity:" + entity.Value);

    /// <summary>A product node.</summary>
    public static LayerNode Product(ProductCode product) => new(ConfigLayer.ProductChannel, "product:" + product.Value);

    /// <summary>A channel node.</summary>
    public static LayerNode Channel(string channel) => new(ConfigLayer.ProductChannel, "channel:" + channel);

    /// <summary>A product+channel node.</summary>
    public static LayerNode ProductAndChannel(ProductCode product, string channel) =>
        new(ConfigLayer.ProductChannel, $"product:{product.Value}+channel:{channel}");

    /// <summary>Specificity: layer first, then product+channel &gt; product &gt; channel inside L5.</summary>
    public int Specificity => ((int)Layer * 10) + (Layer != ConfigLayer.ProductChannel ? 0
        : Name.Contains('+', StringComparison.Ordinal) ? 3
        : Name.StartsWith("product:", StringComparison.Ordinal) ? 2
        : 1);

    /// <summary>The node name.</summary>
    public override string ToString() => Name;
}

/// <summary>The context a value is resolved for (REQ-MKT-001 inputs).</summary>
/// <param name="LegalEntity">Legal entity.</param>
/// <param name="Jurisdiction">Jurisdiction.</param>
public sealed record ResolutionContext(LegalEntityCode LegalEntity, Jurisdiction Jurisdiction)
{
    /// <summary>Group code (L1), if the entity belongs to one.</summary>
    public string? Group { get; init; }

    /// <summary>Region code (L2), e.g. EU.</summary>
    public string? Region { get; init; }

    /// <summary>Product (L5).</summary>
    public ProductCode? Product { get; init; }

    /// <summary>Channel code (L5).</summary>
    public string? Channel { get; init; }

    /// <summary>The nodes that apply to this context, least specific first.</summary>
    public IReadOnlyList<LayerNode> ApplicableNodes()
    {
        var nodes = new List<LayerNode> { LayerNode.Core };
        if (Group is { Length: > 0 } group)
        {
            nodes.Add(LayerNode.Group(group));
        }

        if (Region is { Length: > 0 } region)
        {
            nodes.Add(LayerNode.Region(region));
        }

        nodes.Add(LayerNode.Country(Jurisdiction));
        nodes.Add(LayerNode.Entity(LegalEntity));
        if (Channel is { Length: > 0 } channel)
        {
            nodes.Add(LayerNode.Channel(channel));
        }

        if (Product is { } product)
        {
            nodes.Add(LayerNode.Product(product));
            if (Channel is { Length: > 0 } c)
            {
                nodes.Add(LayerNode.ProductAndChannel(product, c));
            }
        }

        return nodes;
    }
}

/// <summary>One stored version of a configuration value: key, node, value, final flag, business validity and record time.</summary>
/// <param name="Key">Key.</param>
/// <param name="Node">Layer node.</param>
/// <param name="Value">Value as JSON (decimals and money as strings).</param>
/// <param name="Final">A final value at a node blocks every more specific node (REQ-MKT-041).</param>
/// <param name="Valid">Business validity <c>[from, to)</c>.</param>
/// <param name="Recorded">Record time <c>[from, to)</c>.</param>
/// <param name="VersionId">Value version id.</param>
public sealed record ConfigValueVersion(
    ConfigKey Key, LayerNode Node, JsonNode? Value, bool Final, InstantRange Valid, InstantRange Recorded, string VersionId);

/// <summary>A resolved value with its source (REQ-MKT-001 output per key).</summary>
/// <param name="Key">Key.</param>
/// <param name="Value">Value.</param>
/// <param name="Source">Node the value came from.</param>
/// <param name="Final">True when the value is final at its node.</param>
/// <param name="VersionId">Value version id.</param>
/// <param name="Validity">Business validity of the version.</param>
public sealed record ResolvedValue(ConfigKey Key, JsonNode? Value, LayerNode Source, bool Final, string VersionId, InstantRange Validity)
{
    /// <summary>The source layer.</summary>
    public ConfigLayer Layer => Source.Layer;
}

/// <summary>The result of a resolution: values by key, keys with no value, and the configuration hash of the state used.</summary>
/// <param name="Values">Resolved values.</param>
/// <param name="Missing">Requested keys without a value (callers fail closed; never defaults, NFR-MKT-011).</param>
/// <param name="Hash">Configuration hash of the state resolved against.</param>
/// <param name="ValidAt">Business instant.</param>
/// <param name="KnownAt">Record instant.</param>
public sealed record ConfigurationResolution(
    IReadOnlyDictionary<ConfigKey, ResolvedValue> Values, IReadOnlyList<ConfigKey> Missing, ConfigurationHash Hash, Instant ValidAt, Instant KnownAt);

/// <summary>
/// The configuration runtime (<c>mkt.Configuration.resolve</c>, implemented by PLT; REQ-MKT-001/044–048). The six-layer
/// model, merge types other than REPLACE, change workflow and persistence are W1-MKT; this interface fixes the shape.
/// </summary>
public interface IConfigurationResolver
{
    /// <summary>Resolves <paramref name="keys"/> for a context at <paramref name="validAt"/> as known at <paramref name="knownAt"/> (default: now).</summary>
    Task<ConfigurationResolution> ResolveAsync(
        IReadOnlyCollection<ConfigKey> keys, ResolutionContext context, Instant validAt, Instant? knownAt, CancellationToken cancellationToken);

    /// <summary>The configuration hash of the whole state known at <paramref name="knownAt"/> (default: now).</summary>
    Task<ConfigurationHash> CurrentHashAsync(Instant? knownAt, CancellationToken cancellationToken);
}

/// <summary>
/// A simple in-memory resolver for tests and until W1-MKT: REPLACE semantics (most specific value wins) with final
/// values blocking more specific nodes, effective dating and record-time travel. The hash is SHA-256 over the RFC 8785
/// canonical JSON of the full state manifest (every value version known at <c>knownAt</c>), so it does not depend on
/// insertion order and changes whenever any version (including one <c>valid_to</c>) changes (REQ-MKT-046).
/// </summary>
public sealed class InMemoryConfigurationResolver(IClock clock) : IConfigurationResolver
{
    private readonly List<ConfigValueVersion> _versions = [];
    private readonly Lock _gate = new();

    /// <summary>Adds a value version.</summary>
    public InMemoryConfigurationResolver Add(ConfigValueVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        lock (_gate)
        {
            _versions.Add(version);
        }

        return this;
    }

    /// <inheritdoc />
    public Task<ConfigurationResolution> ResolveAsync(
        IReadOnlyCollection<ConfigKey> keys, ResolutionContext context, Instant validAt, Instant? knownAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(context);
        var known = knownAt ?? clock.Now;
        var nodes = context.ApplicableNodes();
        ConfigValueVersion[] state;
        lock (_gate)
        {
            state = [.. _versions.Where(v => v.Recorded.Contains(known))];
        }

        var values = new Dictionary<ConfigKey, ResolvedValue>();
        var missing = new List<ConfigKey>();
        foreach (var key in keys.Distinct())
        {
            var candidates = state
                .Where(v => v.Key == key && v.Valid.Contains(validAt) && nodes.Contains(v.Node))
                .OrderBy(v => v.Node.Specificity)
                .ToList();
            var winner = candidates.FirstOrDefault(v => v.Final) ?? candidates.LastOrDefault();
            if (winner is null)
            {
                missing.Add(key);
            }
            else
            {
                values[key] = new ResolvedValue(key, winner.Value?.DeepClone(), winner.Node, winner.Final, winner.VersionId, winner.Valid);
            }
        }

        return Task.FromResult(new ConfigurationResolution(values, missing, Hash(state), validAt, known));
    }

    /// <inheritdoc />
    public Task<ConfigurationHash> CurrentHashAsync(Instant? knownAt, CancellationToken cancellationToken)
    {
        var known = knownAt ?? clock.Now;
        ConfigValueVersion[] state;
        lock (_gate)
        {
            state = [.. _versions.Where(v => v.Recorded.Contains(known))];
        }

        return Task.FromResult(Hash(state));
    }

    /// <summary>The manifest hash of a state (order-independent).</summary>
    public static ConfigurationHash Hash(IEnumerable<ConfigValueVersion> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var manifest = new JsonArray();
        foreach (var version in state
                     .OrderBy(v => v.Key.Value, StringComparer.Ordinal)
                     .ThenBy(v => v.Node.Name, StringComparer.Ordinal)
                     .ThenBy(v => v.Valid.Start)
                     .ThenBy(v => v.VersionId, StringComparer.Ordinal))
        {
            manifest.Add(new JsonObject
            {
                ["key"] = version.Key.Value,
                ["node"] = version.Node.Name,
                ["value"] = version.Value?.DeepClone(),
                ["final"] = version.Final,
                ["validFrom"] = version.Valid.Start.ToString(),
                ["validTo"] = version.Valid.End?.ToString(),
                ["versionId"] = version.VersionId,
            });
        }

        return new ConfigurationHash(CanonicalJson.Hash(new JsonObject { ["manifestVersion"] = "1", ["values"] = manifest }));
    }
}
