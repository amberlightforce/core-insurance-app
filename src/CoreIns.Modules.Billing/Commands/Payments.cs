using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Modules.Billing.Services;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Commands;

/// <summary><c>bil.Payment.take</c>: records money received by bank transfer or at a cashier (REQ-BIL-004, REQ-BIL-126).</summary>
internal sealed record TakePayment(PaymentTakeRequest Request) : ICommand<PaymentTakeResponse>;

internal sealed class TakePaymentValidator : AbstractValidator<TakePayment>
{
    public TakePaymentValidator()
    {
        RuleFor(c => c.Request.Amount).Must(a => a.IsPositive).WithErrorCode("AMOUNT").WithMessage("The amount must be positive.");
        RuleFor(c => c.Request.Amount).Must(a => a.IsRoundedToMinorUnits).WithErrorCode("AMOUNT-ROUNDING").WithMessage("The amount must be rounded to minor units.");
        RuleFor(c => c.Request.BankReference).MaximumLength(140);
        RuleFor(c => c.Request.Method).IsInEnum();
    }
}

/// <summary>
/// Creates a numbered receipt (REQ-BIL-126) and its RECEIVED entry (cash at bank LA-10 → unapplied cash LA-11; bank
/// transfer and cashier money is in the bank or till when recorded, so there is no in-transit step, REQ-BIL-131), and
/// publishes <c>PaymentReceived</c>. With <c>autoAllocate</c> (default) it matches deterministically (REQ-BIL-127):
/// the referenced invoice, else the account's single open invoice whose open amount equals the receipt. Only an exact
/// amount is allocated; anything else stays unapplied in suspense with a reason (REQ-BIL-135) — never a wrong allocation.
/// </summary>
internal sealed class TakePaymentHandler(
    BillingDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    INumberingService numbering,
    IEventPublisher events,
    LedgerWriter ledger,
    Allocator allocator) : ICommandHandler<TakePayment, PaymentTakeResponse>
{
    public async Task<Result<PaymentTakeResponse>> HandleAsync(TakePayment command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
        var account = await db.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.BillingAccountId == request.BillingAccountId && a.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return BillingErrors.NotFound("billing account");
        }

        if (account.Status != Codes.Of(BillingAccountStatus.Active))
        {
            return DomainError.Of(ModuleCode.BIL, "METHOD-NOT-ALLOWED", $"The account is {account.Status}; payments are recorded on Active accounts.");
        }

        if (request.Amount.Currency.Code != account.Currency)
        {
            return DomainError.Of(ModuleCode.BIL, "CURRENCY", $"The account is kept in {account.Currency}.");
        }

        InvoiceRow? referenced = null;
        if (request.InvoiceId is { } invoiceId)
        {
            referenced = await db.Invoices.SingleOrDefaultAsync(
                i => i.InvoiceId == invoiceId && i.LegalEntityId == legalEntity && i.BillingAccountId == account.BillingAccountId, cancellationToken).ConfigureAwait(false);
            if (referenced is null)
            {
                return BillingErrors.NotFound("invoice on this account");
            }
        }

        var now = clock.Now;
        var today = ledger.Today;
        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.Receipt, today), cancellationToken).ConfigureAwait(false);
        var method = request.Method == PaymentTakeRequest.MethodValue.Cashier ? ReceiptCodes.Cashier : ReceiptCodes.BankTransfer;
        var receipt = new ReceiptRow
        {
            ReceiptId = PaymentId.New(),
            LegalEntityId = legalEntity,
            Jurisdiction = account.Jurisdiction,
            BillingAccountId = account.BillingAccountId,
            ReceiptNumber = number.Value,
            Channel = ReceiptCodes.ChannelStaff,
            Method = method,
            Amount = request.Amount.Amount,
            Currency = account.Currency,
            ValueDate = request.ValueDate ?? today,
            AccountingDate = today,
            InvoiceRef = request.InvoiceId,
            BankReference = request.BankReference,
            State = Codes.Of(PaymentStateModel.Machine.Start(PaymentState.Received).Value),
            RecordedAt = now,
            CreatedBy = context.Actor.ToString(),
            RecordVersion = 1,
        };
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var rule = await ledger.RuleAsync(ledger.Key(EntryTypes.Received, RuleQualifiers.Any, RuleQualifiers.Any), cancellationToken).ConfigureAwait(false);
        if (rule.IsFailure)
        {
            return rule.Error!;
        }

        var keys = BusinessKeys.Empty.With("receiptId", receipt.ReceiptId.Value.ToString());
        ledger.Post(new EntrySpec(
            EntryTypes.Received,
            account.BillingAccountId,
            [new PostingLeg(rule.Value, request.Amount, new LineDimensions { BillingAccountId = account.BillingAccountId, ReceiptId = receipt.ReceiptId })],
            keys,
            "bil.Payment.take"));
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PaymentReceivedV1.Descriptor), "BillingAccount", account.BillingAccountId.Value.ToString(),
            new PaymentReceivedV1
            {
                ReceiptId = receipt.ReceiptId,
                ReceiptNumber = ReceiptNumber.Parse(receipt.ReceiptNumber),
                Channel = receipt.Channel,
                Method = receipt.Method,
                Amount = request.Amount,
                ValueDate = receipt.ValueDate,
                AccountingDate = receipt.AccountingDate,
            },
            keys.With("billingAccountId", account.BillingAccountId.Value.ToString())) { OccurredAt = now });

        if (request.AutoAllocate == false)
        {
            return Respond(receipt, [], PaymentTakeResponse.AllocationOutcomeValue.NotRequested);
        }

        var (match, ruleId, reason) = await MatchAsync(account, referenced, request.Amount, cancellationToken).ConfigureAwait(false);
        if (match is null)
        {
            receipt.State = Codes.Of(PaymentStateModel.Machine.FireOrThrow(PaymentState.Received, PaymentTrigger.MoveToSuspense));
            receipt.SuspenseReason = reason;
            receipt.RecordVersion++;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Respond(receipt, [], PaymentTakeResponse.AllocationOutcomeValue.Suspense);
        }

        // The receipt and its RECEIVED entry are saved first, so a lost race on the invoice keeps the money.
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var allocated = await allocator.AllocateAsync(receipt, [(match, request.Amount)], ruleId, "RULE", cancellationToken).ConfigureAwait(false);
        if (allocated.IsFailure)
        {
            if (allocated.Error!.Code.Value is not ("BIL-ERR-STALE" or "BIL-ERR-OVER-ALLOCATION"))
            {
                return allocated.Error!;
            }

            // A concurrent payment allocated the invoice first: EF rolled the failed save back to its savepoint; drop the
            // allocation's pending changes and keep this receipt as unapplied cash (REQ-BIL-135) instead of refusing money.
            await allocator.DiscardPendingAsync(cancellationToken).ConfigureAwait(false);
            receipt.State = Codes.Of(PaymentStateModel.Machine.FireOrThrow(PaymentState.Received, PaymentTrigger.MoveToSuspense));
            receipt.SuspenseReason = ReceiptCodes.ConcurrentAllocation;
            receipt.RecordVersion++;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Respond(receipt, [], PaymentTakeResponse.AllocationOutcomeValue.Suspense);
        }

        return Respond(receipt, allocated.Value, PaymentTakeResponse.AllocationOutcomeValue.Allocated);
    }

    /// <summary>Deterministic matching (REQ-BIL-127 rules 3/4 and 6, account scope): the referenced invoice, else a unique equal open amount.</summary>
    private async Task<(InvoiceRow? Invoice, string RuleId, string? Reason)> MatchAsync(
        BillingAccountRow account, InvoiceRow? referenced, Money amount, CancellationToken cancellationToken)
    {
        if (referenced is not null)
        {
            if (!InvoiceStateModel.Machine.CanFire(Codes.Parse<InvoiceState>(referenced.State), InvoiceTrigger.AllocateInFull))
            {
                return (null, ReceiptCodes.RuleReferencedInvoice, ReceiptCodes.InvoiceNotOpen);
            }

            var open = await allocator.OpenAsync(referenced, cancellationToken).ConfigureAwait(false);
            return open == amount
                ? (referenced, ReceiptCodes.RuleReferencedInvoice, null)
                : (null, ReceiptCodes.RuleReferencedInvoice, ReceiptCodes.AmountMismatch);
        }

        var openStates = new[] { Codes.Of(InvoiceState.Due), Codes.Of(InvoiceState.Overdue), Codes.Of(InvoiceState.PartiallyPaid) };
        var candidates = await db.Invoices
            .Where(i => i.BillingAccountId == account.BillingAccountId && openStates.Contains(i.State))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return (null, ReceiptCodes.RuleUniqueOpenAmount, ReceiptCodes.NoOpenInvoice);
        }

        var matching = new List<InvoiceRow>();
        foreach (var invoice in candidates)
        {
            if (await allocator.OpenAsync(invoice, cancellationToken).ConfigureAwait(false) == amount)
            {
                matching.Add(invoice);
            }
        }

        return matching.Count switch
        {
            1 => (matching[0], ReceiptCodes.RuleUniqueOpenAmount, null),
            0 => (null, ReceiptCodes.RuleUniqueOpenAmount, ReceiptCodes.AmountMismatch),
            _ => (null, ReceiptCodes.RuleUniqueOpenAmount, ReceiptCodes.AmbiguousMatch),
        };
    }

    private static PaymentTakeResponse Respond(ReceiptRow receipt, IReadOnlyList<AllocationRow> allocations, PaymentTakeResponse.AllocationOutcomeValue outcome) =>
        new()
        {
            Receipt = BillingReader.Receipt(receipt, allocations.Sum(a => a.Amount)),
            Allocations = [.. allocations.Select(BillingReader.Allocation)],
            AllocationOutcome = outcome,
        };
}

/// <summary>Audit facts of <c>bil.Payment.take</c>: the receipt, its number and the allocation outcome (no personal data).</summary>
internal sealed class TakePaymentAuditor : ICommandAuditor<TakePayment, PaymentTakeResponse>
{
    public CommandAuditFacts Describe(TakePayment command, Result<PaymentTakeResponse>? result)
    {
        var keys = BusinessKeys.Empty.With("billingAccountId", command.Request.BillingAccountId.Value.ToString());
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.BIL, "BillingAccount", command.Request.BillingAccountId), BusinessKeys = keys };
        }

        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.BIL, "Receipt", ok.Value.Receipt.ReceiptId.Value.ToString("D")),
            ObjectNumber = ok.Value.Receipt.ReceiptNumber.Value,
            BusinessKeys = keys.With("receiptId", ok.Value.Receipt.ReceiptId.Value.ToString()),
            Changes = AuditDiff.Compute(null, new
            {
                amount = ok.Value.Receipt.Amount.ToString(),
                method = ok.Value.Receipt.Method,
                outcome = ok.Value.AllocationOutcome.ToString(),
                allocations = ok.Value.Allocations.Count,
            }),
        };
    }
}

/// <summary><c>bil.Allocation.allocate</c>: allocates a receipt's unapplied cash to whole invoices (REQ-BIL-130, REQ-BIL-136).</summary>
internal sealed record AllocateReceipt(AllocationAllocateRequest Request) : ICommand<AllocationAllocateResponse>;

internal sealed class AllocateReceiptValidator : AbstractValidator<AllocateReceipt>
{
    public AllocateReceiptValidator()
    {
        RuleFor(c => c.Request.Lines).NotEmpty();
        RuleFor(c => c.Request.Lines).Must(lines => lines.Select(l => l.InvoiceId).Distinct().Count() == lines.Count)
            .WithErrorCode("DUPLICATE-INVOICE").WithMessage("Each invoice may appear once.");
        RuleForEach(c => c.Request.Lines).Must(l => l.Amount.IsPositive && l.Amount.IsRoundedToMinorUnits)
            .WithErrorCode("AMOUNT").WithMessage("Amounts must be positive and rounded to minor units.");
    }
}

internal sealed class AllocateReceiptHandler(BillingDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, Allocator allocator)
    : ICommandHandler<AllocateReceipt, AllocationAllocateResponse>
{
    public async Task<Result<AllocationAllocateResponse>> HandleAsync(AllocateReceipt command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
        var receiptId = request.ReceiptId;
        var receipt = await db.Receipts.SingleOrDefaultAsync(r => r.ReceiptId == receiptId && r.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (receipt is null)
        {
            return BillingErrors.NotFound("receipt");
        }

        var lines = new List<(InvoiceRow, Money)>();
        foreach (var line in request.Lines)
        {
            var invoice = await db.Invoices.SingleOrDefaultAsync(
                i => i.InvoiceId == line.InvoiceId && i.LegalEntityId == legalEntity && i.BillingAccountId == receipt.BillingAccountId, cancellationToken).ConfigureAwait(false);
            if (invoice is null)
            {
                return BillingErrors.NotFound("invoice on the receipt's account");
            }

            lines.Add((invoice, line.Amount));
        }

        var allocated = await allocator.AllocateAsync(receipt, lines, ReceiptCodes.RuleManual, "MANUAL", cancellationToken).ConfigureAwait(false);
        if (allocated.IsFailure)
        {
            return allocated.Error!;
        }

        var all = await db.Allocations.Where(a => a.ReceiptId == receiptId).SumAsync(a => a.Amount, cancellationToken).ConfigureAwait(false);
        return new AllocationAllocateResponse
        {
            Allocations = [.. allocated.Value.Select(BillingReader.Allocation)],
            Receipt = BillingReader.Receipt(receipt, all),
        };
    }
}

internal sealed class AllocateReceiptAuditor : ICommandAuditor<AllocateReceipt, AllocationAllocateResponse>
{
    public CommandAuditFacts Describe(AllocateReceipt command, Result<AllocationAllocateResponse>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.BIL, "Receipt", command.Request.ReceiptId.Value.ToString("D")),
        BusinessKeys = BusinessKeys.Empty.With("receiptId", command.Request.ReceiptId.Value.ToString()),
        Changes = result is { IsSuccess: true } ok
            ? AuditDiff.Compute(null, new { allocations = ok.Value.Allocations.Count, receiptState = ok.Value.Receipt.State.ToString() })
            : [],
    };
}

/// <summary>Errors shared by the module's commands and queries.</summary>
internal static class BillingErrors
{
    public static DomainError NotFound(string what) => DomainError.Of(ModuleCode.BIL, "NOT-FOUND", $"The {what} does not exist in your legal entity.");
}
