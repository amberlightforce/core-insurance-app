using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Services;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AuthorityCheckRequest = CoreIns.Platform.Authority.AuthorityCheckRequest;
using StoredRefundState = CoreIns.Modules.Billing.Contracts.RefundState;

namespace CoreIns.Modules.Billing.Commands;

/// <summary>
/// BO-BIL-08: the refund of unapplied cash to its sender runs through the refund workflow (one approval path, one segregation-of-duties
/// rule set, one disbursement gate): BIL.REFUND authority over the amount, a second person always decides (the sender is not
/// verifiable from the receipt), then the disbursement checks the payee's VoP, screening and cooling-off. It has no credit lines:
/// approval moves the cash LA-11 to LA-12 (<c>SUSPENSE_REFUND_APPROVED</c>) instead of a credit.
/// </summary>
internal sealed partial class RefundWorkflow
{
    /// <summary>Proposes the refund of <paramref name="item"/> to <paramref name="payee"/>; the item waits as REFUND_PENDING.</summary>
    public async Task<Result<RefundView>> CreateForSuspenseAsync(
        SuspenseItemRow item, ReceiptRow receipt, PayeeAccountRow payee, string reasonCode, string? comment, CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var legalEntity = ledger.LegalEntityId;
        var now = clock.Now;
        await LockAsync(item.BillingAccountId.Value, cancellationToken).ConfigureAwait(false);

        if (await db.Refunds.AsNoTracking().Where(r => r.BillingAccountId == item.BillingAccountId && !Closed.Contains(r.State))
                .Select(r => (RefundId?)r.RefundId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) is { } open)
        {
            return OpenError(open);
        }

        if (receipt.Currency != Currency.EUR.Code)
        {
            return DomainError.Of(ModuleCode.BIL, "CURRENCY", "Refunds are paid in EUR (D-SL2-06).");
        }

        // Payee: an Active, verified REFUND account (REQ-BIL-186, -187); the disbursement re-checks it and screens the payee.
        if (payee.Purpose != DisbursementCodes.RefundPurpose || payee.Status != Codes.Of(PayeeAccountStatus.Active) || payee.ValidTo is not null)
        {
            return DomainError.Of(ModuleCode.BIL, "PAYEE-ACCOUNT", "A refund is paid to an Active REFUND account of the sender (REQ-BIL-187).");
        }

        if (Codes.Parse<PayeeVerification>(payee.VerificationStatus) is not (PayeeVerification.VopMatched or PayeeVerification.Confirmed))
        {
            return DomainError.Of(ModuleCode.BIL, "PAYEE-UNVERIFIED", $"Verification of payee is {payee.VerificationStatus}; a refund is paid to a verified account only (REQ-BIL-186).");
        }

        var total = new Money(receipt.Amount, Currency.EUR);
        var payeeChanged = payee.IsChange;
        var check = await authority.CheckAsync(
            new AuthorityCheckRequest(
                context.Actor, context.Roles, SupportAuthorityTypes.Refund, RefundAuthority.Dimensions(total, payeeChanged, reasonCode, RefundAuthority.MixedProducts),
                new ObjectRef(ModuleCode.BIL, "SuspenseItem", item.SuspenseItemId.ToString("D")), now),
            cancellationToken).ConfigureAwait(false);
        context.AuthorityChecks.Add(check);
        if (check.Decision == AuthorityDecision.Deny)
        {
            return DomainError.Of(ModuleCode.BIL, "NOT-PERMITTED", $"No authority for a refund of {total} (BIL.REFUND: {check.ReasonCode}).") with
            {
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["authorityCheckId"] = check.CheckId.ToString(), ["reasonCode"] = check.ReasonCode },
            };
        }

        var refundId = RefundId.New();
        var sourceId = refundId.Value.ToString("D");
        var hash = DisbursementContent.Hash(DisbursementCodes.RefundPayment, sourceId, payee.PartyId, payee.PayeeAccountId, total);
        var machine = RefundStateModel.Machine;
        var state = machine.FireOrThrow(machine.Start(StoredRefundState.Proposed).Value, RefundTrigger.Submit);
        var refund = new RefundRow
        {
            RefundId = refundId,
            LegalEntityId = legalEntity,
            Jurisdiction = ledger.Jurisdiction.Value,
            BillingAccountId = item.BillingAccountId,
            State = Codes.Of(state),
            ApprovalState = Codes.Of(RefundApprovalStateCode.Pending),
            Amount = total.Amount,
            Currency = total.Currency.Code,
            PayeePartyId = payee.PartyId,
            PayeeAccountId = payee.PayeeAccountId,
            PayeeChanged = payeeChanged,
            PayeeAccountChangedBy = payee.CreatedBy,
            PayoutMethod = DisbursementCodes.SepaCreditTransfer,
            ReasonCode = reasonCode,
            Comment = comment,
            CreditSetKey = Sha256Hash.ComputeUtf8("SUSPENSE:" + receipt.ReceiptId.Value.ToString("D")).Value,
            Participants = [.. ActorKeys()],
            RequestedBy = context.Actor.ToString(),
            ApprovalContentHash = hash.Value,
            ProposedAt = now,
            RecordVersion = 1,
        };
        db.Refunds.Add(refund);
        item.State = SuspenseStates.RefundPending;
        item.RefundId = refundId;
        item.RecordVersion++;
        var saved = await TrySaveAsync(cancellationToken).ConfigureAwait(false);
        if (saved is not null)
        {
            return saved;
        }

        // Always a second person (the sender cannot be verified from the receipt, TODO(hardening) in BO-BIL-11). A dry run creates no request.
        if (!context.DryRun)
        {
            var approvals = services.GetService<IPlatformApprovalService>();
            if (approvals is null)
            {
                return DomainError.Of(ModuleCode.BIL, "APPROVAL-MISMATCH", "The approval service is not available; the refund cannot be sent for approval.");
            }

            try
            {
                var response = await approvals.RequestAsync(
                    new ApprovalRequestRequest
                    {
                        Type = DisbursementApproval.RefundType,
                        ObjectRef = DisbursementApproval.RefundSubject(sourceId),
                        PayloadHash = hash,
                        Authority = new ApprovalAuthority
                        {
                            Type = SupportAuthorityTypes.Refund.Value,
                            Amount = total,
                            Codes = RefundAuthority.ApprovalCodes(total, payeeChanged, reasonCode, RefundAuthority.MixedProducts),
                        },
                        ReferralRole = opts.RefundApproverRole,
                        Reason = $"Refund to sender of unapplied cash {total} (receipt {receipt.ReceiptNumber}): {(payeeChanged ? "payee account changed; " : string.Empty)}requester check {check.Decision} ({check.ReasonCode}).",
                    },
                    CommandOptions.New(),
                    cancellationToken).ConfigureAwait(false);
                refund.ApprovalRequestId = response.Request.RequestId;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DomainException ex)
            {
                return ex.Error;
            }
        }

        return (await reader.ViewsAsync([refund], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>On approval: unapplied cash LA-11 to refunds payable LA-12, the receipt Refunded and the item REFUNDED (rolled back with the command if the disbursement is refused).</summary>
    private async Task<Result<bool>> ApproveSuspenseRefundAsync(RefundRow refund, SuspenseItemRow item, Instant now, CancellationToken cancellationToken)
    {
        var rule = await ledger.RuleAsync(ledger.Key(EntryTypes.SuspenseRefundApproved, RuleQualifiers.Any, RuleQualifiers.Any), cancellationToken).ConfigureAwait(false);
        if (rule.IsFailure)
        {
            return rule.Error!;
        }

        var receipt = await db.Receipts.SingleAsync(r => r.ReceiptId == item.ReceiptId, cancellationToken).ConfigureAwait(false);
        if (receipt.State != Codes.Of(PaymentState.Suspense) || receipt.BillingAccountId != item.BillingAccountId)
        {
            return DomainError.Of(ModuleCode.BIL, "SUSPENSE-STATE", $"The receipt is {receipt.State}; the unapplied cash is no longer there to refund.");
        }

        var sourceId = refund.RefundId.Value.ToString("D");
        var money = new Money(refund.Amount, Currency.FromCode(refund.Currency));
        var lineage = BusinessKeys.Empty.With("billingAccountId", refund.BillingAccountId.Value.ToString()).With("refundId", sourceId)
            .With("receiptId", receipt.ReceiptId.Value.ToString()).With("suspenseItemId", item.SuspenseItemId.ToString("D"));
        refund.ApprovedEntryId = ledger.Post(new EntrySpec(
            EntryTypes.SuspenseRefundApproved,
            refund.BillingAccountId,
            [
                new PostingLeg(rule.Value, money, new LineDimensions
                {
                    BillingAccountId = refund.BillingAccountId,
                    ReceiptId = receipt.ReceiptId,
                    SourceType = DisbursementCodes.RefundPayment,
                    SourceId = sourceId,
                }),
            ],
            lineage,
            "bil.Suspense.refundToSender"));
        receipt.State = Codes.Of(PaymentStateModel.Machine.FireOrThrow(PaymentState.Suspense, PaymentTrigger.Refund));
        receipt.SuspenseReason = null;
        receipt.RecordVersion++;
        item.State = SuspenseStates.Refunded;
        item.ResolvedAt = now;
        item.ResolvedBy = context.Actor.ToString();
        item.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
