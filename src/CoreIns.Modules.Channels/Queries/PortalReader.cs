using System.Globalization;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Channels.Contracts.Api;
using CoreIns.Modules.Channels.Services;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Channels.Queries;

/// <summary>
/// Portal read model (BO-CHN-02). Everything is read through <see cref="PortalScope"/> from POL and BIL in-process; nothing is copied into CHN
/// (REQ-CHN-305) and only customer-visible fields are mapped (no UW data, no internal notes, no rule or rating trace, no producer data).
/// Every method returns null (404) for a caller without a live portal link and for a policy the caller does not own.
/// </summary>
internal sealed class PortalReader(PortalScope scope, PortalBillingScope billing, PortalInbox inbox, IServiceProvider services, IClock clock)
{
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;
    private const int RenewalWindowDays = 60;

    public async Task<PortalMeResponse?> MeAsync(CancellationToken cancellationToken)
    {
        if (await scope.ResolveAsync(cancellationToken).ConfigureAwait(false) is not { } caller)
        {
            return null;
        }

        return new PortalMeResponse
        {
            Subject = caller.Subject,
            PartyId = caller.PartyId,
            AccountIds = [.. caller.AccountIds],
            HouseholdSize = caller.PartyIds.Count,
        };
    }

    public async Task<PortalPolicyListResponse?> PoliciesAsync(string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (await scope.ResolveAsync(cancellationToken).ConfigureAwait(false) is not { } caller)
        {
            return null;
        }

        var size = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var offset = int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : 0;
        var all = await scope.MyPoliciesAsync(cancellationToken).ConfigureAwait(false);
        var page = all.Skip(offset).Take(size).Select(p => ToListItem(p, caller.PartyId)).ToList();
        var next = offset + size < all.Count ? (offset + size).ToString(CultureInfo.InvariantCulture) : null;
        return new PortalPolicyListResponse { Items = page, NextCursor = next, Limit = size };
    }

    public async Task<PortalPolicyDetailResponse?> PolicyAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var guid)
            || await scope.ResolveAsync(cancellationToken).ConfigureAwait(false) is not { } caller
            || !await scope.OwnsPolicyAsync(new PolicyId(guid), cancellationToken).ConfigureAwait(false)
            || services.GetService<IPolicyPolicyService>() is not { } policies)
        {
            return null;
        }

        PolicyGetResponse response;
        try
        {
            response = await policies.GetAsync(guid.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException)
        {
            return null;
        }

        var coverages = response.RiskTree?.Coverages ?? [];
        var vehicles = response.RiskTree?.Vehicles ?? [];
        return new PortalPolicyDetailResponse
        {
            PolicyId = response.Policy.PolicyId,
            PolicyNumber = response.Policy.PolicyNumber,
            ProductCode = response.Policy.ProductCode,
            Status = response.Policy.Status?.ToString(),
            Ownership = response.Policy.PolicyholderPartyId == caller.PartyId ? PortalPolicyDetailResponse.OwnershipValue.Own : PortalPolicyDetailResponse.OwnershipValue.Household,
            TermNumber = response.Term?.TermNumber,
            TermStart = response.Term?.Period.Start,
            TermEnd = response.Term?.Period.End,
            Currency = response.Term?.Currency,
            PaymentPlan = response.Term?.PaymentPlanRef,
            Coverages = [.. coverages.Select(c => new PortalCoverage { CoverageCode = c.CoverageCode, Selected = c.Selected })],
            // Plate, make, model and year only: no VIN, no value, no free-form risk fields.
            Vehicles = [.. vehicles.Select(v => new PortalVehicle { Plate = v.Plate, Make = v.Make, Model = v.Model, FirstRegistrationYear = v.FirstRegistrationYear })],
            // Totals per category only: no rates, rule ids or treatment details.
            RenewalJobId = await OfferedRenewalJobAsync(guid, cancellationToken).ConfigureAwait(false),
            Premium = [.. response.Charges.GroupBy(c => (c.ChargeCategory, c.Amount.Currency))
                .Select(g => new PortalPremiumLine { Category = g.Key.ChargeCategory, Amount = new Money(g.Sum(c => c.Amount.Amount), g.Key.Currency) })],
        };
    }

    public async Task<PortalDashboardResponse?> DashboardAsync(CancellationToken cancellationToken)
    {
        if (await scope.ResolveAsync(cancellationToken).ConfigureAwait(false) is not { } caller)
        {
            return null;
        }

        var policies = await scope.MyPoliciesAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        var today = now.UtcDate;
        var due = policies
            .Where(p => p.Status == TermStateCode.InForce && p.TermPeriod is { End: { } end } && end > now && end.UtcDate.DaysSince(today) <= RenewalWindowDays)
            .OrderBy(p => p.TermPeriod!.Value.End!.Value)
            .ToList();
        var renewals = new List<PortalRenewalDue>();
        foreach (var p in due)
        {
            var end = p.TermPeriod!.Value.End!.Value;
            renewals.Add(new PortalRenewalDue
            {
                PolicyId = p.PolicyId, PolicyNumber = p.PolicyNumber, ProductCode = p.ProductCode, ExpiresAt = end,
                DaysLeft = end.UtcDate.DaysSince(today),
                RenewalJobId = await OfferedRenewalJobAsync(p.PolicyId.Value, cancellationToken).ConfigureAwait(false),
            });
        }

        return new PortalDashboardResponse
        {
            PolicyCount = policies.Count,
            ActivePolicyCount = policies.Count(p => p.Status == TermStateCode.InForce),
            RenewalsDue = renewals,
            UnpaidInvoices = await UnpaidInvoicesAsync(today, cancellationToken).ConfigureAwait(false),
            UnreadNotificationCount = await inbox.UnreadCountAsync(caller.PartyId, cancellationToken).ConfigureAwait(false),
            OpenRequests = await inbox.OpenRequestsAsync(caller.PartyId, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// The policy's offered renewal (a QUOTED job of type RENEWAL; the only state <c>portal/renewals/accept</c> and <c>decline</c> can act on), or null.
    /// The caller has already been shown to own the policy. Read failures degrade to "no offer yet", never to an error on the dashboard.
    /// </summary>
    private async Task<Guid?> OfferedRenewalJobAsync(Guid policyId, CancellationToken cancellationToken)
    {
        if (services.GetService<IPolicyJobService>() is not { } jobs)
        {
            return null;
        }

        try
        {
            var page = await jobs.ListAsync(policy: policyId.ToString("D"), state: "QUOTED", cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                if (item.Jobs is { ValueKind: System.Text.Json.JsonValueKind.Object } job
                    && job.TryGetProperty("jobType", out var type) && type.GetString() == "RENEWAL"
                    && job.TryGetProperty("jobId", out var id) && id.TryGetGuid(out var jobId))
                {
                    return jobId;
                }
            }
        }
        catch (DomainException)
        {
            // TODO(hardening): log; a POL read failure must not break the portal dashboard.
        }

        return null;
    }

    private async Task<IReadOnlyList<PortalUnpaidInvoice>> UnpaidInvoicesAsync(BusinessDate today, CancellationToken cancellationToken)
    {
        // One BIL read per billing account (BO-CHN-04), already cut down to invoices of the own policies of the caller.
        var data = await billing.LoadAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. (data?.Invoices ?? []).Select(r => r.Invoice).Where(PortalBillingScope.IsUnpaid).OrderBy(i => i.DueDate)
                .Select(i => new PortalUnpaidInvoice
                {
                    InvoiceId = i.InvoiceId, InvoiceNumber = i.InvoiceNumber, PolicyId = i.PolicyId, DueDate = i.DueDate, Open = i.Open,
                    Overdue = i.State == InvoiceView.StateValue.Overdue || i.DueDate < today,
                }),
        ];
    }

    private static PortalPolicyListItem ToListItem(PolicySearchItem p, PartyId caller) => new()
    {
        PolicyId = p.PolicyId,
        PolicyNumber = p.PolicyNumber,
        ProductCode = p.ProductCode,
        Status = p.Status?.ToString(),
        TermStart = p.TermPeriod?.Start,
        TermEnd = p.TermPeriod?.End,
        Ownership = new PartyId(p.InsuredPartyId) == caller ? PortalPolicyListItem.OwnershipValue.Own : PortalPolicyListItem.OwnershipValue.Household,
    };
}
