using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Channels.Domain;

/// <summary>What CHN needs to know about the policy of a withdrawal.</summary>
/// <param name="ConcludedAt">When the contract was concluded (the window starts on that day).</param>
/// <param name="PolicyholderPartyId">The addressee of the acknowledgement.</param>
/// <param name="PolicyNumber">For the acknowledgement text.</param>
internal sealed record WithdrawalPolicyFacts(Instant ConcludedAt, PartyId PolicyholderPartyId, string PolicyNumber);

/// <summary>Result of the handoff to POL.</summary>
internal enum PolicySubmitOutcome
{
    /// <summary>POL accepted the request (void job started).</summary>
    Accepted,

    /// <summary>POL refused it because the right has expired.</summary>
    Expired,

    /// <summary>POL has no implementation bound yet: the WithdrawalRequestReceived event is the only path (backup, R-89).</summary>
    NotBound,
}

/// <summary>
/// The seam to POL (SPI <c>pol.Withdrawal.submit</c>, POL-WITHDRAW). A local interface so CHN builds before POL ships its
/// implementation and so the contract's still-untyped fields stay in one adapter.
/// </summary>
internal interface IWithdrawalPolicyPort
{
    /// <summary>The policy, or null when it does not exist for the legal entity.</summary>
    Task<WithdrawalPolicyFacts?> FindAsync(PolicyId policyId, CancellationToken cancellationToken);

    /// <summary><c>IPolicyWithdrawalService.Submit(policyId, right=WITHDRAWAL, receivedAt, channelRef)</c>.</summary>
    Task<PolicySubmitOutcome> SubmitAsync(PolicyId policyId, Instant receivedAt, string channelRef, CancellationToken cancellationToken);
}

/// <summary>Adapter over the POL in-process contracts.</summary>
internal sealed class PolicyContractWithdrawalPort(IServiceProvider services) : IWithdrawalPolicyPort
{
    public async Task<WithdrawalPolicyFacts?> FindAsync(PolicyId policyId, CancellationToken cancellationToken)
    {
        var policies = services.GetService<IPolicyPolicyService>();
        if (policies is null)
        {
            return null;
        }

        try
        {
            var response = await policies.GetAsync(policyId.Value.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false);
            // TODO(hardening): use the bind/issue date of the contract when POL exposes it; RecordedAt is when POL recorded the policy.
            return new WithdrawalPolicyFacts(response.Policy.RecordedAt, response.Policy.PolicyholderPartyId, response.Policy.PolicyNumber.ToString());
        }
        catch (DomainException)
        {
            return null;
        }
    }

    public async Task<PolicySubmitOutcome> SubmitAsync(PolicyId policyId, Instant receivedAt, string channelRef, CancellationToken cancellationToken)
    {
        var withdrawals = services.GetService<IPolicyWithdrawalService>();
        if (withdrawals is null)
        {
            return PolicySubmitOutcome.NotBound;
        }

        var request = new WithdrawalSubmitRequest
        {
            PolicyId = policyId,
            Right = WithdrawalRight.Withdrawal,
            ReceivedAt = receivedAt,
            ChannelRef = channelRef,
        };
        try
        {
            // The channel reference derives the idempotency key: a retry of the same request is one POL call (R-89).
            var key = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("chn-withdrawal:" + channelRef))[..16]);
            await withdrawals.SubmitAsync(request, new CommandOptions(IdempotencyKey.From(key)), cancellationToken).ConfigureAwait(false);
            return PolicySubmitOutcome.Accepted;
        }
        catch (DomainException ex) when (ex.Error.Code.ToString().EndsWith("RIGHT-EXPIRED", StringComparison.Ordinal))
        {
            return PolicySubmitOutcome.Expired;
        }
    }
}
