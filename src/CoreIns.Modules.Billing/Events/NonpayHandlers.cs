using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Compliance.Contracts.Events;
using CoreIns.Modules.Documents.Contracts.Events;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Billing.Events;

/// <summary>
/// <c>doc.DocumentDelivered</c> → <c>bil.Delinquency.noticeDelivered</c>: delivery proof of the non-payment notice carries the
/// notification date that starts the BIL_NONPAY_NOTICE clock (D-SL6-03). Documents that are not a case's notice are ignored.
/// </summary>
internal sealed class DocumentDeliveredNonpayHandler(ICommandHandler<NoticeDelivered, int> delivered) : IEventHandler<DocumentDeliveredV1>
{
    public const string Name = "BIL.DocumentDelivered.NonpayNotice";

    public async Task HandleAsync(EventEnvelope envelope, DocumentDeliveredV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(payload);
        if (DocumentIdOf(envelope) is not { } id)
        {
            return;
        }

        ChargeDeltaEmittedHandler.Unwrap(await delivered.HandleAsync(
            new NoticeDelivered(id, payload.NotificationDate, payload.Medium, $"{payload.ProofLevel}:{payload.DeliveryId.Value:D}"), cancellationToken).ConfigureAwait(false));
    }

    internal static DocumentId? DocumentIdOf(EventEnvelope envelope) =>
        envelope.BusinessKeys.TryGetValue("documentId", out var value) && Guid.TryParse(value, out var id) ? new DocumentId(id) : null;
}

/// <summary><c>doc.DeliveryFailed</c> → <c>bil.Delinquency.noticeFailed</c>: a returned letter starts no clock (D-SL6-03).</summary>
internal sealed class DeliveryFailedNonpayHandler(ICommandHandler<NoticeFailed, int> failed) : IEventHandler<DeliveryFailedV1>
{
    public const string Name = "BIL.DeliveryFailed.NonpayNotice";

    public async Task HandleAsync(EventEnvelope envelope, DeliveryFailedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(payload);
        if (DocumentDeliveredNonpayHandler.DocumentIdOf(envelope) is not { } id)
        {
            return;
        }

        ChargeDeltaEmittedHandler.Unwrap(await failed.HandleAsync(new NoticeFailed(id, payload.Reason.ToString()), cancellationToken).ConfigureAwait(false));
    }
}

/// <summary><c>cmp.ClockElapsed</c> of BIL_NONPAY_NOTICE → <c>bil.Delinquency.clockElapsed</c>.</summary>
internal sealed class ClockElapsedNonpayHandler(ICommandHandler<NonpayClockElapsed, int> elapsed) : IEventHandler<ClockElapsedV1>
{
    public const string Name = "BIL.ClockElapsed.Nonpay";

    public async Task HandleAsync(EventEnvelope envelope, ClockElapsedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(payload);
        if (!string.Equals(payload.ClockCode.Value, Domain.Nonpay.ClockCode, StringComparison.Ordinal)
            || !envelope.BusinessKeys.TryGetValue("clockInstanceId", out var value) || !Guid.TryParse(value, out var instance))
        {
            return;
        }

        ChargeDeltaEmittedHandler.Unwrap(await elapsed.HandleAsync(new NonpayClockElapsed(instance), cancellationToken).ConfigureAwait(false));
    }
}
