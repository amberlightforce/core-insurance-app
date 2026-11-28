using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Services;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Commands;

/// <summary><c>bil.Transfer.money</c> (BO-BIL-13).</summary>
internal sealed record TransferMoney(TransferMoneyRequest Request) : ICommand<TransferMoneyResponse>;

internal sealed class TransferMoneyValidator : AbstractValidator<TransferMoney>
{
    public TransferMoneyValidator()
    {
        RuleFor(c => c.Request.ReceiptIds).NotEmpty().Must(ids => ids.Count <= 50).WithErrorCode("RECEIPTS").WithMessage("Between 1 and 50 receipts.");
        RuleFor(c => c.Request.ReceiptIds).Must(ids => ids.Distinct().Count() == ids.Count).WithErrorCode("DUPLICATE-RECEIPT").WithMessage("Each receipt may appear once.");
        RuleFor(c => c.Request.TargetBillingAccountId).NotEqual(c => c.Request.SourceBillingAccountId).WithErrorCode("SAME-ACCOUNT").WithMessage("The accounts must differ.");
        RuleFor(c => c.Request.Comment).MaximumLength(500);
    }
}

/// <summary>
/// Moves the unapplied cash of receipts in suspense from one billing account to another account of the same payer (BO-BIL-13, REQ-BIL-225..227):
/// one <c>MONEY_TRANSFERRED</c> entry with, per receipt, LA-11 of the source debited against the inter-account clearing LA-20 and LA-20
/// debited against LA-11 of the target (the clearing nets to zero within the entry); the receipt and its workbench item follow the target
/// (reason TRANSFERRED), where staff allocate the cash like any unapplied cash. FIN journalises nothing (memo: the GL account of unallocated receipts does not change).
/// Between accounts of different payers the cash is not moved (BIL-ERR-PAYER-MISMATCH): that is a refund and a new payment.
/// TODO(hardening): partial amounts of a receipt (needs a receipt split), cash that is partly allocated, and a four-eyes approval above an amount limit.
/// </summary>
internal sealed class TransferMoneyHandler(BillingDbContext db, LedgerWriter ledger) : ICommandHandler<TransferMoney, TransferMoneyResponse>
{
    public async Task<Result<TransferMoneyResponse>> HandleAsync(TransferMoney command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = ledger.LegalEntityId;
        var sourceId = new BillingAccountId(request.SourceBillingAccountId);
        var targetId = new BillingAccountId(request.TargetBillingAccountId);
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.LegalEntityId == legalEntity && (a.BillingAccountId == sourceId || a.BillingAccountId == targetId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var source = accounts.SingleOrDefault(a => a.BillingAccountId == sourceId);
        var target = accounts.SingleOrDefault(a => a.BillingAccountId == targetId);
        if (source is null || target is null)
        {
            return BillingErrors.NotFound("billing account");
        }

        if (source.PayerPartyId != target.PayerPartyId)
        {
            return DomainError.Of(ModuleCode.BIL, "PAYER-MISMATCH", "Cash moves only between billing accounts of the same payer.");
        }

        if (source.Currency != target.Currency)
        {
            return DomainError.Of(ModuleCode.BIL, "CURRENCY", $"The accounts are kept in {source.Currency} and {target.Currency}.");
        }

        if (target.Status != Codes.Of(BillingAccountStatus.Active))
        {
            return DomainError.Of(ModuleCode.BIL, "METHOD-NOT-ALLOWED", $"The target account is {target.Status}; cash is transferred to Active accounts.");
        }

        var receiptIds = request.ReceiptIds.ToList();
        var receipts = await db.Receipts.Where(r => r.LegalEntityId == legalEntity && receiptIds.Contains(r.ReceiptId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (receipts.Count != receiptIds.Count)
        {
            return BillingErrors.NotFound("receipt");
        }

        var items = await db.SuspenseItems.Where(i => receiptIds.Contains(i.ReceiptId)).ToDictionaryAsync(i => i.ReceiptId, cancellationToken).ConfigureAwait(false);
        foreach (var receipt in receipts)
        {
            if (receipt.BillingAccountId != sourceId || receipt.State != Codes.Of(PaymentState.Suspense)
                || !items.TryGetValue(receipt.ReceiptId, out var item) || item.State != SuspenseStates.Open)
            {
                return DomainError.Of(ModuleCode.BIL, "SUSPENSE-STATE", $"Receipt {receipt.ReceiptNumber} is not unapplied cash of the source account.");
            }
        }

        var outRule = await ledger.RuleAsync(ledger.Key(EntryTypes.MoneyTransferred, RuleQualifiers.Any, "OUT"), cancellationToken).ConfigureAwait(false);
        var inRule = await ledger.RuleAsync(ledger.Key(EntryTypes.MoneyTransferred, RuleQualifiers.Any, "IN"), cancellationToken).ConfigureAwait(false);
        if (outRule.IsFailure || inRule.IsFailure)
        {
            return (outRule.IsFailure ? outRule.Error : inRule.Error)!;
        }

        var currency = Currency.FromCode(source.Currency);
        var legs = new List<PostingLeg>();
        var moved = new List<MoneyTransferLine>();
        foreach (var receipt in receipts.OrderBy(r => r.ReceiptNumber, StringComparer.Ordinal))
        {
            var item = items[receipt.ReceiptId];
            var money = new Money(item.Amount, currency);
            legs.Add(new PostingLeg(outRule.Value, money, new LineDimensions { BillingAccountId = sourceId, ReceiptId = receipt.ReceiptId }));
            legs.Add(new PostingLeg(inRule.Value, money, new LineDimensions { BillingAccountId = targetId, ReceiptId = receipt.ReceiptId }));
            moved.Add(new MoneyTransferLine { ReceiptId = receipt.ReceiptId, Amount = money });
            receipt.BillingAccountId = targetId;
            receipt.SuspenseReason = SuspenseStates.ReasonTransferred;
            receipt.RecordVersion++;
            item.BillingAccountId = targetId;
            item.Reason = SuspenseStates.ReasonTransferred;
            item.RecordVersion++;
        }

        var lineage = BusinessKeys.Empty.With("sourceBillingAccountId", sourceId.Value.ToString()).With("targetBillingAccountId", targetId.Value.ToString());
        var entryId = ledger.Post(new EntrySpec(EntryTypes.MoneyTransferred, sourceId, legs, lineage, "bil.Transfer.money"));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.BIL, "STALE", "A receipt or its suspense item changed meanwhile; reload and retry.");
        }

        return new TransferMoneyResponse
        {
            Transfers = moved,
            Total = Money.Sum(moved.Select(m => m.Amount), currency),
            LedgerEntryId = entryId,
        };
    }
}

internal sealed class TransferMoneyAuditor : ICommandAuditor<TransferMoney, TransferMoneyResponse>
{
    public CommandAuditFacts Describe(TransferMoney command, Result<TransferMoneyResponse>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.BIL, "BillingAccount", command.Request.SourceBillingAccountId.ToString("D")),
        BusinessKeys = BusinessKeys.Empty.With("billingAccountId", command.Request.SourceBillingAccountId.ToString("D"))
            .With("targetBillingAccountId", command.Request.TargetBillingAccountId.ToString("D")),
        Changes = result is { IsSuccess: true } ? AuditDiff.Compute(null, new { operation = nameof(TransferMoney), receipts = command.Request.ReceiptIds.Count }) : [],
    };
}
