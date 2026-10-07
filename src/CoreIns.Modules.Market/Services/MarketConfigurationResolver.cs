using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Platform.Configuration;
using CoreIns.Platform.Contracts;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Market.Services;

/// <summary>
/// The platform's <see cref="IConfigurationResolver"/> over MKT (D-SLC-15): MKT is the configuration authority, so the
/// hash the request middleware pins into the request context (and so into every event envelope) is the hash MKT
/// serves, and modules can hand that hash back to <c>mkt.Configuration.resolve</c> without CFG-HASH-UNKNOWN.
/// The slice keeps one catalogue state, so <c>knownAt</c> earlier than its activation is refused until the configuration
/// history of W1-MKT-01 exists.
/// </summary>
internal sealed class MarketConfigurationResolver(ConfigurationEngine engine) : IConfigurationResolver
{
    /// <inheritdoc />
    public Task<ConfigurationHash> CurrentHashAsync(Instant? knownAt, CancellationToken cancellationToken)
    {
        if (knownAt is { } known && known < engine.Catalogue.ActivatedAt)
        {
            throw new NotSupportedException("A configuration hash from before the current state needs the configuration history (W1-MKT-01).");
        }

        return Task.FromResult(engine.Catalogue.Hash);
    }

    /// <inheritdoc />
    public Task<ConfigurationResolution> ResolveAsync(
        IReadOnlyCollection<ConfigKey> keys, ResolutionContext context, Instant validAt, Instant? knownAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(context);
        var response = engine.Resolve(
            new ConfigurationResolveRequest
            {
                LegalEntity = context.LegalEntity.Value,
                Jurisdiction = context.Jurisdiction.Value,
                ProductCode = context.Product?.Value,
                Channel = context.Channel,
                Keys = [.. keys.Select(k => k.Value)],
            },
            ValidAt.From(validAt),
            knownAt);
        var values = new Dictionary<ConfigKey, ResolvedValue>();
        foreach (var item in response.Values)
        {
            var key = ConfigKey.Parse(item.Key);
            var validity = item.Validity is { } v
                ? new InstantRange(Instant.FromUtc(v.Start.Value.Year, v.Start.Value.Month, v.Start.Value.Day), v.End is { } end ? Instant.FromUtc(end.Value.Year, end.Value.Month, end.Value.Day) : null)
                : InstantRange.Open(Instant.MinValue);
            values[key] = new ResolvedValue(key, JsonNode.Parse(item.Value.GetRawText()), NodeOf(item.SourceLayer), item.Final, item.ValueVersionId.ToString(), validity);
        }

        return Task.FromResult(new ConfigurationResolution(
            values, [.. response.MissingKeys.Select(ConfigKey.Parse)], response.ConfigurationHash, validAt, knownAt ?? engine.Catalogue.ActivatedAt));
    }

    private static LayerNode NodeOf(string node) => node switch
    {
        "core" => LayerNode.Core,
        _ when node.StartsWith("group:", StringComparison.Ordinal) => new(ConfigLayer.Group, node),
        _ when node.StartsWith("region:", StringComparison.Ordinal) => new(ConfigLayer.Region, node),
        _ when node.StartsWith("country:", StringComparison.Ordinal) => new(ConfigLayer.Country, node),
        _ when node.StartsWith("entity:", StringComparison.Ordinal) => new(ConfigLayer.LegalEntity, node),
        _ => new(ConfigLayer.ProductChannel, node),
    };
}
