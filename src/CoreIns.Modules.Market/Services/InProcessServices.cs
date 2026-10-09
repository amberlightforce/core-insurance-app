using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.SharedKernel;

namespace CoreIns.Modules.Market.Services;

/// <summary>
/// The in-process contract <see cref="IMarketConfigurationService"/> (D-ARC-16): <c>mkt.Configuration.resolve</c> and
/// <c>currentHash</c>. Plain reads over immutable configuration states, not pipeline commands: nothing is stored, so no
/// idempotency record can hold a result (SL-0 review). <c>resolve</c> serves the request's <c>configurationHash</c> (any recorded
/// state), else the state at <c>knownAt</c>, else the hash pinned for the unit of work (REQ-MKT-051), else the current state;
/// <c>currentHash</c> is always the current state (cached at most 1 s, REQ-MKT-050). The other MKT operations arrive with their work packages.
/// </summary>
internal sealed class MarketConfigurationService(ConfigurationEngine engine, RequestContext? context = null) : IMarketConfigurationService
{
    public Task<ConfigurationCurrentHashResponse> CurrentHashAsync(CancellationToken cancellationToken = default) =>
        engine.CurrentHashAsync(cancellationToken);

    public Task<ConfigurationResolveResponse> ResolveAsync(
        ConfigurationResolveRequest request, ValidAt? validAt = null, Instant? knownAt = null, CancellationToken cancellationToken = default) =>
        engine.ResolveAsync(request, validAt, knownAt, context?.ConfigurationHash, cancellationToken);
}

/// <summary>The in-process contract <see cref="IMarketRoundingService"/>: <c>mkt.Rounding.apply</c> (REQ-MKT-195).</summary>
internal sealed class MarketRoundingService(ConfigurationEngine engine, RequestContext? context = null) : IMarketRoundingService
{
    public Task<RoundingApplyResponse> ApplyAsync(RoundingApplyRequest request, CancellationToken cancellationToken = default) =>
        engine.ApplyRoundingAsync(request, context?.ConfigurationHash, cancellationToken);
}
