using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Platform.Events;

namespace CoreIns.Modules.Reinsurance.Intake;

// Outbox consumers of the CLM events RI needs (REQ-RI-113). Each only delegates to ClaimIntake.

/// <summary><c>clm.ClaimReported</c> → the claim view: policy, product and loss date.</summary>
internal sealed class ClaimReportedHandler(ClaimIntake intake) : IEventHandler<ClaimReportedV1>
{
    public const string Name = "RI.ClaimReported.ClaimView";

    public Task HandleAsync(EventEnvelope envelope, ClaimReportedV1 payload, CancellationToken cancellationToken) =>
        intake.OnClaimReportedAsync(envelope, payload, cancellationToken);
}

/// <summary><c>clm.ExposureCreated</c> → the exposure's coverage code.</summary>
internal sealed class ExposureCreatedHandler(ClaimIntake intake) : IEventHandler<ExposureCreatedV1>
{
    public const string Name = "RI.ExposureCreated.ClaimView";

    public Task HandleAsync(EventEnvelope envelope, ExposureCreatedV1 payload, CancellationToken cancellationToken) =>
        intake.OnExposureCreatedAsync(envelope, payload, cancellationToken);
}

/// <summary><c>clm.ReserveChanged</c> → the open reserve (RESERVE) or the anticipated recovery (RECOVERY_RESERVE).</summary>
internal sealed class ReserveChangedHandler(ClaimIntake intake) : IEventHandler<ReserveChangedV1>
{
    public const string Name = "RI.ReserveChanged.ClaimView";

    public Task HandleAsync(EventEnvelope envelope, ReserveChangedV1 payload, CancellationToken cancellationToken) =>
        intake.OnReserveChangedAsync(envelope, payload, cancellationToken);
}

/// <summary><c>clm.PaymentIssued</c> → paid amounts per exposure and cost type.</summary>
internal sealed class PaymentIssuedHandler(ClaimIntake intake) : IEventHandler<PaymentIssuedV1>
{
    public const string Name = "RI.PaymentIssued.ClaimView";

    public Task HandleAsync(EventEnvelope envelope, PaymentIssuedV1 payload, CancellationToken cancellationToken) =>
        intake.OnPaymentIssuedAsync(envelope, payload, cancellationToken);
}

/// <summary><c>clm.PaymentVoided</c> → reverses the paid amount.</summary>
internal sealed class PaymentVoidedHandler(ClaimIntake intake) : IEventHandler<PaymentVoidedV1>
{
    public const string Name = "RI.PaymentVoided.ClaimView";

    public Task HandleAsync(EventEnvelope envelope, PaymentVoidedV1 payload, CancellationToken cancellationToken) =>
        intake.OnPaymentVoidedAsync(envelope, payload, cancellationToken);
}

/// <summary><c>clm.RecoveryRecorded</c> → a realised recovery (the only recovery that inures, REQ-RI-117).</summary>
internal sealed class RecoveryRecordedHandler(ClaimIntake intake) : IEventHandler<RecoveryRecordedV1>
{
    public const string Name = "RI.RecoveryRecorded.ClaimView";

    public Task HandleAsync(EventEnvelope envelope, RecoveryRecordedV1 payload, CancellationToken cancellationToken) =>
        intake.OnRecoveryRecordedAsync(envelope, payload, cancellationToken);
}

/// <summary><c>clm.ClaimClosed</c> → open reserves released (REQ-RI-122).</summary>
internal sealed class ClaimClosedHandler(ClaimIntake intake) : IEventHandler<ClaimClosedV1>
{
    public const string Name = "RI.ClaimClosed.ClaimView";

    public Task HandleAsync(EventEnvelope envelope, ClaimClosedV1 payload, CancellationToken cancellationToken) =>
        intake.OnClaimClosedAsync(envelope, cancellationToken);
}

/// <summary><c>clm.ClaimReopened</c> → the claim is open again; reserves arrive as new <c>ReserveChanged</c> events.</summary>
internal sealed class ClaimReopenedHandler(ClaimIntake intake) : IEventHandler<ClaimReopenedV1>
{
    public const string Name = "RI.ClaimReopened.ClaimView";

    public Task HandleAsync(EventEnvelope envelope, ClaimReopenedV1 payload, CancellationToken cancellationToken) =>
        intake.OnClaimReopenedAsync(envelope, cancellationToken);
}
