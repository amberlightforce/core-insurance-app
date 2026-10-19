using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Contracts;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Reinsurance.Recovery;

/// <summary>In-process <c>ri.Recovery.listByClaim</c> for CLM (D-ARC-16): the same read as the HTTP query.</summary>
internal sealed class ReinsuranceRecoveryService(RecoveryReader reader) : IReinsuranceRecoveryService
{
    public async Task<RecoveryListByClaimPage> ListByClaimAsync(
        ClaimId claimId, ValidAt? validAt = null, Instant? knownAt = null, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var page = await reader.ByClaimAsync(claimId.Value, knownAt, cursor, limit, cancellationToken).ConfigureAwait(false);
        return page.IsFailure
            ? throw new DomainException(page.Error!)
            : new RecoveryListByClaimPage { Items = [.. page.Value.Items.Select(RecoveryReader.ToClaimItem)], NextCursor = page.Value.Next, Limit = page.Value.Limit };
    }
}
