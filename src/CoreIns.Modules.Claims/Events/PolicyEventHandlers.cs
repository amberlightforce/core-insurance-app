using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Claims.Events;

/// <summary>
/// Shared part of the POL event consumers (REQ-CLM-057, D-SL3-03 d): resolve the policy and hand the effective date and the
/// cause event to the <see cref="RaiseReverification"/> scan. The outbox marker makes a redelivery a no-op, and the unique
/// (claim, cause) index makes a replay harmless. Order per aggregate is the outbox's (D-ARC-26); the scan reads POL's
/// current supersession, so it does not depend on the order in which two changes of one policy arrive.
/// </summary>
internal static class PolicyEventScan
{
    public static async Task RunAsync(
        ICommandHandler<RaiseReverification, int> raise, EventEnvelope envelope, BusinessDate effectiveDate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var policy = PolicyIdOf(envelope);
        ApprovalDecidedHandler.Unwrap(await raise.HandleAsync(
            new RaiseReverification(policy, effectiveDate, envelope.EventId.Value, envelope.EventType.Value), cancellationToken).ConfigureAwait(false));
    }

    private static PolicyId PolicyIdOf(EventEnvelope envelope)
    {
        if (envelope.AggregateType == "Policy" && Guid.TryParse(envelope.AggregateId, out var aggregate))
        {
            return new PolicyId(aggregate);
        }

        if (envelope.BusinessKeys.TryGetValue("policyId", out var key) && Guid.TryParse(key, out var fromKey))
        {
            return new PolicyId(fromKey);
        }

        throw new InvalidOperationException("The POL event carries no policy id.");
    }
}

/// <summary><c>pol.PolicyChanged</c> → re-verification of open claims whose loss is on or after the effective date (REQ-CLM-057).</summary>
internal sealed class PolicyChangedHandler(ICommandHandler<RaiseReverification, int> raise) : IEventHandler<PolicyChangedV1>
{
    public const string Name = "CLM.PolicyChanged.Reverify";

    public Task HandleAsync(EventEnvelope envelope, PolicyChangedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return PolicyEventScan.RunAsync(raise, envelope, payload.EffectiveDate, cancellationToken);
    }
}

/// <summary><c>pol.PolicyCancelled</c> → a loss on or after the cancellation's effective date loses cover (REQ-CLM-057).</summary>
internal sealed class PolicyCancelledHandler(ICommandHandler<RaiseReverification, int> raise) : IEventHandler<PolicyCancelledV1>
{
    public const string Name = "CLM.PolicyCancelled.Reverify";

    public Task HandleAsync(EventEnvelope envelope, PolicyCancelledV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return PolicyEventScan.RunAsync(raise, envelope, payload.EffectiveDate, cancellationToken);
    }
}
