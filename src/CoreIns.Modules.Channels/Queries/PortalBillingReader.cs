using System.Globalization;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Channels.Commands;
using CoreIns.Modules.Channels.Contracts.Api;
using CoreIns.Modules.Channels.Domain;
using CoreIns.Modules.Channels.Services;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Channels.Queries;

/// <summary>
/// Portal billing views (BO-CHN-04): balances, invoices, refund status and the pay-now check. Everything is read through
/// <see cref="PortalBillingScope"/> (BIL in-process, scoped to the customer's own policies and payer accounts); nothing is copied into CHN.
/// Every method returns null (404) for a caller without a live portal link and for an invoice outside the caller's scope.
/// </summary>
internal sealed class PortalBillingReader(PortalBillingScope billing, PermissionMatrixService matrix, IServiceProvider services, IClock clock)
{
    private const string PortalChannel = "WEB_DIRECT";
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;

    public async Task<PortalBillingSummary?> SummaryAsync(CancellationToken cancellationToken)
    {
        if (await billing.LoadAsync(cancellationToken).ConfigureAwait(false) is not { } data)
        {
            return null;
        }

        var today = clock.Now.UtcDate;
        var unpaid = data.Invoices.Where(r => PortalBillingScope.IsUnpaid(r.Invoice)).Select(r => r.Invoice).ToList();
        return new PortalBillingSummary
        {
            Accounts =
            [
                .. data.PayerAccounts.Select(a => new PortalBillingAccountBalance
                {
                    BillingAccountId = a.Account.BillingAccountId,
                    AccountNumber = a.Account.AccountNumber,
                    Currency = a.Account.Currency.ToString(),
                    Billed = a.BalancesByState.Billed,
                    Overdue = a.BalancesByState.Overdue,
                    Unapplied = a.BalancesByState.Unapplied,
                    Credit = a.BalancesByState.Credit,
                }),
            ],
            OpenInvoiceCount = unpaid.Count,
            OverdueInvoiceCount = unpaid.Count(i => IsOverdue(i, today)),
        };
    }

    public async Task<PortalInvoiceListPage?> InvoicesAsync(string? state, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (await billing.LoadAsync(cancellationToken).ConfigureAwait(false) is not { } data)
        {
            return null;
        }

        var size = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var offset = int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : 0;
        var today = clock.Now.UtcDate;
        var rows = string.Equals(state, "all", StringComparison.OrdinalIgnoreCase) ? data.Invoices : [.. data.Invoices.Where(r => PortalBillingScope.IsUnpaid(r.Invoice))];
        var ordered = rows.OrderBy(r => r.Invoice.DueDate).ThenBy(r => r.Invoice.InvoiceNumber.Value, StringComparer.Ordinal).ToList();
        var page = ordered.Skip(offset).Take(size).Select(r => ToInvoice(r, today)).ToList();
        var next = offset + size < ordered.Count ? (offset + size).ToString(CultureInfo.InvariantCulture) : null;
        return new PortalInvoiceListPage { Items = page, NextCursor = next, Limit = size };
    }

    public async Task<PortalRefundListResponse?> RefundsAsync(CancellationToken cancellationToken)
    {
        if (await billing.LoadAsync(cancellationToken).ConfigureAwait(false) is not { } data)
        {
            return null;
        }

        var items = new List<PortalRefund>();
        if (services.GetService<IBillingRefundService>() is { } refunds)
        {
            foreach (var account in data.PayerAccounts)
            {
                try
                {
                    var page = await refunds.ListAsync(limit: 100, billingAccountId: account.Account.BillingAccountId, cancellationToken: cancellationToken).ConfigureAwait(false);
                    // Amount and state only: no payee, IBAN, approval comments or reason codes.
                    items.AddRange(page.Items.Select(i => new PortalRefund
                    {
                        RefundId = i.Refund.RefundId, State = i.Refund.State.ToString(), Amount = i.Refund.Amount, ProposedAt = i.Refund.ProposedAt, DecidedAt = i.Refund.DecidedAt,
                    }));
                }
                catch (DomainException)
                {
                    // An unreadable account's refunds are left out rather than failing the page.
                }
            }
        }

        return new PortalRefundListResponse { Items = [.. items.OrderByDescending(i => i.ProposedAt)] };
    }

    /// <summary>
    /// The pay-now check: the invoice must be one of the customer's and still open, and the transaction-permission matrix must allow
    /// <c>payment.pay</c> for this actor and amount. No money moves here; the answer carries the link of the hosted payment page.
    /// </summary>
    public async Task<PortalPayNowResponse?> PayNowAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var guid) || await billing.LoadAsync(cancellationToken).ConfigureAwait(false) is not { } data
            || data.Invoices.FirstOrDefault(r => r.Invoice.InvoiceId.Value == guid) is not { } row)
        {
            return null;
        }

        var invoice = row.Invoice;
        var open = PortalBillingScope.IsUnpaid(invoice);
        var answer = await matrix.EvaluateAsync(new PermissionCheck("payment.pay", PortalChannel, null, null, invoice.Open.Amount, null, false), cancellationToken).ConfigureAwait(false);
        var outcome = answer.IsSuccess ? answer.Value.Decision.Outcome : MatrixOutcomes.Deny;
        var reasons = answer.IsSuccess ? answer.Value.Decision.Reasons.ToList() : ["MATRIX_UNAVAILABLE"];
        if (!open)
        {
            reasons.Add("NOTHING_TO_PAY");
        }

        var payable = open && outcome == MatrixOutcomes.Allow;
        return new PortalPayNowResponse
        {
            InvoiceId = invoice.InvoiceId,
            Decision = outcome switch
            {
                MatrixOutcomes.Allow => PortalPayNowResponse.DecisionValue.Allow,
                MatrixOutcomes.Refer => PortalPayNowResponse.DecisionValue.Refer,
                _ => PortalPayNowResponse.DecisionValue.Deny,
            },
            Payable = payable,
            // TODO(hardening): sign the link / create a payment-provider session once a hosted payment page exists.
            PayUrl = payable ? $"/portal/billing/pay/{invoice.InvoiceId.Value:D}" : null,
            Reasons = reasons,
        };
    }

    private static bool IsOverdue(InvoiceView invoice, BusinessDate today) => invoice.State == InvoiceView.StateValue.Overdue || invoice.DueDate < today;

    private static PortalInvoice ToInvoice(PortalInvoiceRow row, BusinessDate today) => new()
    {
        InvoiceId = row.Invoice.InvoiceId,
        InvoiceNumber = row.Invoice.InvoiceNumber,
        PolicyId = row.Invoice.PolicyId,
        PolicyNumber = row.PolicyNumber,
        IssueDate = row.Invoice.IssueDate,
        DueDate = row.Invoice.DueDate,
        State = row.Invoice.State.ToString(),
        Total = row.Invoice.Total,
        Paid = row.Invoice.Paid,
        Open = row.Invoice.Open,
        Overdue = PortalBillingScope.IsUnpaid(row.Invoice) && IsOverdue(row.Invoice, today),
        Payable = PortalBillingScope.IsUnpaid(row.Invoice),
    };
}
