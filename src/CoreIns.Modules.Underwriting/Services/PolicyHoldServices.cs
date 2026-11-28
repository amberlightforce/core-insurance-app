using CoreIns.Modules.Underwriting.Commands;
using CoreIns.Modules.Underwriting.Contracts;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Platform.Errors;

namespace CoreIns.Modules.Underwriting.Services;

/// <summary><c>uw.PolicyHold.check</c> in-process (D-ARC-16): which Active holds apply to a quote.</summary>
internal sealed class UnderwritingPolicyHoldService(PolicyHoldQueries queries) : IUnderwritingPolicyHoldService
{
    public Task<PolicyHoldCheckResponse> CheckAsync(PolicyHoldCheckRequest request, CancellationToken cancellationToken = default) =>
        queries.CheckAsync(request, cancellationToken);
}

/// <summary><c>uw.Accumulation.check</c> in-process (D-ARC-16).</summary>
internal sealed class UnderwritingAccumulationService(PolicyHoldQueries queries) : IUnderwritingAccumulationService
{
    public async Task<AccumulationCheckResponse> CheckAsync(AccumulationCheckRequest request, CancellationToken cancellationToken = default)
    {
        var result = await queries.AccumulationAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? result.Value : throw new DomainException(result.Error);
    }
}
