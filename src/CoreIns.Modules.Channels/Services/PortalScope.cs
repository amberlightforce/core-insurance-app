using CoreIns.Modules.Channels.Domain;
using CoreIns.Modules.Channels.Persistence;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Channels.Services;

/// <summary>What a signed-in customer may see: the linked party, the accounts it holds or belongs to, and every party of those accounts.</summary>
/// <param name="Subject">The sign-in identity (actor id).</param>
/// <param name="PartyId">The one party the identity is linked to.</param>
/// <param name="AccountIds">Pending or active accounts of the party (held or current member).</param>
/// <param name="PartyIds">The linked party plus the holder and every current member of those accounts: the policyholders whose policies the customer sees.</param>
internal sealed record PortalCaller(string Subject, PartyId PartyId, IReadOnlyList<AccountId> AccountIds, IReadOnlySet<PartyId> PartyIds);

/// <summary>
/// The CHN scope service (BO-CHN-01): resolves "my parties", "my accounts" and "my policies" for a portal customer and fails closed.
/// The permission matrix (<c>PermissionMatrixService.EvaluateAsync</c>) decides whether an actor may do a transaction but never checks
/// ownership, so every portal read or command must go through this service. Nothing is copied into CHN (REQ-CHN-305): accounts come from
/// PTY and policies from POL through their in-process contracts.
/// </summary>
internal sealed class PortalScope(ChannelsDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IServiceProvider services)
{
    private const int MaxAccounts = 100;
    private const int MaxPolicies = 500;
    private PortalCaller? _caller;
    private bool _resolved;

    /// <summary>True for staff and platform roles: they are governed by permissions, not by ownership.</summary>
    public static bool IsStaff(RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Roles.Any(r => r.StartsWith("Staff.", StringComparison.Ordinal) || r.StartsWith("Platform.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Owner filter for readers that store the declaring actor: null for staff (no restriction), the actor for everyone else.
    /// Fails closed: anyone without a staff or platform role is restricted. Generalises the withdrawal controller's filter.
    /// </summary>
    public static string? OwnerFilter(RequestContext context) => IsStaff(context) ? null : context.Actor.ToString();

    /// <summary>
    /// The caller's scope, or null when the caller is not a portal customer with a live link (no portal role, staff, an unlinked identity).
    /// Portal endpoints answer null with 404.
    /// </summary>
    public async Task<PortalCaller?> ResolveAsync(CancellationToken cancellationToken)
    {
        if (_resolved)
        {
            return _caller;
        }

        _caller = await LoadAsync(cancellationToken).ConfigureAwait(false);
        _resolved = true;
        return _caller;
    }

    /// <summary>True when the policy exists and its policyholder is one of "my parties". An unknown policy is also false (never reveals existence).</summary>
    public async Task<bool> OwnsPolicyAsync(PolicyId policyId, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(cancellationToken).ConfigureAwait(false) is not { } caller || services.GetService<IPolicyPolicyService>() is not { } policies)
        {
            return false;
        }

        try
        {
            var response = await policies.GetAsync(policyId.Value.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false);
            return caller.PartyIds.Contains(response.Policy.PolicyholderPartyId);
        }
        catch (DomainException)
        {
            return false;
        }
    }

    /// <summary>"My policies": the policies of every party in scope (POL search by insured party), newest policy number first, capped.</summary>
    public async Task<IReadOnlyList<PolicySearchItem>> MyPoliciesAsync(CancellationToken cancellationToken)
    {
        if (await ResolveAsync(cancellationToken).ConfigureAwait(false) is not { } caller || services.GetService<IPolicyPolicyService>() is not { } policies)
        {
            return [];
        }

        var found = new Dictionary<PolicyId, PolicySearchItem>();
        foreach (var party in caller.PartyIds)
        {
            string? cursor = null;
            do
            {
                var page = await policies.SearchByCriteriaAsync(new PolicySearchCriteria { InsuredPartyId = party.Value }, null, cursor, 100, cancellationToken).ConfigureAwait(false);
                foreach (var item in page.Items)
                {
                    // Defence in depth: never trust the search filter alone.
                    if (caller.PartyIds.Contains(new PartyId(item.InsuredPartyId)))
                    {
                        found[item.PolicyId] = item;
                    }
                }

                cursor = page.NextCursor;
            }
            while (cursor is not null && found.Count < MaxPolicies);
        }

        return [.. found.Values.OrderByDescending(p => p.PolicyNumber.Value, StringComparer.Ordinal).Take(MaxPolicies)];
    }

    private async Task<PortalCaller?> LoadAsync(CancellationToken cancellationToken)
    {
        if (IsStaff(context) || !context.Roles.Contains("Customer.Portal") || context.Actor.Kind != ActorKind.User || context.LegalEntity is null)
        {
            return null;
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity.Value);
        var subject = context.Actor.Id;
        var link = await db.PortalIdentities.AsNoTracking()
            .SingleOrDefaultAsync(r => r.LegalEntityId == legalEntity && r.Subject == subject && r.Status == PortalIdentityStatuses.Linked, cancellationToken)
            .ConfigureAwait(false);
        if (link is null)
        {
            return null;
        }

        var parties = new HashSet<PartyId> { link.PartyId };
        var accountIds = new List<AccountId>();
        if (services.GetService<IPartyAccountService>() is { } accounts)
        {
            try
            {
                string? cursor = null;
                do
                {
                    var page = await accounts.SearchAsync(cursor, 100, link.PartyId, null, null, cancellationToken).ConfigureAwait(false);
                    foreach (var item in page.Items.Where(i => i.Status != AccountSearchItem.StatusValue.Withdrawn))
                    {
                        accountIds.Add(item.AccountId);
                        parties.Add(item.HolderPartyId);
                        var detail = await accounts.GetAsync(item.AccountId.Value.ToString("D"), cancellationToken).ConfigureAwait(false);
                        foreach (var member in detail.Account.Members)
                        {
                            parties.Add(member.PartyId);
                        }
                    }

                    cursor = page.NextCursor;
                }
                while (cursor is not null && accountIds.Count < MaxAccounts);
            }
            catch (DomainException)
            {
                // Fail closed to the linked party alone when accounts cannot be read.
                // TODO(hardening): surface this as a 503 instead of a narrower scope.
                accountIds.Clear();
                parties = [link.PartyId];
            }
        }

        return new PortalCaller(subject, link.PartyId, accountIds, parties);
    }
}
