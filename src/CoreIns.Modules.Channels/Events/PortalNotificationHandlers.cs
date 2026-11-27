using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Channels.Commands;
using CoreIns.Modules.Channels.Domain;
using CoreIns.Modules.Channels.Persistence;
using CoreIns.Modules.Claims.Contracts;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Channels.Events;

/// <summary>
/// Shared part of the owner-event consumers that raise portal notifications (BO-CHN-04): find the party to tell through the owner's in-process
/// contract and queue one notification keyed by the event id, so a redelivery is a no-op. An event whose party cannot be resolved is
/// skipped rather than retried forever (the facts stay in the owner module).
/// </summary>
internal sealed class PortalNotifier(ChannelsDbContext db, ILegalEntityDirectory legalEntities, IClock clock, IServiceProvider services)
{
    public async Task NotifyAsync(EventEnvelope envelope, string kind, Func<CancellationToken, Task<(PartyId Party, string? SubjectType, string? SubjectId)?>> resolve, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(resolve);
        (PartyId Party, string? SubjectType, string? SubjectId)? target;
        try
        {
            target = await resolve(cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException)
        {
            return;
        }

        if (target is not { } found)
        {
            return;
        }

        await PortalEngagementSupport.AddNotificationAsync(
            db, legalEntities.Resolve(envelope.LegalEntity), found.Party, kind, found.SubjectType, found.SubjectId, envelope.EventId.Value.ToString("D"), clock.Now, cancellationToken).ConfigureAwait(false);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The unique source index: another delivery of the same event won the race.
            db.ChangeTracker.Clear();
        }
    }

    public T? Service<T>() where T : class => services.GetService<T>();

    public static string? Key(EventEnvelope envelope, string key) =>
        envelope.BusinessKeys.TryGetValue(key, out var value) ? value : envelope.AggregateId;
}

/// <summary><c>pol.RenewalOffered</c> → "your renewal offer is ready" for the policyholder.</summary>
internal sealed class RenewalOfferedNotificationHandler(PortalNotifier notifier) : IEventHandler<RenewalOfferedV1>
{
    public const string Name = "CHN.RenewalOffered.Notify";

    public Task HandleAsync(EventEnvelope envelope, RenewalOfferedV1 payload, CancellationToken cancellationToken) =>
        notifier.NotifyAsync(envelope, NotificationKinds.RenewalOffered, async ct =>
        {
            var policyKey = PortalNotifier.Key(envelope, "policyId");
            if (policyKey is null || notifier.Service<IPolicyPolicyService>() is not { } policies)
            {
                return null;
            }

            var policy = (await policies.GetAsync(policyKey, cancellationToken: ct).ConfigureAwait(false)).Policy;
            return (policy.PolicyholderPartyId, "POLICY", policy.PolicyId.Value.ToString("D"));
        }, cancellationToken);
}

/// <summary><c>bil.InvoiceIssued</c> → "a new invoice is ready" for the payer of the billing account.</summary>
internal sealed class InvoiceIssuedNotificationHandler(PortalNotifier notifier) : IEventHandler<InvoiceIssuedV1>
{
    public const string Name = "CHN.InvoiceIssued.Notify";

    public Task HandleAsync(EventEnvelope envelope, InvoiceIssuedV1 payload, CancellationToken cancellationToken) =>
        notifier.NotifyAsync(envelope, NotificationKinds.InvoiceIssued, async ct =>
        {
            if (!envelope.BusinessKeys.TryGetValue("billingAccountId", out var account) || notifier.Service<IBillingBillingAccountService>() is not { } accounts)
            {
                return null;
            }

            var payer = (await accounts.GetAsync(account, ct).ConfigureAwait(false)).Account.PayerPartyId;
            return (payer, "INVOICE", payload.InvoiceId.Value.ToString("D"));
        }, cancellationToken);
}

/// <summary><c>clm.ClaimUpdated</c> with a new milestone or sub-status → "your claim moved" for the insured party.</summary>
internal sealed class ClaimUpdatedNotificationHandler(PortalNotifier notifier) : IEventHandler<ClaimUpdatedV1>
{
    public const string Name = "CHN.ClaimUpdated.Notify";

    public Task HandleAsync(EventEnvelope envelope, ClaimUpdatedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.NewMilestone is null && payload.SubStatus is null)
        {
            return Task.CompletedTask;
        }

        return notifier.NotifyAsync(envelope, NotificationKinds.ClaimUpdated, async ct =>
        {
            var claimKey = PortalNotifier.Key(envelope, "claimId");
            if (claimKey is null || notifier.Service<IClaimsClaimService>() is not { } claims)
            {
                return null;
            }

            var summary = (await claims.GetAsync(claimKey, ct).ConfigureAwait(false)).Claim.Summary;
            return (summary.InsuredPartyId, "CLAIM", summary.ClaimId.Value.ToString("D"));
        }, cancellationToken);
    }
}
