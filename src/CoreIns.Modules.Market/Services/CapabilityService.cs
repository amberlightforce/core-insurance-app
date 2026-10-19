using System.Text.Json;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.Extensions.Configuration;

namespace CoreIns.Modules.Market.Services;

/// <summary>
/// <c>mkt.Capability.get</c> (REQ-MKT-004): the state of a capability switch. <c>scope</c> is the jurisdiction (default: the stamp's
/// <c>Stamp:Country</c>). Minimal resolve for the keys of <see cref="CapabilityCatalogue"/>; an unknown switch is refused.
/// </summary>
internal sealed class MarketCapabilityService(IConfiguration configuration) : IMarketCapabilityService
{
    public Task<CapabilityGetResponse> GetAsync(ValidAt? validAt = null, string? @switch = null, string? scope = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(@switch))
        {
            throw new DomainException(DomainError.Of(ModuleCode.MKT, "CFG-VALIDATION", "A capability switch key is required."));
        }

        var jurisdiction = string.IsNullOrWhiteSpace(scope) ? configuration["Stamp:Country"] ?? "GR" : scope;
        var state = CapabilityCatalogue.State(@switch, jurisdiction);
        var source = JsonSerializer.SerializeToElement(new { kind = "CODE_DEFAULT", jurisdiction });
        return Task.FromResult(new CapabilityGetResponse { State = state, Source = source });
    }
}
