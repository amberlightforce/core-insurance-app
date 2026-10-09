using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Platform.Events;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Services;

internal sealed class ReceivableMatching(BillingDbContext db, LedgerWriter ledger, IEventPublisher events, IClock clock)
{
    public async Task<Result<ReceivableRow>> ResolveAsync(PaymentTakeRequest r, LegalEntityId entity, CancellationToken ct)
    {
        if (r.InvoiceId is not null) return DomainError.Of(ModuleCode.BIL, "VALIDATION", "Receivable matching cannot also reference an invoice.");
        var reference = r.PaymentReference ?? r.BankReference;
        var matches = await db.Receivables.Where(x => x.LegalEntityId == entity && x.BillingAccountId == r.BillingAccountId
            && (r.ReceivableId == null || x.ReceivableId == r.ReceivableId)
            && (reference == null || x.PaymentReference == reference || (x.SourceType == "FS_CLEARING" && x.StatementRef == reference)))
            .Take(2).ToListAsync(ct).ConfigureAwait(false);
        return matches.Count switch
        {
            0 => BillingErrors.NotFound("receivable on this account"),
            1 => matches[0],
            _ => DomainError.Of(ModuleCode.BIL, "VALIDATION", "Statement reference is ambiguous; use the receivable id or RF reference."),
        };
    }

    public async Task<Result<decimal>> AllocateAsync(ReceiptRow receipt, ReceivableRow row, CancellationToken ct)
    {
        // Lock order agrees with the database allocation guard: receipt, then receivable.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT receipt_id FROM bil.receipt WHERE receipt_id = {receipt.ReceiptId.Value} FOR UPDATE", ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT receivable_id FROM bil.receivable WHERE receivable_id = {row.ReceivableId} FOR UPDATE", ct).ConfigureAwait(false);
        var paid = await db.ReceivableAllocations.Where(a => a.ReceivableId == row.ReceivableId).SumAsync(a => a.Amount, ct).ConfigureAwait(false);
        var used = await db.ReceivableAllocations.Where(a => a.ReceiptId == receipt.ReceiptId).SumAsync(a => a.Amount, ct).ConfigureAwait(false)
            + await db.Allocations.Where(a => a.ReceiptId == receipt.ReceiptId).SumAsync(a => a.Amount, ct).ConfigureAwait(false);
        var amount = Math.Min(row.Amount - paid, receipt.Amount - used);
        if (amount <= 0) return 0m;
        var rule = await ledger.RuleAsync(ledger.Key("RECEIVABLE_COLLECTED", RuleQualifiers.Any, row.SourceType), ct).ConfigureAwait(false);
        if (rule.IsFailure) return rule.Error!;
        var allocation = new ReceivableAllocationRow { AllocationId = Guid.CreateVersion7(), LegalEntityId = row.LegalEntityId, ReceiptId = receipt.ReceiptId, ReceivableId = row.ReceivableId, Amount = amount, Currency = row.Currency, AllocatedAt = clock.Now };
        db.ReceivableAllocations.Add(allocation);
        var money = new Money(amount, Currency.FromCode(row.Currency));
        var keys = BusinessKeys.Empty.With("receiptId", receipt.ReceiptId.Value.ToString("D")).With("billingAccountId", row.BillingAccountId.Value.ToString("D")).With("receivableId", row.ReceivableId.ToString("D")).With("allocationId", allocation.AllocationId.ToString("D"));
        ledger.Post(new EntrySpec("RECEIVABLE_COLLECTED", row.BillingAccountId,
            [new PostingLeg(rule.Value, money, new LineDimensions { BillingAccountId = row.BillingAccountId, ReceiptId = receipt.ReceiptId, AllocationId = allocation.AllocationId, ClaimId = row.ClaimId, RecoveryId = row.RecoveryId, StatementRef = row.StatementRef, CounterpartyPartyId = row.CounterpartyPartyId, SourceType = row.SourceType, SourceId = row.SourceId })], keys, "bil.Payment.take"));
        events.Publish(new OutgoingEvent(EventDescriptor.From(CashAllocatedV1.Descriptor), "BillingAccount", row.BillingAccountId.Value.ToString("D"), new CashAllocatedV1
        {
            ReceiptId = receipt.ReceiptId, Allocations = [], ArrearsCleared = [], DownPayment = false,
            ReceivableId = row.ReceivableId, ReceivableAmount = money, AllocationId = allocation.AllocationId, ClaimId = row.ClaimId,
            RecoveryId = row.RecoveryId, StatementRef = row.StatementRef, SourceType = row.SourceType,
        }, keys) { OccurredAt = clock.Now });
        receipt.State = amount + used == receipt.Amount ? "ALLOCATED" : "PARTIALLY_ALLOCATED";
        receipt.RecordVersion++;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return amount;
    }
}


