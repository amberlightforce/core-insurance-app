using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Modules.Billing.Services;
using CoreIns.Modules.Billing.Services.Ai;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Commands;

/// <summary><c>bil.Suspense.suggest</c>: ranked invoice candidates for an unapplied receipt (REQ-BIL-135); read-only, facts loaded server-side.</summary>
internal sealed record SuggestSuspense(SuspenseSuggestRequest Request) : ICommand<SuspenseSuggestResponse>;

/// <summary><c>bil.Suspense.transfer</c>: moves unapplied cash to another billing account of the legal entity.</summary>
internal sealed record TransferSuspense(SuspenseTransferRequest Request) : ICommand<SuspenseTransferResponse>;

/// <summary><c>bil.Suspense.refundToSender</c>: proposes paying unapplied cash back to its sender, through the refund approval path.</summary>
internal sealed record RefundSuspenseToSender(SuspenseRefundToSenderRequest Request) : ICommand<SuspenseRefundToSenderResponse>;

internal sealed class TransferSuspenseValidator : AbstractValidator<TransferSuspense>
{
    public TransferSuspenseValidator() => RuleFor(c => c.Request.Comment).MaximumLength(500);
}

internal sealed class RefundSuspenseToSenderValidator : AbstractValidator<RefundSuspenseToSender>
{
    public RefundSuspenseToSenderValidator()
    {
        RuleFor(c => c.Request.ReasonCode).NotEmpty().Must(c => RefundRules.ReasonCodePattern().IsMatch(c ?? string.Empty)).WithErrorCode("REASON_CODE")
            .WithMessage("The reason is a code of capital letters, digits and underscores.");
        RuleFor(c => c.Request.Comment).MaximumLength(500);
    }
}

/// <summary>Shared rules of the suspense workbench (BO-BIL-08).</summary>
internal static class SuspenseWorkbench
{
    /// <summary>A new OPEN item for a receipt that has just moved to SUSPENSE.</summary>
    public static SuspenseItemRow NewItem(ReceiptRow receipt, Instant now) => new()
    {
        SuspenseItemId = Guid.CreateVersion7(),
        LegalEntityId = receipt.LegalEntityId,
        ReceiptId = receipt.ReceiptId,
        BillingAccountId = receipt.BillingAccountId,
        Amount = receipt.Amount,
        Currency = receipt.Currency,
        State = SuspenseStates.Open,
        Reason = receipt.SuspenseReason ?? ReceiptCodes.NoOpenInvoice,
        CreatedAt = now,
        RecordVersion = 1,
    };

    public static DomainError WrongState(SuspenseItemRow item) =>
        DomainError.Of(ModuleCode.BIL, "SUSPENSE-STATE", $"The suspense item is {item.State}; only an OPEN item can be worked.");
}

internal sealed class SuggestSuspenseHandler(BillingDbContext db, ILegalEntityDirectory legalEntities, RequestContext context, SuspenseReader reader)
    : ICommandHandler<SuggestSuspense, SuspenseSuggestResponse>
{
    public async Task<Result<SuspenseSuggestResponse>> HandleAsync(SuggestSuspense command, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
        var item = await db.SuspenseItems.AsNoTracking()
            .SingleOrDefaultAsync(i => i.SuspenseItemId == command.Request.SuspenseItemId && i.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return BillingErrors.NotFound("suspense item");
        }

        if (item.State != SuspenseStates.Open)
        {
            return SuspenseWorkbench.WrongState(item);
        }

        // The payment and the open invoices come from the database, never from the request (NX-HD-14).
        var facts = await SuspenseFacts.LoadAsync(db, legalEntity, item.ReceiptId, cancellationToken).ConfigureAwait(false);
        if (facts is null)
        {
            return BillingErrors.NotFound("receipt");
        }

        var (payment, candidates) = facts.Value;
        return new SuspenseSuggestResponse
        {
            Item = await reader.ViewAsync(item, cancellationToken).ConfigureAwait(false),
            Suggestions =
            [
                .. SuspenseMatching.Rank(payment, candidates)
                    .Select(x => new SuspenseSuggestion { InvoiceNo = x.Candidate.InvoiceNo, Score = x.Score, ExactAmount = x.Candidate.OpenAmount == payment.Amount }),
            ],
            Note = SuspenseMatching.Note,
        };
    }
}

/// <summary>
/// Moves the unapplied cash of an OPEN item from its billing account to another one of the legal entity (same currency, Active):
/// one <c>SUSPENSE_TRANSFERRED</c> entry (LA-11 of the source to LA-29, LA-29 to LA-11 of the target; the source account is the
/// entry's account, the target the dimension of the second leg), and the receipt and item follow the target. The receipt's
/// account is frozen except while it is in suspense (database guard); the item stays OPEN with reason TRANSFERRED, so the
/// target's staff allocate it as any unapplied cash.
/// </summary>
internal sealed class TransferSuspenseHandler(BillingDbContext db, LedgerWriter ledger, SuspenseReader reader) : ICommandHandler<TransferSuspense, SuspenseTransferResponse>
{
    public async Task<Result<SuspenseTransferResponse>> HandleAsync(TransferSuspense command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = ledger.LegalEntityId;
        var item = await db.SuspenseItems.SingleOrDefaultAsync(i => i.SuspenseItemId == request.SuspenseItemId && i.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return BillingErrors.NotFound("suspense item");
        }

        if (item.State != SuspenseStates.Open)
        {
            return SuspenseWorkbench.WrongState(item);
        }

        var targetId = new BillingAccountId(request.TargetBillingAccountId);
        var target = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.BillingAccountId == targetId && a.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return BillingErrors.NotFound("billing account");
        }

        if (targetId == item.BillingAccountId)
        {
            return DomainError.Of(ModuleCode.BIL, "SUSPENSE-STATE", "The cash is already on this billing account.");
        }

        if (target.Status != Codes.Of(BillingAccountStatus.Active))
        {
            return DomainError.Of(ModuleCode.BIL, "METHOD-NOT-ALLOWED", $"The account is {target.Status}; unapplied cash is transferred to Active accounts.");
        }

        if (target.Currency != item.Currency)
        {
            return DomainError.Of(ModuleCode.BIL, "CURRENCY", $"The target account is kept in {target.Currency}.");
        }

        var receipt = await db.Receipts.SingleAsync(r => r.ReceiptId == item.ReceiptId, cancellationToken).ConfigureAwait(false);
        if (receipt.State != Codes.Of(PaymentState.Suspense))
        {
            return DomainError.Of(ModuleCode.BIL, "SUSPENSE-STATE", $"The receipt is {receipt.State}, not in suspense.");
        }

        var outRule = await ledger.RuleAsync(ledger.Key(EntryTypes.SuspenseTransferred, RuleQualifiers.Any, "OUT"), cancellationToken).ConfigureAwait(false);
        var inRule = await ledger.RuleAsync(ledger.Key(EntryTypes.SuspenseTransferred, RuleQualifiers.Any, "IN"), cancellationToken).ConfigureAwait(false);
        if (outRule.IsFailure || inRule.IsFailure)
        {
            return (outRule.IsFailure ? outRule.Error : inRule.Error)!;
        }

        var from = item.BillingAccountId;
        var money = new Money(item.Amount, Currency.FromCode(item.Currency));
        var lineage = BusinessKeys.Empty.With("receiptId", receipt.ReceiptId.Value.ToString()).With("suspenseItemId", item.SuspenseItemId.ToString("D"))
            .With("targetBillingAccountId", targetId.Value.ToString());
        var entryId = ledger.Post(new EntrySpec(
            EntryTypes.SuspenseTransferred,
            from,
            [
                new PostingLeg(outRule.Value, money, new LineDimensions { BillingAccountId = from, ReceiptId = receipt.ReceiptId }),
                new PostingLeg(inRule.Value, money, new LineDimensions { BillingAccountId = targetId, ReceiptId = receipt.ReceiptId }),
            ],
            lineage,
            "bil.Suspense.transfer"));

        receipt.BillingAccountId = targetId;
        receipt.SuspenseReason = SuspenseStates.ReasonTransferred;
        receipt.RecordVersion++;
        item.BillingAccountId = targetId;
        item.Reason = SuspenseStates.ReasonTransferred;
        item.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.BIL, "STALE", "The suspense item or its receipt changed meanwhile; reload and retry.");
        }

        return new SuspenseTransferResponse { Item = await reader.ViewAsync(item, cancellationToken).ConfigureAwait(false), LedgerEntryId = entryId };
    }
}

internal sealed class RefundSuspenseToSenderHandler(BillingDbContext db, LedgerWriter ledger, RefundWorkflow workflow, SuspenseReader reader)
    : ICommandHandler<RefundSuspenseToSender, SuspenseRefundToSenderResponse>
{
    public async Task<Result<SuspenseRefundToSenderResponse>> HandleAsync(RefundSuspenseToSender command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = ledger.LegalEntityId;
        var item = await db.SuspenseItems.SingleOrDefaultAsync(i => i.SuspenseItemId == request.SuspenseItemId && i.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return BillingErrors.NotFound("suspense item");
        }

        if (item.State != SuspenseStates.Open)
        {
            return SuspenseWorkbench.WrongState(item);
        }

        var receipt = await db.Receipts.AsNoTracking().SingleAsync(r => r.ReceiptId == item.ReceiptId, cancellationToken).ConfigureAwait(false);
        if (receipt.State != Codes.Of(PaymentState.Suspense))
        {
            return DomainError.Of(ModuleCode.BIL, "SUSPENSE-STATE", $"The receipt is {receipt.State}, not in suspense.");
        }

        var payee = await db.PayeeAccounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.PayeeAccountId == request.PayeeAccountId && a.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (payee is null)
        {
            return BillingErrors.NotFound("payee account");
        }

        var created = await workflow.CreateForSuspenseAsync(item, receipt, payee, request.ReasonCode, request.Comment, cancellationToken).ConfigureAwait(false);
        return created.IsFailure
            ? created.Error!
            : new SuspenseRefundToSenderResponse { Item = await reader.ViewAsync(item, cancellationToken).ConfigureAwait(false), Refund = created.Value };
    }
}

internal sealed class SuspenseAuditor<TCommand, TResponse> : ICommandAuditor<TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    public CommandAuditFacts Describe(TCommand command, Result<TResponse>? result)
    {
        var id = command switch
        {
            SuggestSuspense c => c.Request.SuspenseItemId,
            TransferSuspense c => c.Request.SuspenseItemId,
            RefundSuspenseToSender c => c.Request.SuspenseItemId,
            _ => Guid.Empty,
        };
        var keys = BusinessKeys.Empty.With("suspenseItemId", id.ToString("D"));
        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.BIL, "SuspenseItem", id.ToString("D")),
            BusinessKeys = keys,
            Changes = result is { IsSuccess: true } ? AuditDiff.Compute(null, new { operation = typeof(TCommand).Name }) : [],
        };
    }
}
