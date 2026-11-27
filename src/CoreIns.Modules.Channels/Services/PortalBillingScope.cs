using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Channels.Services;

/// <summary>One invoice of the customer's policies with the policy number for display.</summary>
internal sealed record PortalInvoiceRow(InvoiceView Invoice, PolicyNumber PolicyNumber);

/// <summary>The customer's billing data: the accounts they pay (balances) and the invoices of the policies they own.</summary>
internal sealed record PortalBillingData(IReadOnlyList<BillingAccountGetResponse> PayerAccounts, IReadOnlyList<PortalInvoiceRow> Invoices);

/// <summary>
/// BIL data of the signed-in customer, read once per request (BO-CHN-04). Billing accounts are discovered from the customer's own policies
/// (one cheap invoice lookup per policy), then every account is listed once with <c>billingAccountId</c> and the result is cut down to invoices
/// of policies in scope: another payer's invoice on a shared account never shows. Balances are shown only for accounts the customer pays.
/// </summary>
internal sealed class PortalBillingScope(PortalScope scope, IServiceProvider services)
{
    private const int MaxDiscoveryPolicies = 50;
    private const int MaxInvoices = 300;
    private PortalBillingData? _data;

    /// <summary>The customer's billing data, or null when the caller is not a portal customer with a live link.</summary>
    public async Task<PortalBillingData?> LoadAsync(CancellationToken cancellationToken)
    {
        if (_data is not null)
        {
            return _data;
        }

        if (await scope.ResolveAsync(cancellationToken).ConfigureAwait(false) is not { } caller)
        {
            return null;
        }

        var policies = await scope.MyPoliciesAsync(cancellationToken).ConfigureAwait(false);
        if (services.GetService<IBillingInvoiceService>() is not { } invoices || policies.Count == 0)
        {
            return _data = new PortalBillingData([], []);
        }

        var numbers = policies.ToDictionary(p => p.PolicyId, p => p.PolicyNumber);
        var accountIds = new HashSet<BillingAccountId>();
        // TODO(hardening): replace the per-policy discovery with a BIL read of billing accounts by payer party.
        foreach (var policy in policies.Take(MaxDiscoveryPolicies))
        {
            try
            {
                var first = await invoices.ListAsync(limit: 1, policyId: policy.PolicyId, cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (var item in first.Items.Where(i => i.Invoice.PolicyId == policy.PolicyId))
                {
                    accountIds.Add(item.Invoice.BillingAccountId);
                }
            }
            catch (DomainException)
            {
                // One policy's billing being unreadable must not blank the page.
            }
        }

        var rows = new List<PortalInvoiceRow>();
        var payerAccounts = new List<BillingAccountGetResponse>();
        var accountService = services.GetService<IBillingBillingAccountService>();
        foreach (var accountId in accountIds)
        {
            try
            {
                string? cursor = null;
                do
                {
                    var page = await invoices.ListAsync(cursor: cursor, limit: 100, billingAccountId: accountId, cancellationToken: cancellationToken).ConfigureAwait(false);
                    rows.AddRange(page.Items
                        .Where(i => numbers.ContainsKey(i.Invoice.PolicyId))
                        .Select(i => new PortalInvoiceRow(i.Invoice, numbers[i.Invoice.PolicyId])));
                    cursor = page.NextCursor;
                }
                while (cursor is not null && rows.Count < MaxInvoices);

                if (accountService is not null)
                {
                    var account = await accountService.GetAsync(accountId.Value.ToString("D"), cancellationToken).ConfigureAwait(false);
                    if (caller.PartyIds.Contains(account.Account.PayerPartyId))
                    {
                        payerAccounts.Add(account);
                    }
                }
            }
            catch (DomainException)
            {
                // Skip an unreadable account.
            }
        }

        return _data = new PortalBillingData(payerAccounts, [.. rows.DistinctBy(r => r.Invoice.InvoiceId)]);
    }

    /// <summary>True for an invoice that still has an amount to pay.</summary>
    public static bool IsUnpaid(InvoiceView invoice) =>
        invoice.Open.Amount > 0m
        && invoice.State is InvoiceView.StateValue.Billed or InvoiceView.StateValue.Due or InvoiceView.StateValue.PartiallyPaid or InvoiceView.StateValue.Overdue;
}
