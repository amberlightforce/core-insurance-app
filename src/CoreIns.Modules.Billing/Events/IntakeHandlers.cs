using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Compliance.Contracts.Events;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Billing.Events;

/// <summary>
/// <c>pol.ChargeDeltaEmitted</c> → <c>bil.Charge.intake</c> (REQ-BIL-002, REQ-BIL-364). Idempotent on the charge id
/// (the outbox may redeliver, and a dead letter may be replayed) and order-independent: deltas that arrive before
/// <c>PolicyBound</c> wait, the set is scheduled when complete whichever member arrives last (D-ARC-26).
/// </summary>
internal sealed class ChargeDeltaEmittedHandler(ICommandHandler<IntakeCharge, IntakeOutcome> intake) : IEventHandler<ChargeDeltaEmittedV1>
{
    public const string Name = "BIL.ChargeDeltaEmitted.Intake";

    public async Task HandleAsync(EventEnvelope envelope, ChargeDeltaEmittedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(payload);
        var policyId = envelope.BusinessKeys.TryGetValue("policyId", out var value) && Guid.TryParse(value, out var id)
            ? new PolicyId(id)
            : throw new InvalidOperationException("ChargeDeltaEmitted without the policyId business key (catalogue x-business-keys).");
        Unwrap(await intake.HandleAsync(
            new IntakeCharge(payload, policyId, new EventSource(envelope.EventId.Value, envelope.AggregateSequence, envelope.Set)), cancellationToken).ConfigureAwait(false));
    }

    internal static void Unwrap<T>(Result<T> result)
    {
        if (result.IsFailure)
        {
            throw new DomainException(result.Error!);
        }
    }
}

/// <summary><c>pol.PolicyBound</c> → <c>bil.BillingAccount.attachTerm</c> (REQ-BIL-031…033, REQ-BIL-052). Idempotent on the term id.</summary>
internal sealed class PolicyBoundHandler(ICommandHandler<AttachTerm, IntakeOutcome> attach) : IEventHandler<PolicyBoundV1>
{
    public const string Name = "BIL.PolicyBound.AttachTerm";

    public async Task HandleAsync(EventEnvelope envelope, PolicyBoundV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(payload);
        ChargeDeltaEmittedHandler.Unwrap(await attach.HandleAsync(
            new AttachTerm(payload, new EventSource(envelope.EventId.Value, envelope.AggregateSequence, envelope.Set)), cancellationToken).ConfigureAwait(false));
    }
}

/// <summary><c>cmp.FiscalDocRegistered</c> → store the MARK on the invoice (REQ-BIL-098).</summary>
internal sealed class FiscalDocRegisteredHandler(ICommandHandler<RecordFiscalOutcome, int> record) : IEventHandler<FiscalDocRegisteredV1>
{
    public const string Name = "BIL.FiscalDocRegistered.StoreMark";

    public async Task HandleAsync(EventEnvelope envelope, FiscalDocRegisteredV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(payload);
        if (FiscalDocumentIdOf(envelope) is not { } id)
        {
            return;
        }

        ChargeDeltaEmittedHandler.Unwrap(await record.HandleAsync(new RecordFiscalOutcome(id, true, payload.Mark, payload.Uid, []), cancellationToken).ConfigureAwait(false));
    }

    internal static FiscalDocumentId? FiscalDocumentIdOf(EventEnvelope envelope) =>
        envelope.BusinessKeys.TryGetValue("fiscalDocumentId", out var value) && Guid.TryParse(value, out var id) ? new FiscalDocumentId(id) : null;
}

/// <summary><c>cmp.FiscalDocRejected</c> → flag the invoice and raise <c>CMP-FISCAL-REJECTED</c> (REQ-BIL-098).</summary>
internal sealed class FiscalDocRejectedHandler(ICommandHandler<RecordFiscalOutcome, int> record) : IEventHandler<FiscalDocRejectedV1>
{
    public const string Name = "BIL.FiscalDocRejected.FlagInvoice";

    public async Task HandleAsync(EventEnvelope envelope, FiscalDocRejectedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(payload);
        if (FiscalDocRegisteredHandler.FiscalDocumentIdOf(envelope) is not { } id)
        {
            return;
        }

        ChargeDeltaEmittedHandler.Unwrap(await record.HandleAsync(new RecordFiscalOutcome(id, false, null, null, payload.Codes), cancellationToken).ConfigureAwait(false));
    }
}
