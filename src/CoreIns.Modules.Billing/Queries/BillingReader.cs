using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Platform.Contracts.Common;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Queries;

/// <summary>
/// The read side of the slice's operations (<c>bil.BillingAccount.get</c>, <c>bil.Invoice.get/list</c>,
/// <c>bil.Receipt.get</c>). Every query filters by the caller's legal entity: another entity's row is "not found".
/// Balances are derived from the sub-ledger lines (REQ-BIL-001, REQ-BIL-285); paid and open amounts from allocations
/// (REQ-BIL-087). No personal data is returned (the payer is a party id).
/// </summary>
internal sealed class BillingReader(BillingDbContext db)
{
    public async Task<BillingAccountGetResponse?> AccountAsync(LegalEntityId legalEntity, BillingAccountId id, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.BillingAccountId == id && a.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return null;
        }

        var currency = Currency.FromCode(account.Currency);
        var terms = await db.PlanInstances.AsNoTracking().Where(p => p.BillingAccountId == id).OrderBy(p => p.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var net = await db.LedgerLines.AsNoTracking().Where(l => l.BillingAccountId == id && l.Currency == account.Currency)
            .GroupBy(l => new { l.AccountCode, l.Side })
            .Select(g => new { g.Key.AccountCode, g.Key.Side, Sum = g.Sum(l => l.Amount) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        decimal Sum(string code, LedgerSide side) => net.Where(n => n.AccountCode == code && n.Side == Codes.Of(side)).Sum(n => n.Sum);
        decimal Debit(string code) => Sum(code, LedgerSide.Debit) - Sum(code, LedgerSide.Credit);

        var overdueInvoices = await db.Invoices.AsNoTracking()
            .Where(i => i.BillingAccountId == id && i.State == Codes.Of(InvoiceState.Overdue)).Select(i => i.InvoiceId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var overdue = overdueInvoices.Count == 0 ? 0m : (await OpenByInvoiceAsync(overdueInvoices, cancellationToken).ConfigureAwait(false)).Values.Sum();

        return new BillingAccountGetResponse
        {
            Account = new BillingAccountView
            {
                BillingAccountId = account.BillingAccountId,
                AccountNumber = AccountNumber.Parse(account.AccountNumber),
                PayerPartyId = account.PayerPartyId,
                Currency = currency,
                Status = Codes.Api<BillingAccountView.StatusValue>(Codes.Parse<BillingAccountStatus>(account.Status)),
                CreatedAt = account.CreatedAt,
                Terms = [.. terms.Select(t => new BillingTermView
                {
                    PolicyTermId = t.TermId,
                    PolicyId = t.PolicyId,
                    PolicyNumber = PolicyNumber.Parse(t.PolicyNumber),
                    TermNumber = t.TermNumber,
                    ProductCode = t.ProductCode,
                    PlanCode = t.PlanCode,
                    BillMode = t.BillMode == PaymentPlans.DirectBill ? BillingTermView.BillModeValue.DirectBill : BillingTermView.BillModeValue.AgencyBill,
                    TermPeriod = DateRange.Of(t.TermFrom, t.TermTo),
                })],
            },
            BalancesByState = new BillingAccountBalances
            {
                WrittenUnbilled = new Money(Debit(LedgerAccounts.WrittenUnbilled), currency),
                Billed = new Money(Math.Max(Debit(LedgerAccounts.BilledReceivable), 0m), currency),
                Overdue = new Money(overdue, currency),
                Collected = new Money(Sum(LedgerAccounts.CashAtBank, LedgerSide.Debit), currency),
                Unapplied = new Money(-Debit(LedgerAccounts.Suspense), currency),

                // Customer credit: credit notes not yet offset against an invoice (a credit balance of the billed receivable
                // LA-02) plus what has been moved to refunds payable LA-12 (PRD-06 §4.13 steps 17 and 18).
                Credit = new Money(Math.Max(-Debit(LedgerAccounts.BilledReceivable), 0m) - Debit(LedgerAccounts.RefundsPayable), currency),
            },
        };
    }

    public async Task<InvoiceGetResponse?> InvoiceAsync(LegalEntityId legalEntity, InvoiceId id, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking()
            .SingleOrDefaultAsync(i => i.InvoiceId == id && i.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (invoice is null)
        {
            return null;
        }

        var items = await db.InvoiceItems.AsNoTracking().Where(i => i.InvoiceId == id).OrderBy(i => i.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        var allocations = await db.Allocations.AsNoTracking().Where(a => a.InvoiceId == id).OrderBy(a => a.RecordedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var currency = Currency.FromCode(invoice.Currency);
        var paidByItem = allocations.GroupBy(a => a.InvoiceItemId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));

        // Credit notes settle items too: an invoice item by the credit offset against it, a credit item by what has been applied.
        var itemIds = items.Select(i => i.InvoiceItemId).ToList();
        var applications = await db.CreditApplications.AsNoTracking()
            .Where(c => (c.TargetInvoiceItemId != null && itemIds.Contains(c.TargetInvoiceItemId.Value)) || itemIds.Contains(c.CreditItemId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var application in applications)
        {
            var key = invoice.Kind == "CREDIT_NOTE" ? application.CreditItemId : application.TargetInvoiceItemId!.Value;
            paidByItem[key] = paidByItem.GetValueOrDefault(key) + application.Amount;
        }

        return new InvoiceGetResponse
        {
            Invoice = View(invoice, items, paidByItem.Values.Sum()),
            InvoiceItems = [.. items.Select(i => new InvoiceItemView
            {
                InvoiceItemId = i.InvoiceItemId,
                ChargeId = i.ChargeId,
                TransactionId = i.TransactionId,
                ElementLocator = i.ElementLocator,
                CoverageCode = i.CoverageCode,
                ChargeType = i.ChargeType,
                ChargeCategory = i.ChargeCategory,
                FiscalCategoryKey = i.FiscalCategoryKey,
                LegalStatus = i.LegalStatus,
                Provisional = i.LegalStatus is null ? null : i.Provisional,
                ValidPeriod = new DateRange(i.ValidFrom, i.ValidTo),
                Amount = new Money(i.Amount, currency),
                Open = new Money(i.Amount - paidByItem.GetValueOrDefault(i.InvoiceItemId), currency),
                State = Codes.Api<InvoiceItemView.StateValue>(Codes.Parse<InvoiceItemState>(i.State)),
                TransactionKind = i.TransactionKind,
                CancellationSource = i.CancellationSource,
                TreatmentRuleId = i.TreatmentRuleId,
            })],
            Allocations = [.. allocations.Select(Allocation)],
            FiscalStatus = Fiscal(invoice),
            DeliveryStatus = InvoiceGetResponse.DeliveryStatusValue.NotRequested,
        };
    }

    public async Task<InvoiceListPage> InvoicesAsync(
        LegalEntityId legalEntity, BillingAccountId? account, PolicyId? policy, PolicyTermId? term, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.Invoices.AsNoTracking().Where(i => i.LegalEntityId == legalEntity);
        if (account is { } a)
        {
            query = query.Where(i => i.BillingAccountId == a);
        }

        if (policy is { } p)
        {
            query = query.Where(i => i.PolicyId == p);
        }

        if (term is { } t)
        {
            query = query.Where(i => i.TermId == t);
        }

        // Invoice ids are UUIDv7: ordering by id is creation order and a stable cursor. The slice pages in memory over the
        // filtered set (one account or policy holds few invoices); a keyset query replaces it with the invoice run (W5-BIL-02).
        var all = (await query.ToListAsync(cancellationToken).ConfigureAwait(false)).OrderBy(i => i.InvoiceId.Value).ToList();
        if (cursor is not null && Guid.TryParse(cursor, out var after))
        {
            all = [.. all.Where(i => i.InvoiceId.Value.CompareTo(after) > 0)];
        }

        var page = all.Take(size + 1).ToList();
        var shown = page.Take(size).ToList();
        var ids = shown.Select(i => i.InvoiceId).ToList();
        var items = await db.InvoiceItems.AsNoTracking().Where(i => ids.Contains(i.InvoiceId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var paid = await SettledByInvoiceAsync(ids, cancellationToken).ConfigureAwait(false);
        return new InvoiceListPage
        {
            Items = [.. shown.Select(i => new InvoiceListItem
            {
                Invoice = View(i, [.. items.Where(x => x.InvoiceId == i.InvoiceId)], paid.GetValueOrDefault(i.InvoiceId)),
                FiscalStatus = Fiscal(i),
            })],
            NextCursor = page.Count > size ? shown[^1].InvoiceId.Value.ToString("D") : null,
            Limit = size,
        };
    }

    public async Task<ReceiptGetResponse?> ReceiptAsync(LegalEntityId legalEntity, PaymentId id, CancellationToken cancellationToken)
    {
        var receipt = await db.Receipts.AsNoTracking()
            .SingleOrDefaultAsync(r => r.ReceiptId == id && r.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (receipt is null)
        {
            return null;
        }

        var allocations = await db.Allocations.AsNoTracking().Where(a => a.ReceiptId == id).OrderBy(a => a.RecordedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var receivablePaid = await db.ReceivableAllocations.Where(a => a.ReceiptId == id).SumAsync(a => a.Amount, cancellationToken).ConfigureAwait(false);
        return new ReceiptGetResponse { Receipt = Receipt(receipt, allocations.Sum(a => a.Amount) + receivablePaid), Allocations = [.. allocations.Select(Allocation)], Reversals = [] };
    }

    public static ReceiptView Receipt(ReceiptRow r, decimal allocated)
    {
        var currency = Currency.FromCode(r.Currency);
        return new ReceiptView
        {
            ReceiptId = r.ReceiptId,
            ReceiptNumber = ReceiptNumber.Parse(r.ReceiptNumber),
            BillingAccountId = r.BillingAccountId,
            State = Codes.Api<ReceiptView.StateValue>(Codes.Parse<PaymentState>(r.State)),
            Channel = r.Channel,
            Method = r.Method,
            Amount = new Money(r.Amount, currency),
            Allocated = new Money(allocated, currency),
            Unallocated = new Money(r.Amount - allocated, currency),
            ValueDate = r.ValueDate,
            AccountingDate = r.AccountingDate,
            InvoiceId = r.InvoiceRef,
            SuspenseReason = r.SuspenseReason,
            RecordedAt = r.RecordedAt,
        };
    }

    public static AllocationView Allocation(AllocationRow a) => new()
    {
        AllocationId = a.AllocationId,
        ReceiptId = a.ReceiptId,
        InvoiceId = a.InvoiceId,
        InvoiceItemId = a.InvoiceItemId,
        Amount = new Money(a.Amount, Currency.FromCode(a.Currency)),
        RuleId = a.RuleId,
        Source = a.Source == "MANUAL" ? AllocationView.SourceValue.Manual : AllocationView.SourceValue.Rule,
        RecordedAt = a.RecordedAt,
    };

    private async Task<Dictionary<InvoiceId, decimal>> OpenByInvoiceAsync(List<InvoiceId> ids, CancellationToken cancellationToken)
    {
        var totals = await db.Invoices.AsNoTracking().Where(i => ids.Contains(i.InvoiceId)).Select(i => new { i.InvoiceId, i.Total })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var paid = await SettledByInvoiceAsync(ids, cancellationToken).ConfigureAwait(false);
        return totals.ToDictionary(t => t.InvoiceId, t => t.Total - paid.GetValueOrDefault(t.InvoiceId));
    }

    /// <summary>
    /// Settled amount per invoice: cash allocations plus credit offset against an invoice, and for a credit note the credit
    /// applied from it (REQ-BIL-087, REQ-BIL-073). The rest is open (for a credit note: the account credit it still holds).
    /// </summary>
    private async Task<Dictionary<InvoiceId, decimal>> SettledByInvoiceAsync(List<InvoiceId> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<InvoiceId, decimal>();
        var cash = await db.Allocations.AsNoTracking().Where(a => ids.Contains(a.InvoiceId)).GroupBy(a => a.InvoiceId)
            .Select(g => new { g.Key, Sum = g.Sum(a => a.Amount) }).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var c in cash)
        {
            result[c.Key] = result.GetValueOrDefault(c.Key) + c.Sum;
        }

        var offset = await db.CreditApplications.AsNoTracking().Where(a => a.TargetInvoiceId != null && ids.Contains(a.TargetInvoiceId.Value))
            .GroupBy(a => a.TargetInvoiceId!.Value).Select(g => new { g.Key, Sum = g.Sum(a => a.Amount) }).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var o in offset)
        {
            result[o.Key] = result.GetValueOrDefault(o.Key) + o.Sum;
        }

        var applied = await db.CreditApplications.AsNoTracking().Where(a => ids.Contains(a.CreditNoteId)).GroupBy(a => a.CreditNoteId)
            .Select(g => new { g.Key, Sum = g.Sum(a => a.Amount) }).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var a in applied)
        {
            result[a.Key] = result.GetValueOrDefault(a.Key) + a.Sum;
        }

        return result;
    }

    private static InvoiceView View(InvoiceRow i, List<InvoiceItemRow> items, decimal paid)
    {
        var currency = Currency.FromCode(i.Currency);
        return new InvoiceView
        {
            InvoiceId = i.InvoiceId,
            InvoiceNumber = InvoiceNumber.Parse(i.InvoiceNumber),
            Kind = i.Kind == "CREDIT_NOTE" ? InvoiceView.KindValue.CreditNote : InvoiceView.KindValue.Invoice,
            State = Codes.Api<InvoiceView.StateValue>(Codes.Parse<InvoiceState>(i.State)),
            BillingAccountId = i.BillingAccountId,
            PolicyId = i.PolicyId,
            PolicyTermId = i.TermId,
            TransactionId = i.TransactionId,
            OriginalInvoiceId = i.OriginalInvoiceId?.Value,
            IssueDate = i.IssueDate,
            DueDate = i.DueDate,
            Method = i.Method,
            Total = new Money(i.Total, currency),
            Paid = new Money(paid, currency),
            Open = new Money(i.Total - paid, currency),
            TotalsByCategory = [.. items.GroupBy(x => x.ChargeCategory).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new CategoryTotal { Category = g.Key, Amount = new Money(g.Sum(x => x.Amount), currency) })],
        };
    }

    private static InvoiceFiscalStatus Fiscal(InvoiceRow i) => new()
    {
        Status = Codes.Api<InvoiceFiscalStatus.StatusValue>(Codes.Parse<FiscalStatus>(i.FiscalStatus)),
        FiscalDocumentId = i.FiscalDocumentId,
        TriggerPoint = i.FiscalTriggerPoint switch
        {
            "TRANSACTION" => InvoiceFiscalStatus.TriggerPointValue.Transaction,
            "INVOICE" => InvoiceFiscalStatus.TriggerPointValue.Invoice,
            _ => null,
        },
        Series = i.FiscalSeries,
        Number = i.FiscalNumber,
        Mark = i.FiscalMark,
        Uid = i.FiscalUid,
        DocumentType = i.FiscalDocumentType,
        RejectionCodes = i.FiscalRejectionCodes.Length == 0 ? null : i.FiscalRejectionCodes,
    };
}

