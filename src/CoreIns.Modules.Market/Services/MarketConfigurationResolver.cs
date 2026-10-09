using System.Net.Sockets;
using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Platform.Configuration;
using CoreIns.Platform.Contracts;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CoreIns.Modules.Market.Services;

/// <summary>
/// The platform's <see cref="IConfigurationResolver"/> over MKT (D-SLC-15): MKT is the configuration authority, so the
/// hash the request middleware pins into the request context (and so into every event envelope) is the hash MKT
/// serves, and modules can hand that hash back to <c>mkt.Configuration.resolve</c> without CFG-HASH-UNKNOWN.
/// <c>knownAt</c> selects the state that was current at that instant (REQ-MKT-048); before the first state it is refused.
/// </summary>
internal sealed partial class MarketConfigurationResolver(ConfigurationEngine engine, ILogger<MarketConfigurationResolver> logger) : IConfigurationResolver
{
    /// <inheritdoc />
    public async Task<ConfigurationHash> CurrentHashAsync(Instant? knownAt, CancellationToken cancellationToken)
    {
        if (knownAt is { } known)
        {
            return (await engine.StateAsync(null, known, null, cancellationToken).ConfigureAwait(false)).Hash;
        }

        try
        {
            return (await engine.States.CurrentAsync(cancellationToken).ConfigureAwait(false)).Hash;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or SocketException or IOException)
        {
            // The request middleware pins a hash on every request, health probes included, so an unreachable database must not fail the probe.
            // The hash returned is the one this release writes as genesis; any read of configuration still goes to the database and fails there.
            CurrentStateUnreadable(logger, ex);
            return engine.States.ExpectedGenesisHash;
        }
    }

    /// <inheritdoc />
    public async Task<ConfigurationResolution> ResolveAsync(
        IReadOnlyCollection<ConfigKey> keys, ResolutionContext context, Instant validAt, Instant? knownAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(context);
        var state = await engine.StateAsync(null, knownAt, null, cancellationToken).ConfigureAwait(false);
        var response = engine.Resolve(
            state,
            new ConfigurationResolveRequest
            {
                LegalEntity = context.LegalEntity.Value,
                Jurisdiction = context.Jurisdiction.Value,
                ProductCode = context.Product?.Value,
                Channel = context.Channel,
                Keys = [.. keys.Select(k => k.Value)],
            },
            ValidAt.From(validAt));
        var values = new Dictionary<ConfigKey, ResolvedValue>();
        foreach (var item in response.Values)
        {
            var key = ConfigKey.Parse(item.Key);
            var validity = item.Validity is { } v
                ? new InstantRange(Instant.FromUtc(v.Start.Value.Year, v.Start.Value.Month, v.Start.Value.Day), v.End is { } end ? Instant.FromUtc(end.Value.Year, end.Value.Month, end.Value.Day) : null)
                : InstantRange.Open(Instant.MinValue);
            values[key] = new ResolvedValue(key, JsonNode.Parse(item.Value.GetRawText()), NodeOf(item.SourceLayer), item.Final, item.ValueVersionId.ToString(), validity);
        }

        return new ConfigurationResolution(
            values, [.. response.MissingKeys.Select(ConfigKey.Parse)], response.ConfigurationHash, validAt, knownAt ?? state.ActivatedAt);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The current configuration state cannot be read; pinning the release's genesis hash for this request.")]
    private static partial void CurrentStateUnreadable(ILogger logger, Exception exception);

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
