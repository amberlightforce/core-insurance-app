using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Modules.Finance.Contracts;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Billing.Services;

/// <summary>The reversal rows, the ledger entry that mirrors them and the receipt as it stands after the reversal.</summary>
internal sealed record AllocationReversalOutcome(ReceiptRow Receipt, IReadOnlyList<AllocationRow> Reversals, Guid LedgerEntryId);

/// <summary>
/// Reverses allocations of a receipt (BO-BIL-07): the allocation table and the sub-ledger are append-only, so a reversal is a
/// new row with the negated amount that points at the row it undoes (a row is reversed at most once, database-enforced) and one
/// <c>ALLOCATION_REVERSED</c> entry (billed receivable LA-02 back from unapplied cash LA-11 per row; FIN journalises it as the mirror of
/// <c>ALLOCATED</c>). Every sum over allocations is net of reversals, so the freed cash is the receipt's unapplied balance again, invoice
/// items reopen and invoices roll back (Paid to PartiallyPaid, or to Due/Overdue when nothing stays paid). A receipt with nothing left
/// allocated returns to suspense (its workbench item reopens). Refused when the accounting period is closed in FIN, when the term of an
/// allocation is part of a refund being paid out, or when the actor lacks BIL.ALLOCATION_REVERSE authority over the amount. Runs in the
/// caller's transaction (also used by reallocate).
/// </summary>
internal sealed class AllocationReverser(
    BillingDbContext db, RequestContext context, LedgerWriter ledger, Allocator allocator, IClock clock, IAuthorityService authority,
    IServiceProvider services, IOptions<BillingOptions> options)
{
    /// <summary>Refund states in which the cash of a term has been, or is being, paid out.</summary>
    private static readonly string[] PaidOutRefundStates =
        [Codes.Of(RefundState.Disbursing), Codes.Of(RefundState.AwaitingProof), Codes.Of(RefundState.Paid)];

    public async Task<Result<AllocationReversalOutcome>> ReverseAsync(
        PaymentId receiptId, IReadOnlyCollection<Guid>? allocationIds, string reasonCode, string? comment, CancellationToken cancellationToken)
    {
        var legalEntity = ledger.LegalEntityId;
        var receipt = await db.Receipts.SingleOrDefaultAsync(r => r.ReceiptId == receiptId && r.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (receipt is null)
        {
            return BillingErrors.NotFound("receipt");
        }

        var receiptState = Codes.Parse<PaymentState>(receipt.State);
        if (!PaymentStateModel.Machine.CanFire(receiptState, PaymentTrigger.ReturnToSuspense))
        {
            return DomainError.Of(ModuleCode.BIL, "NOT-ALLOCATED", $"Receipt {receipt.ReceiptNumber} is {receipt.State}; there is nothing allocated to reverse.");
        }

        var rows = await db.Allocations.Where(a => a.ReceiptId == receiptId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var reversedIds = rows.Where(r => r.ReversesAllocationId is not null).Select(r => r.ReversesAllocationId!.Value).ToHashSet();
        var live = rows.Where(r => r.ReversesAllocationId is null && !reversedIds.Contains(r.AllocationId)).OrderBy(r => r.RecordedAt).ThenBy(r => r.AllocationId).ToList();

        List<AllocationRow> selected;
        if (allocationIds is null or { Count: 0 })
        {
            selected = live;
        }
        else
        {
            selected = [];
            foreach (var id in allocationIds.Distinct())
            {
                var original = rows.SingleOrDefault(r => r.AllocationId == id && r.ReversesAllocationId is null);
                if (original is null)
                {
                    return BillingErrors.NotFound("allocation of this receipt");
                }

                if (reversedIds.Contains(id))
                {
                    return DomainError.Of(ModuleCode.BIL, "ALREADY-REVERSED", "The allocation was already reversed.");
                }

                selected.Add(original);
            }
        }

        if (selected.Count == 0)
        {
            return DomainError.Of(ModuleCode.BIL, "NOT-ALLOCATED", $"Nothing of receipt {receipt.ReceiptNumber} is allocated to invoices.");
        }

        var currency = Currency.FromCode(receipt.Currency);
        var reversed = Money.Sum(selected.Select(r => new Money(r.Amount, currency)), currency);

        var invoiceIds = selected.Select(r => r.InvoiceId).Distinct().ToList();
        var invoices = await db.Invoices.Where(i => invoiceIds.Contains(i.InvoiceId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var invoice in invoices)
        {
            var state = Codes.Parse<InvoiceState>(invoice.State);
            if (state is not (InvoiceState.Paid or InvoiceState.PartiallyPaid or InvoiceState.Due or InvoiceState.Overdue))
            {
                return DomainError.Of(ModuleCode.BIL, "INVOICE-STATE", $"Invoice {invoice.InvoiceNumber} is {invoice.State}; its allocations can no longer be reversed.");
            }
        }

        var period = await CheckPeriodsAsync(selected, cancellationToken).ConfigureAwait(false);
        if (period is not null)
        {
            return period;
        }

        var refunded = await RefundedAsync(receipt, selected, cancellationToken).ConfigureAwait(false);
        if (refunded is not null)
        {
            return refunded;
        }

        // Amount-limited authority (NX-EC-09 pattern): a refer is a refusal too, the reversal is immediate.
        var check = await authority.CheckAsync(
            new AuthorityCheckRequest(
                context.Actor, context.Roles, SupportAuthorityTypes.AllocationReverse,
                new Dictionary<string, DimensionValue>(StringComparer.Ordinal) { [SupportAuthorityTypes.AmountDimension] = DimensionValue.Of(reversed) },
                new ObjectRef(ModuleCode.BIL, "Receipt", receipt.ReceiptId.Value.ToString("D")), clock.Now),
            cancellationToken).ConfigureAwait(false);
        context.AuthorityChecks.Add(check);
        if (check.Decision != AuthorityDecision.Allow)
        {
            return DomainError.Of(ModuleCode.BIL, "SOD", $"No authority to reverse allocations of {reversed} (BIL.ALLOCATION_REVERSE: {check.ReasonCode}).") with
            {
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["authorityCheckId"] = check.CheckId.ToString(), ["reasonCode"] = check.ReasonCode },
            };
        }

        var rule = await ledger.RuleAsync(ledger.Key(EntryTypes.AllocationReversed, RuleQualifiers.Any, RuleQualifiers.Any), cancellationToken).ConfigureAwait(false);
        if (rule.IsFailure)
        {
            return rule.Error!;
        }

        var itemIds = selected.Select(r => r.InvoiceItemId).Distinct().ToList();
        var items = await db.InvoiceItems.Where(i => itemIds.Contains(i.InvoiceItemId)).ToDictionaryAsync(i => i.InvoiceItemId, cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        var reversals = new List<AllocationRow>();
        var legs = new List<PostingLeg>();
        foreach (var original in selected)
        {
            var row = new AllocationRow
            {
                AllocationId = Guid.CreateVersion7(),
                LegalEntityId = original.LegalEntityId,
                ReceiptId = original.ReceiptId,
                InvoiceId = original.InvoiceId,
                InvoiceItemId = original.InvoiceItemId,
                TermId = original.TermId,
                Amount = -original.Amount,
                Currency = original.Currency,
                RuleId = ReceiptCodes.RuleReversal,
                Source = "MANUAL",
                Actor = context.Actor.ToString(),
                RecordedAt = now,
                ReversesAllocationId = original.AllocationId,
                ReasonCode = reasonCode,
                Comment = comment,
            };
            reversals.Add(row);
            var item = items[original.InvoiceItemId];
            var invoice = invoices.Single(i => i.InvoiceId == original.InvoiceId);
            legs.Add(new PostingLeg(rule.Value, new Money(original.Amount, currency), ServicingDimensions.Apply(
                new LineDimensions
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
                },
                item.TransactionKind, item.CancellationSource, item.TreatmentRuleId)));
        }

        db.Allocations.AddRange(reversals);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_allocation_reverses" })
        {
            return DomainError.Of(ModuleCode.BIL, "ALREADY-REVERSED", "A concurrent request reversed the same allocation first.");
        }
        catch (DbUpdateException ex) when (Allocator.IsOverAllocation(ex))
        {
            return DomainError.Of(ModuleCode.BIL, "OVER-ALLOCATION", "The database refused the reversal (REQ-BIL-130).");
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.BIL, "STALE", "The receipt or an invoice changed meanwhile; reload and retry.");
        }

        var lineage = BusinessKeys.Empty.With("receiptId", receipt.ReceiptId.Value.ToString());
        if (invoices.Count == 1)
        {
            var invoice = invoices[0];
            lineage = lineage.With("invoiceId", invoice.InvoiceId.Value.ToString()).With("policyId", invoice.PolicyId.Value.ToString())
                .With("policyTermId", invoice.TermId.Value.ToString()).With("transactionId", invoice.TransactionId.Value.ToString());
        }

        var entryId = ledger.Post(new EntrySpec(EntryTypes.AllocationReversed, receipt.BillingAccountId, legs, lineage, "bil.Allocation.unallocate"));

        // The database now sees the reversals: roll the items, invoices and the receipt back from the net allocations.
        var open = await allocator.OpenByItemAsync(invoiceIds, cancellationToken).ConfigureAwait(false);
        foreach (var item in items.Values.Where(i => open.GetValueOrDefault(i.InvoiceItemId) > 0m && i.State == Codes.Of(InvoiceItemState.Settled)))
        {
            item.State = Codes.Of(InvoiceItemState.Open);
        }

        var today = ledger.Today;
        var cash = await db.Allocations.AsNoTracking().Where(a => invoiceIds.Contains(a.InvoiceId)).GroupBy(a => a.InvoiceId)
            .Select(g => new { Invoice = g.Key, Sum = g.Sum(a => a.Amount) }).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var invoice in invoices)
        {
            var state = Codes.Parse<InvoiceState>(invoice.State);
            var stillPaid = cash.FirstOrDefault(c => c.Invoice == invoice.InvoiceId)?.Sum > 0m;
            var trigger = stillPaid ? InvoiceTrigger.AllocationReversedPartially
                : invoice.DueDate < today ? InvoiceTrigger.PaymentReversedAfterDue : InvoiceTrigger.PaymentReversedBeforeDue;
            // Due and Overdue invoices with nothing paid stay as they are; a partly paid Overdue invoice with nothing paid left remains Overdue.
            if (state is InvoiceState.Paid or InvoiceState.PartiallyPaid && InvoiceStateModel.Machine.Fire(state, trigger) is { IsSuccess: true } next)
            {
                invoice.State = Codes.Of(next.Value);
            }

            invoice.UpdatedAt = now;
            invoice.RecordVersion++;
        }

        var allocatedNet = await db.Allocations.Where(a => a.ReceiptId == receipt.ReceiptId).SumAsync(a => a.Amount, cancellationToken).ConfigureAwait(false);
        var receivablePaid = await db.ReceivableAllocations.Where(a => a.ReceiptId == receipt.ReceiptId).SumAsync(a => a.Amount, cancellationToken).ConfigureAwait(false);
        var toSuspense = allocatedNet + receivablePaid == 0m;
        receipt.State = Codes.Of(PaymentStateModel.Machine.FireOrThrow(receiptState, toSuspense ? PaymentTrigger.ReturnToSuspense : PaymentTrigger.Unallocate));
        receipt.RecordVersion++;
        if (toSuspense)
        {
            receipt.SuspenseReason = ReceiptCodes.Unallocated;
            var item = await db.SuspenseItems.SingleOrDefaultAsync(i => i.ReceiptId == receipt.ReceiptId, cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                db.SuspenseItems.Add(SuspenseWorkbench.NewItem(receipt, now));
            }
            else
            {
                // The unapplied cash is a workbench item again (allocate, transfer or refund it).
                item.State = SuspenseStates.Open;
                item.Reason = ReceiptCodes.Unallocated;
                item.BillingAccountId = receipt.BillingAccountId;
                item.ResolvedAt = null;
                item.ResolvedBy = null;
                item.RecordVersion++;
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.BIL, "STALE", "The receipt or an invoice changed meanwhile; reload and retry.");
        }

        // TODO(hardening): publish an allocation-reversed event for POL/CLM (arrears flags) and reopen a delinquency case cured by the reversed cash.
        return new AllocationReversalOutcome(receipt, reversals, entryId);
    }

    /// <summary>Refuses when the period of a reversed allocation or of today (where the reversal posts) is closed in FIN.</summary>
    private async Task<DomainError?> CheckPeriodsAsync(IReadOnlyList<AllocationRow> selected, CancellationToken cancellationToken)
    {
        if (services.GetService<IFinancePeriodService>() is not { } periods || context.LegalEntity is not { } legalEntity)
        {
            return null;
        }

        var zone = options.Value.Zone;
        var dates = selected.Select(r => r.RecordedAt.ToBusinessDate(zone)).Append(ledger.Today).Distinct().ToList();
        foreach (var date in dates)
        {
            var status = await periods.StatusAsync(legalEntity.Value, ValidAt.From(date), cancellationToken).ConfigureAwait(false);
            if (status.ClosedForPosting == true)
            {
                return DomainError.Of(ModuleCode.BIL, "PERIOD-LOCKED", $"The accounting period {status.Period} is closed; allocations cannot be reversed into or out of it.");
            }
        }

        return null;
    }

    /// <summary>Refuses when a term of the allocations is part of a refund that is being, or has been, paid out.</summary>
    private async Task<DomainError?> RefundedAsync(ReceiptRow receipt, IReadOnlyList<AllocationRow> selected, CancellationToken cancellationToken)
    {
        var terms = selected.Select(r => r.TermId).Distinct().ToList();
        var refunds = await db.Refunds.AsNoTracking()
            .Where(r => r.BillingAccountId == receipt.BillingAccountId && PaidOutRefundStates.Contains(r.State))
            .Select(r => r.RefundId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (refunds.Count == 0)
        {
            return null;
        }

        var hit = await db.RefundCredits.AsNoTracking().AnyAsync(c => refunds.Contains(c.RefundId) && terms.Contains(c.TermId), cancellationToken).ConfigureAwait(false);
        return hit
            ? DomainError.Of(ModuleCode.BIL, "ALLOCATION-REFUNDED", "A term of these allocations is part of a refund that was already released or is being disbursed.")
            : null;
    }
}
