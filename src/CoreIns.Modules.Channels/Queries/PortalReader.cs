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
internal sealed class PortalReader(PortalScope scope, IServiceProvider services, IClock clock)
{
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;
    private const int RenewalWindowDays = 60;
    private const int MaxInvoicePolicies = 50;

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
            Premium = [.. response.Charges.GroupBy(c => (c.ChargeCategory, c.Amount.Currency))
                .Select(g => new PortalPremiumLine { Category = g.Key.ChargeCategory, Amount = new Money(g.Sum(c => c.Amount.Amount), g.Key.Currency) })],
        };
    }

    public async Task<PortalDashboardResponse?> DashboardAsync(CancellationToken cancellationToken)
    {
        if (await scope.ResolveAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        var policies = await scope.MyPoliciesAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        var today = now.UtcDate;
        var renewals = policies
            .Where(p => p.Status == TermStateCode.InForce && p.TermPeriod is { End: { } end } && end > now && end.UtcDate.DaysSince(today) <= RenewalWindowDays)
            .Select(p =>
            {
                var end = p.TermPeriod!.Value.End!.Value;
                return new PortalRenewalDue
                {
                    PolicyId = p.PolicyId, PolicyNumber = p.PolicyNumber, ProductCode = p.ProductCode, ExpiresAt = end,
                    DaysLeft = end.UtcDate.DaysSince(today),
                };
            })
            .OrderBy(r => r.ExpiresAt)
            .ToList();

        // TODO(hardening): open requests (WRK) once messages/requests are scoped to the customer (BO-CHN-04).
        return new PortalDashboardResponse
        {
            PolicyCount = policies.Count,
            ActivePolicyCount = policies.Count(p => p.Status == TermStateCode.InForce),
            RenewalsDue = renewals,
            UnpaidInvoices = await UnpaidInvoicesAsync(policies, now, cancellationToken).ConfigureAwait(false),
        };
    }

    private async Task<IReadOnlyList<PortalUnpaidInvoice>> UnpaidInvoicesAsync(IReadOnlyList<PolicySearchItem> policies, Instant now, CancellationToken cancellationToken)
    {
        if (services.GetService<IBillingInvoiceService>() is not { } invoices)
        {
            return [];
        }

        var today = now.UtcDate;
        var result = new List<PortalUnpaidInvoice>();
        // TODO(hardening): list invoices by billing account instead of per policy once BO-CHN-04 adds the BIL read for portal billing.
        foreach (var policy in policies.Take(MaxInvoicePolicies))
        {
            try
            {
                var page = await invoices.ListAsync(null, 50, null, policy.PolicyId, null, cancellationToken).ConfigureAwait(false);
                foreach (var item in page.Items.Select(i => i.Invoice).Where(i => i.PolicyId == policy.PolicyId && i.Open.Amount > 0m && IsUnpaid(i.State)))
                {
                    result.Add(new PortalUnpaidInvoice
                    {
                        InvoiceId = item.InvoiceId, InvoiceNumber = item.InvoiceNumber, PolicyId = item.PolicyId, DueDate = item.DueDate, Open = item.Open,
                        Overdue = item.State == InvoiceView.StateValue.Overdue || item.DueDate < today,
                    });
                }
            }
            catch (DomainException)
            {
                // One policy's billing being unreadable must not blank the dashboard.
            }
        }

        return [.. result.OrderBy(i => i.DueDate)];
    }

    private static bool IsUnpaid(InvoiceView.StateValue state) =>
        state is InvoiceView.StateValue.Billed or InvoiceView.StateValue.Due or InvoiceView.StateValue.PartiallyPaid or InvoiceView.StateValue.Overdue;

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
