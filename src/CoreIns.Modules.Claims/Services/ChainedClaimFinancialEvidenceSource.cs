using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Claims.Services;

/// <summary>An evidence owner: resolves the evidence kinds it owns, returns null for any other kind.</summary>
internal interface IClaimFinancialEvidenceProvider
{
    Task<ResolvedClaimFinancialEvidence?> ResolveAsync(ClaimId claimId, ClaimFinancialEvidence evidence, CancellationToken cancellationToken);
}

/// <summary>The engine's evidence source: the first provider that owns the kind answers; none → null (fail closed).</summary>
internal sealed class ChainedClaimFinancialEvidenceSource(IEnumerable<IClaimFinancialEvidenceProvider> providers) : IClaimFinancialEvidenceSource
{
    public async Task<ResolvedClaimFinancialEvidence?> ResolveAsync(ClaimId claimId, ClaimFinancialEvidence evidence, CancellationToken cancellationToken)
    {
        foreach (var provider in providers)
        {
            if (await provider.ResolveAsync(claimId, evidence, cancellationToken).ConfigureAwait(false) is { } resolved)
            {
                return resolved;
            }
        }

        return null;
    }
}
