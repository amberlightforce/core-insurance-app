using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Claims.Events;

/// <summary>
/// <c>plt.ApprovalDecided</c> → apply the decision to the referred transaction set (REQ-CLM-109, -111, -112). Only CLM's
/// approval types are handled; idempotent and race-safe (only a PendingApproval set moves, under the claim and set locks).
/// </summary>
internal sealed class ApprovalDecidedHandler(ICommandHandler<ApplyApprovalDecision, ApprovalOutcome> apply) : IEventHandler<ApprovalDecidedV1>
{
    public const string Name = "CLM.ApprovalDecided.ApplyTransactionSet";

    public async Task HandleAsync(EventEnvelope envelope, ApprovalDecidedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.ApprovalType is not (ClaimApprovals.TransactionSet or DisbursementApproval.ClaimPaymentType))
        {
            return;
        }

        Unwrap(await apply.HandleAsync(new ApplyApprovalDecision(payload.RequestId, payload.Decision), cancellationToken).ConfigureAwait(false));
    }

    internal static void Unwrap<T>(Result<T> result)
    {
        if (result.IsFailure)
        {
            throw new DomainException(result.Error!);
        }
    }
}

/// <summary><c>bil.DisbursementIssued</c> → claim payment Issued and <c>PaymentIssued</c> (REQ-CLM-004, -128).</summary>
internal sealed class DisbursementIssuedHandler(ICommandHandler<RecordDisbursementOutcome, string> record) : IEventHandler<DisbursementIssuedV1>
{
    public const string Name = "CLM.DisbursementIssued.IssuePayment";

    public async Task HandleAsync(EventEnvelope envelope, DisbursementIssuedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.SourceType == DisbursementCodes.ClaimPayment && Guid.TryParseExact(payload.SourceId, "D", out var paymentId))
        {
            ApprovalDecidedHandler.Unwrap(await record.HandleAsync(
                new RecordDisbursementOutcome(new ClaimPaymentId(paymentId), payload.DisbursementId, Cleared: false), cancellationToken).ConfigureAwait(false));
        }
    }
}

/// <summary><c>bil.DisbursementCleared</c> → claim payment Cleared (REQ-CLM-128).</summary>
internal sealed class DisbursementClearedHandler(ICommandHandler<RecordDisbursementOutcome, string> record) : IEventHandler<DisbursementClearedV1>
{
    public const string Name = "CLM.DisbursementCleared.ClearPayment";

    public async Task HandleAsync(EventEnvelope envelope, DisbursementClearedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.SourceType == DisbursementCodes.ClaimPayment && Guid.TryParseExact(payload.SourceId, "D", out var paymentId))
        {
            ApprovalDecidedHandler.Unwrap(await record.HandleAsync(
                new RecordDisbursementOutcome(new ClaimPaymentId(paymentId), payload.DisbursementId, Cleared: true), cancellationToken).ConfigureAwait(false));
        }
    }
}
