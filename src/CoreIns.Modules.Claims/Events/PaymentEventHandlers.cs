using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Claims.Commands.Payments;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Compliance.Contracts.Events;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Claims.Events;

/// <summary>Shared by the BIL correction handlers: the claim payment a disbursement fact names.</summary>
internal static class PaymentFacts
{
    public static bool TryPayment(string sourceType, string sourceId, out ClaimPaymentId id)
    {
        id = default;
        if (sourceType == DisbursementCodes.ClaimPayment && Guid.TryParseExact(sourceId, "D", out var guid))
        {
            id = new ClaimPaymentId(guid);
            return true;
        }

        return false;
    }
}

/// <summary><c>bil.DisbursementStopped</c> to claim payment Stopped, ledger reversed (REQ-CLM-126).</summary>
internal sealed class DisbursementStoppedHandler(ICommandHandler<RecordDisbursementCorrection, string> record) : IEventHandler<DisbursementStoppedV1>
{
    public const string Name = "CLM.DisbursementStopped.StopPayment";

    public async Task HandleAsync(EventEnvelope envelope, DisbursementStoppedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (PaymentFacts.TryPayment(payload.SourceType, payload.SourceId, out var id))
        {
            ApprovalDecidedHandler.Unwrap(await record.HandleAsync(
                new RecordDisbursementCorrection(id, payload.DisbursementId, PaymentStatus.Stopped, payload.ReasonCode), cancellationToken).ConfigureAwait(false));
        }
    }
}

/// <summary><c>bil.DisbursementVoided</c> to claim payment Voided, ledger reversed (REQ-CLM-125).</summary>
internal sealed class DisbursementVoidedHandler(ICommandHandler<RecordDisbursementCorrection, string> record) : IEventHandler<DisbursementVoidedV1>
{
    public const string Name = "CLM.DisbursementVoided.VoidPayment";

    public async Task HandleAsync(EventEnvelope envelope, DisbursementVoidedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (PaymentFacts.TryPayment(payload.SourceType, payload.SourceId, out var id))
        {
            ApprovalDecidedHandler.Unwrap(await record.HandleAsync(
                new RecordDisbursementCorrection(id, payload.DisbursementId, PaymentStatus.Voided, payload.Reason), cancellationToken).ConfigureAwait(false));
        }
    }
}

/// <summary><c>bil.DisbursementReturned</c> to claim payment Returned (the bank returned the credit), ledger reversed.</summary>
internal sealed class DisbursementReturnedHandler(ICommandHandler<RecordDisbursementCorrection, string> record) : IEventHandler<DisbursementReturnedV1>
{
    public const string Name = "CLM.DisbursementReturned.ReturnPayment";

    public async Task HandleAsync(EventEnvelope envelope, DisbursementReturnedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (PaymentFacts.TryPayment(payload.SourceType, payload.SourceId, out var id))
        {
            ApprovalDecidedHandler.Unwrap(await record.HandleAsync(
                new RecordDisbursementCorrection(id, payload.DisbursementId, PaymentStatus.Returned, payload.ReasonCode), cancellationToken).ConfigureAwait(false));
        }
    }
}

/// <summary><c>bil.DisbursementRejected</c> to claim payment Rejected (bank or BIL refused it); ledger reversed when it had been issued.</summary>
internal sealed class DisbursementRejectedHandler(ICommandHandler<RecordDisbursementCorrection, string> record) : IEventHandler<DisbursementRejectedV1>
{
    public const string Name = "CLM.DisbursementRejected.RejectPayment";

    public async Task HandleAsync(EventEnvelope envelope, DisbursementRejectedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (PaymentFacts.TryPayment(payload.SourceType, payload.SourceId, out var id))
        {
            ApprovalDecidedHandler.Unwrap(await record.HandleAsync(
                new RecordDisbursementCorrection(id, payload.DisbursementId, PaymentStatus.Rejected, payload.ReasonCode), cancellationToken).ConfigureAwait(false));
        }
    }
}

/// <summary><c>cmp.FiscalDocRegistered</c> for a claim settlement receipt: the MARK is stored on the payment (REQ-CLM-138).</summary>
internal sealed class FiscalDocRegisteredHandler(ICommandHandler<RecordPaymentFiscalMark, string> record) : IEventHandler<FiscalDocRegisteredV1>
{
    public const string Name = "CLM.FiscalDocRegistered.RecordMark";

    public async Task HandleAsync(EventEnvelope envelope, FiscalDocRegisteredV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.SourceType == SettlementReceipt.SourceType && Guid.TryParseExact(payload.SourceId, "D", out var guid))
        {
            ApprovalDecidedHandler.Unwrap(await record.HandleAsync(new RecordPaymentFiscalMark(new ClaimPaymentId(guid), payload.Mark), cancellationToken).ConfigureAwait(false));
        }
    }
}
