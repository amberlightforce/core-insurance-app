using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Platform.Contracts;
using CoreIns.SharedKernel;

namespace CoreIns.Modules.Market.Services;

/// <summary>
/// The in-process contract <see cref="IMarketConfigurationService"/> (D-ARC-16): <c>mkt.Configuration.resolve</c> and
/// <c>currentHash</c>. Plain reads over the immutable catalogue, not pipeline commands: nothing is stored, so no
/// idempotency record can hold a result (SL-0 review). The other MKT operations arrive with their work packages.
/// </summary>
internal sealed class MarketConfigurationService(ConfigurationEngine engine) : IMarketConfigurationService
{
    public Task<ConfigurationCurrentHashResponse> CurrentHashAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(engine.CurrentHash());

    public Task<ConfigurationResolveResponse> ResolveAsync(
        ConfigurationResolveRequest request, ValidAt? validAt = null, Instant? knownAt = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(engine.Resolve(request, validAt, knownAt));
}

/// <summary>The in-process contract <see cref="IMarketRoundingService"/>: <c>mkt.Rounding.apply</c> (REQ-MKT-195).</summary>
internal sealed class MarketRoundingService(ConfigurationEngine engine) : IMarketRoundingService
{
    public Task<RoundingApplyResponse> ApplyAsync(RoundingApplyRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(engine.ApplyRounding(request));
}
