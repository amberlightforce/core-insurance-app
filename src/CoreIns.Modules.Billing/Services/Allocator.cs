using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreIns.Modules.Billing.Services;

/// <summary>
/// Allocates a receipt's unapplied cash to whole invoices (REQ-BIL-129, REQ-BIL-130): one allocation row per invoice
/// item in waterfall order (taxes and levies before fees before premium), items Settled, invoice Paid (derived from
/// allocations, REQ-BIL-087), receipt Allocated or PartiallyAllocated, one ALLOCATED entry (LA-11 → LA-02) and
/// <c>CashAllocated</c>, all in the caller's transaction. An amount that is not the invoice's exact open amount is
/// refused (BIL-ERR-AMOUNT-MISMATCH): partial payments are not allocated in the slice, they stay unapplied. The
/// database refuses any allocation above the receipt or the item (trigger, REQ-BIL-130).
/// </summary>
internal sealed class Allocator(BillingDbContext db, RequestContext context, LedgerWriter ledger, IEventPublisher events, IClock clock)
{
    /// <summary>SQLSTATE the allocation guard raises when Σ allocations would exceed the receipt or the item.</summary>
    public const string OverAllocationState = "BL001";

    /// <summary>Open amount of each item of the invoices (amount − Σ allocations).</summary>
    public async Task<Dictionary<Guid, decimal>> OpenByItemAsync(IReadOnlyCollection<InvoiceId> invoices, CancellationToken cancellationToken)
    {
        var items = await db.InvoiceItems.AsNoTracking().Where(i => invoices.Contains(i.InvoiceId))
            .Select(i => new { i.InvoiceItemId, i.Amount }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = items.Select(i => i.InvoiceItemId).ToList();
        var allocated = await db.Allocations.AsNoTracking().Where(a => ids.Contains(a.InvoiceItemId))
            .GroupBy(a => a.InvoiceItemId).Select(g => new { Item = g.Key, Sum = g.Sum(a => a.Amount) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var sums = allocated.ToDictionary(a => a.Item, a => a.Sum);
        return items.ToDictionary(i => i.InvoiceItemId, i => i.Amount - sums.GetValueOrDefault(i.InvoiceItemId));
    }

    /// <summary>Open amount of an invoice.</summary>
    public async Task<Money> OpenAsync(InvoiceRow invoice, CancellationToken cancellationToken)
    {
        var open = await OpenByItemAsync([invoice.InvoiceId], cancellationToken).ConfigureAwait(false);
        return new Money(open.Values.Sum(), Currency.FromCode(invoice.Currency));
    }

    /// <summary>Allocates <paramref name="lines"/> (invoice, amount) of <paramref name="receipt"/>; every amount must equal the invoice's open amount.</summary>
    public async Task<Result<IReadOnlyList<AllocationRow>>> AllocateAsync(
        ReceiptRow receipt, IReadOnlyList<(InvoiceRow Invoice, Money Amount)> lines, string ruleId, string source, CancellationToken cancellationToken)
    {
        var currency = Currency.FromCode(receipt.Currency);
        var receiptState = Codes.Parse<PaymentState>(receipt.State);
        var alreadyAllocated = await db.Allocations.Where(a => a.ReceiptId == receipt.ReceiptId).SumAsync(a => (decimal?)a.Amount, cancellationToken).ConfigureAwait(false) ?? 0m;
        var unallocated = new Money(receipt.Amount - alreadyAllocated, currency);
        var requested = Money.Sum(lines.Select(l => l.Amount), currency);
        if (requested > unallocated)
        {
            return DomainError.Of(ModuleCode.BIL, "OVER-ALLOCATION", $"Allocations of {requested} exceed the receipt's unallocated {unallocated} (REQ-BIL-130).");
        }

        var open = await OpenByItemAsync([.. lines.Select(l => l.Invoice.InvoiceId)], cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        var rows = new List<AllocationRow>();
        var legs = new List<PostingLeg>();
        var termsTouched = new HashSet<PolicyTermId>();
        foreach (var (invoice, amount) in lines)
        {
            if (amount.Currency != currency || invoice.Currency != currency.Code)
            {
                return DomainError.Of(ModuleCode.BIL, "CURRENCY", "The receipt, the invoice and the amount must share one currency.");
            }

            var state = Codes.Parse<InvoiceState>(invoice.State);
            var paid = InvoiceStateModel.Machine.Fire(state, InvoiceTrigger.AllocateInFull);
            if (paid.IsFailure)
            {
                return DomainError.Of(ModuleCode.BIL, "AMOUNT-MISMATCH", $"Invoice {invoice.InvoiceNumber} is {invoice.State} and has nothing open to allocate.");
            }

            var items = await db.InvoiceItems.Where(i => i.InvoiceId == invoice.InvoiceId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var invoiceOpen = items.Sum(i => open.GetValueOrDefault(i.InvoiceItemId));
            if (amount.Amount != invoiceOpen)
            {
                return DomainError.Of(ModuleCode.BIL, "AMOUNT-MISMATCH",
                    $"The amount {amount} is not the open amount {new Money(invoiceOpen, currency)} of invoice {invoice.InvoiceNumber}; partial and over-payments stay unapplied in the slice.");
            }

            var rule = await ledger.RuleAsync(ledger.Key(EntryTypes.Allocated, RuleQualifiers.Any, RuleQualifiers.Any), cancellationToken).ConfigureAwait(false);
            if (rule.IsFailure)
            {
                return rule.Error!;
            }

            foreach (var item in Waterfall.Order(items, i => i.ChargeCategory).ThenBy(i => i.LineNo))
            {
                var itemOpen = open.GetValueOrDefault(item.InvoiceItemId);
                if (itemOpen <= 0m)
                {
                    continue;
                }

                var row = new AllocationRow
                {
                    AllocationId = Guid.CreateVersion7(),
                    LegalEntityId = receipt.LegalEntityId,
                    ReceiptId = receipt.ReceiptId,
                    InvoiceId = invoice.InvoiceId,
                    InvoiceItemId = item.InvoiceItemId,
                    TermId = item.TermId,
                    Amount = itemOpen,
                    Currency = currency.Code,
                    RuleId = ruleId,
                    Source = source,
                    Actor = context.Actor.ToString(),
                    RecordedAt = now,
                };
                rows.Add(row);
                item.State = Codes.Of(InvoiceItemState.Settled);
                termsTouched.Add(item.TermId);
                legs.Add(new PostingLeg(rule.Value, new Money(itemOpen, currency), new LineDimensions
                {
                    BillingAccountId = receipt.BillingAccountId,
                    PolicyId = invoice.PolicyId,
                    PolicyTermId = item.TermId,
                    TransactionId = item.TransactionId,
                    ChargeId = item.ChargeId,
                    ChargeType = item.ChargeType,
                    ChargeCategory = item.ChargeCategory,
                    CoverageCode = item.CoverageCode,
                    BillMode = PaymentPlans.DirectBill,
                    InvoiceId = invoice.InvoiceId,
                    InvoiceItemId = item.InvoiceItemId,
                    ReceiptId = receipt.ReceiptId,
                    AllocationId = row.AllocationId,
                }));
            }

            invoice.State = Codes.Of(paid.Value);
            invoice.UpdatedAt = now;
            invoice.RecordVersion++;
        }

        var allocatedNow = Money.Sum(rows.Select(r => new Money(r.Amount, currency)), currency);
        var fullyAllocated = alreadyAllocated + allocatedNow.Amount == receipt.Amount;
        var next = PaymentStateModel.Machine.Fire(receiptState, fullyAllocated ? PaymentTrigger.AllocateInFull : PaymentTrigger.AllocatePartially);
        if (next.IsFailure)
        {
            return DomainError.Of(ModuleCode.BIL, "AMOUNT-MISMATCH", $"Receipt {receipt.ReceiptNumber} is {receipt.State} and cannot be allocated.");
        }

        receipt.State = Codes.Of(next.Value);
        receipt.SuspenseReason = null;
        receipt.RecordVersion++;
        db.Allocations.AddRange(rows);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.BIL, "STALE", "The receipt or an invoice changed meanwhile; reload and retry.");
        }
        catch (DbUpdateException ex) when (IsOverAllocation(ex))
        {
            return DomainError.Of(ModuleCode.BIL, "OVER-ALLOCATION", "The database refused an allocation above the receipt or the item (REQ-BIL-130).");
        }

        var lineage = BusinessKeys.Empty.With("receiptId", receipt.ReceiptId.Value.ToString());
        if (lines.Count == 1)
        {
            var invoice = lines[0].Invoice;
            lineage = lineage.With("invoiceId", invoice.InvoiceId.Value.ToString()).With("policyId", invoice.PolicyId.Value.ToString())
                .With("policyTermId", invoice.TermId.Value.ToString()).With("transactionId", invoice.TransactionId.Value.ToString());
        }

        ledger.Post(new EntrySpec(EntryTypes.Allocated, receipt.BillingAccountId, legs, lineage, "bil.Allocation.allocate"));

        var openAfter = await OpenByTermAsync(termsTouched, cancellationToken).ConfigureAwait(false);
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(CashAllocatedV1.Descriptor), "BillingAccount", receipt.BillingAccountId.Value.ToString(),
            new CashAllocatedV1
            {
                ReceiptId = receipt.ReceiptId,
                Allocations = [.. rows.Select(r => new Allocation { ItemId = r.InvoiceItemId, TermId = r.TermId, Amount = new Money(r.Amount, currency) })],
                ArrearsCleared = [.. termsTouched.Select(t => new TermArrearsFlag { TermId = t, ArrearsCleared = openAfter.GetValueOrDefault(t) == 0m })],
                DownPayment = false,
            },
            lineage.With("billingAccountId", receipt.BillingAccountId.Value.ToString())) { OccurredAt = now });
        return rows;
    }

    /// <summary>Open billed amount per term after this unit of work (the database sees the new allocations).</summary>
    private async Task<Dictionary<PolicyTermId, decimal>> OpenByTermAsync(IReadOnlyCollection<PolicyTermId> terms, CancellationToken cancellationToken)
    {
        var items = await db.InvoiceItems.AsNoTracking().Where(i => terms.Contains(i.TermId))
            .Select(i => new { i.TermId, i.InvoiceItemId, i.Amount }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = items.Select(i => i.InvoiceItemId).ToList();
        var allocated = await db.Allocations.AsNoTracking().Where(a => ids.Contains(a.InvoiceItemId))
            .GroupBy(a => a.InvoiceItemId).Select(g => new { Item = g.Key, Sum = g.Sum(a => a.Amount) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var sums = allocated.ToDictionary(a => a.Item, a => a.Sum);
        return items.GroupBy(i => i.TermId).ToDictionary(g => g.Key, g => g.Sum(i => i.Amount - sums.GetValueOrDefault(i.InvoiceItemId)));
    }

    /// <summary>
    /// Forgets the allocation attempt's unsaved changes after a failed save (EF has rolled the database back to its
    /// savepoint): added rows are detached, modified rows reloaded from the database.
    /// </summary>
    public async Task DiscardPendingAsync(CancellationToken cancellationToken)
    {
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            if (entry.State == EntityState.Added)
            {
                entry.State = EntityState.Detached;
            }
            else if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>True when <paramref name="exception"/> is the database's over-allocation refusal.</summary>
    public static bool IsOverAllocation(Exception exception) =>
        exception is DbUpdateException { InnerException: PostgresException { SqlState: OverAllocationState } } or PostgresException { SqlState: OverAllocationState };
}
