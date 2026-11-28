using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Modules.Billing.Services;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Commands;

/// <summary><c>bil.Allocation.unallocate</c>: reverses allocations of a receipt (BO-BIL-07).</summary>
internal sealed record UnallocateReceipt(AllocationUnallocateRequest Request) : ICommand<AllocationUnallocateResponse>;

/// <summary><c>bil.Allocation.reallocate</c>: unallocate and allocate in one transaction (BO-BIL-07).</summary>
internal sealed record ReallocateReceipt(AllocationReallocateRequest Request) : ICommand<AllocationReallocateResponse>;

internal static class AllocationReversalRules
{
    public static bool ValidReason(string? code) => !string.IsNullOrEmpty(code) && RefundRules.ReasonCodePattern().IsMatch(code);
}

internal sealed class UnallocateReceiptValidator : AbstractValidator<UnallocateReceipt>
{
    public UnallocateReceiptValidator()
    {
        RuleFor(c => c.Request.ReasonCode).Must(AllocationReversalRules.ValidReason).WithErrorCode("REASON_CODE")
            .WithMessage("The reason is a code of capital letters, digits and underscores.");
        RuleFor(c => c.Request.Comment).MaximumLength(500);
    }
}

internal sealed class ReallocateReceiptValidator : AbstractValidator<ReallocateReceipt>
{
    public ReallocateReceiptValidator()
    {
        RuleFor(c => c.Request.ReasonCode).Must(AllocationReversalRules.ValidReason).WithErrorCode("REASON_CODE")
            .WithMessage("The reason is a code of capital letters, digits and underscores.");
        RuleFor(c => c.Request.Comment).MaximumLength(500);
        RuleFor(c => c.Request.Lines).NotEmpty();
        RuleFor(c => c.Request.Lines).Must(lines => lines.Select(l => l.InvoiceId).Distinct().Count() == lines.Count)
            .WithErrorCode("DUPLICATE-INVOICE").WithMessage("Each invoice may appear once.");
        RuleForEach(c => c.Request.Lines).Must(l => l.Amount.IsPositive && l.Amount.IsRoundedToMinorUnits)
            .WithErrorCode("AMOUNT").WithMessage("Amounts must be positive and rounded to minor units.");
    }
}

/// <summary>Net allocated cash of a receipt (reversals included).</summary>
internal static class ReceiptNet
{
    public static async Task<decimal> AllocatedAsync(BillingDbContext db, ReceiptRow receipt, CancellationToken cancellationToken) =>
        await db.Allocations.Where(a => a.ReceiptId == receipt.ReceiptId).SumAsync(a => a.Amount, cancellationToken).ConfigureAwait(false)
        + await db.ReceivableAllocations.Where(a => a.ReceiptId == receipt.ReceiptId).SumAsync(a => a.Amount, cancellationToken).ConfigureAwait(false);
}

internal sealed class UnallocateReceiptHandler(BillingDbContext db, AllocationReverser reverser) : ICommandHandler<UnallocateReceipt, AllocationUnallocateResponse>
{
    public async Task<Result<AllocationUnallocateResponse>> HandleAsync(UnallocateReceipt command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var outcome = await reverser.ReverseAsync(request.ReceiptId, request.AllocationIds, request.ReasonCode, request.Comment, cancellationToken).ConfigureAwait(false);
        if (outcome.IsFailure)
        {
            return outcome.Error!;
        }

        return new AllocationUnallocateResponse
        {
            Reversals = [.. outcome.Value.Reversals.Select(BillingReader.Allocation)],
            Receipt = BillingReader.Receipt(outcome.Value.Receipt, await ReceiptNet.AllocatedAsync(db, outcome.Value.Receipt, cancellationToken).ConfigureAwait(false)),
            LedgerEntryId = outcome.Value.LedgerEntryId,
        };
    }
}

internal sealed class ReallocateReceiptHandler(BillingDbContext db, ILegalEntityDirectory legalEntities, RequestContext context, AllocationReverser reverser, Allocator allocator)
    : ICommandHandler<ReallocateReceipt, AllocationReallocateResponse>
{
    public async Task<Result<AllocationReallocateResponse>> HandleAsync(ReallocateReceipt command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
        var receiptId = request.ReceiptId;

        // The target invoices are resolved up front so a bad target fails before anything is reversed (the transaction would roll back anyway).
        var receipt = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(r => r.ReceiptId == receiptId && r.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
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
                return BillingErrors.NotFound("invoice on the receipt account");
            }

            lines.Add((invoice, line.Amount));
        }

        var reversed = await reverser.ReverseAsync(receiptId, request.AllocationIds, request.ReasonCode, request.Comment, cancellationToken).ConfigureAwait(false);
        if (reversed.IsFailure)
        {
            return reversed.Error!;
        }

        // The reverser rolled the tracked invoice rows back; the lines refer to the same tracked rows.
        var allocated = await allocator.AllocateAsync(reversed.Value.Receipt, lines, ReceiptCodes.RuleManual, "MANUAL", cancellationToken).ConfigureAwait(false);
        if (allocated.IsFailure)
        {
            return allocated.Error!;
        }

        return new AllocationReallocateResponse
        {
            Reversals = [.. reversed.Value.Reversals.Select(BillingReader.Allocation)],
            Allocations = [.. allocated.Value.Select(BillingReader.Allocation)],
            Receipt = BillingReader.Receipt(reversed.Value.Receipt, await ReceiptNet.AllocatedAsync(db, reversed.Value.Receipt, cancellationToken).ConfigureAwait(false)),
            LedgerEntryId = reversed.Value.LedgerEntryId,
        };
    }
}

internal sealed class AllocationReversalAuditor<TCommand, TResponse> : ICommandAuditor<TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    public CommandAuditFacts Describe(TCommand command, Result<TResponse>? result)
    {
        var (receiptId, reason, named) = command switch
        {
            UnallocateReceipt c => (c.Request.ReceiptId, c.Request.ReasonCode, c.Request.AllocationIds?.Count ?? 0),
            ReallocateReceipt c => (c.Request.ReceiptId, c.Request.ReasonCode, c.Request.AllocationIds?.Count ?? 0),
            _ => (default, string.Empty, 0),
        };
        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.BIL, "Receipt", receiptId.Value.ToString("D")),
            BusinessKeys = BusinessKeys.Empty.With("receiptId", receiptId.Value.ToString()),
            Changes = result is { IsSuccess: true } ? AuditDiff.Compute(null, new { operation = typeof(TCommand).Name, reason, named }) : [],
        };
    }
}
