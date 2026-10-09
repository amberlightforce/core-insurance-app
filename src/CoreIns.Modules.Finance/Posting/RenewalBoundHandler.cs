using System.Text.Json.Serialization;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Platform.Events;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.Posting;

/// <summary>FIN's view of POL RenewalBound: the fields FIN reads (a tolerant reader).</summary>
internal sealed record RenewalBoundContext
{
    [JsonPropertyName("newTermId")]
    public Guid NewTermId { get; init; }

    [JsonPropertyName("transactionId")]
    public Guid TransactionId { get; init; }

    [JsonPropertyName("productCode")]
    public string ProductCode { get; init; } = string.Empty;

    [JsonPropertyName("productVersion")]
    public string ProductVersion { get; init; } = string.Empty;

    [JsonPropertyName("artefactHash")]
    public string ArtefactHash { get; init; } = string.Empty;

    [JsonPropertyName("predecessorTermId")]
    public Guid? PredecessorTermId { get; init; }
}

/// <summary>
/// POL RenewalBound (CONTEXT, SL3-E2E integration fix): the policy context of renewal term n+1. The policy key comes from the
/// predecessor term's context; the BIL entries of the new term wait on <c>term:{newTermId}</c> until this arrives, exactly as for
/// PolicyBound (REQ-FIN-040). Without it the entries of a renewal term were never journalised.
/// </summary>
internal sealed class RenewalBoundHandler(FinanceDbContext db, PolicyBoundHandler policyBound) : IEventHandler<RenewalBoundContext>
{
    public async Task HandleAsync(EventEnvelope envelope, RenewalBoundContext payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(payload);
        var predecessorId = payload.PredecessorTermId ?? throw new InvalidOperationException("RenewalBound without predecessorTermId (contract: always set).");
        var predecessor = await db.PolicyContexts.AsNoTracking().SingleOrDefaultAsync(p => p.PolicyTermId == predecessorId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"RenewalBound for a term whose predecessor {predecessorId} has no policy context yet; retried.");
        await policyBound.HandleAsync(
            envelope,
            new PolicyBoundContext
            {
                PolicyId = predecessor.PolicyId,
                PolicyNumber = predecessor.PolicyNumber,
                TermId = payload.NewTermId,
                TransactionId = payload.TransactionId,
                ProductCode = payload.ProductCode,
                ProductVersion = payload.ProductVersion,
                ArtefactHash = payload.ArtefactHash,
            },
            cancellationToken).ConfigureAwait(false);
    }
}
