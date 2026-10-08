using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CoreIns.Modules.Claims.Services;

/// <summary>
/// <see cref="ICoverageSource"/> over the generated POL contract <see cref="IPolicySnapshotService"/>
/// (<c>pol.Snapshot.get(policyId, validAt = lossAt, knownAt = now)</c>, REQ-CLM-002, REQ-POL-007). POL answers success
/// with <c>inForce = false</c> and no content when no term is in force; an unknown policy is POL-ERR-NOT-FOUND.
/// </summary>
internal sealed partial class PolicySnapshotAdapter(IServiceProvider services, ILogger<PolicySnapshotAdapter> logger) : ICoverageSource
{
    public async Task<SnapshotRead> ReadAsync(Guid policyId, Instant lossAt, Instant knownAt, CancellationToken cancellationToken)
    {
        // Resolved per call: a deployment without POL still starts and FNOL answers 503.
        var snapshots = services.GetService<IPolicySnapshotService>();
        if (snapshots is null)
        {
            return SnapshotRead.Unavailable("IPolicySnapshotService is not registered in this deployment.");
        }

        SnapshotGetResponse response;
        try
        {
            response = await snapshots.GetAsync(
                validAt: ValidAt.From(lossAt), knownAt: knownAt, policyId: new PolicyId(policyId), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.POL && ex.Error.Code.Name is "NOT-FOUND" or "VALIDATION")
        {
            return SnapshotRead.Unverified($"POL answered {ex.Error.Code}.");
        }
        catch (DomainException ex)
        {
            LogFailed(logger, ex.Error.Code.Value);
            return SnapshotRead.Unavailable($"POL answered {ex.Error.Code}.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            // Any other POL failure is a dependency outage for CLM (CLM-ERR-DEPENDENCY-UNAVAILABLE, 503), never a 500.
            LogUnavailable(logger, ex);
            return SnapshotRead.Unavailable("POL did not answer.");
        }

        return Map(response, policyId);
    }

    /// <summary>Maps the typed snapshot; a reference without text or for another policy than asked is unverified.</summary>
    internal static SnapshotRead Map(SnapshotGetResponse response, Guid requestedPolicyId)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (string.IsNullOrWhiteSpace(response.SnapshotRef) || response.Policy is null)
        {
            return SnapshotRead.Unverified("The POL snapshot has no reference or no policy facts.");
        }

        if (response.Policy.PolicyId.Value != requestedPolicyId)
        {
            return SnapshotRead.Unverified("POL answered for another policy than the one asked.");
        }

        var content = response.InForce ? response.Content : null;
        return SnapshotRead.Found(new PolicySnapshotFacts(
            response.SnapshotRef,
            response.ValidAt,
            response.KnownAt,
            response.InForce,
            response.Status is { } status ? Codes.Of(status) : null,
            response.NotInForceReason is { } reason ? Codes.Of(reason) : null,
            response.Policy.PolicyId.Value,
            response.Policy.PolicyNumber.Value,
            response.Policy.ProductCode,
            content?.ProductVersion.ToString(),
            response.Policy.InsuredPartyId,
            content?.Segment.SegmentId.Value,
            [.. (content?.Coverages ?? []).Where(c => c.Selected).Select(c => c.CoverageCode).Distinct(StringComparer.Ordinal)]));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "pol.Snapshot.get failed with {Code}")]
    private static partial void LogFailed(ILogger logger, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "pol.Snapshot.get is unavailable")]
    private static partial void LogUnavailable(ILogger logger, Exception exception);
}
