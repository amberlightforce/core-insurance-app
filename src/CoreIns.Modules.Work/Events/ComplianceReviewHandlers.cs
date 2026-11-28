using CoreIns.Modules.Compliance.Contracts.Events;
using CoreIns.Modules.Work.Commands;
using CoreIns.Modules.Work.Domain;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Work.Events;

/// <summary>
/// <c>cmp.ComplaintEscalated</c> → one follow-up activity in the compliance operations queue (BO-WRK-04). One open activity per complaint,
/// so a redelivery or a second escalation event adds nothing while the first is open.
/// </summary>
internal sealed class ComplaintEscalatedHandler(ActivityEngine engine) : IEventHandler<ComplaintEscalatedV1>
{
    public const string Name = "WRK.ComplaintEscalated.RaiseFollowUp";

    public async Task HandleAsync(EventEnvelope envelope, ComplaintEscalatedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(payload);
        var created = await engine.CreateAsync(
            new CreateActivityInput(
                WorkCatalog.CmpComplaintEscalated,
                new ObjectRef(ModuleCode.CMP, "Complaint", payload.ComplaintId.Value.ToString("D")),
                [],
                ModuleCode.CMP,
                "ComplaintEscalated"),
            returnExistingOnDuplicate: true, cancellationToken).ConfigureAwait(false);
        if (created.IsFailure)
        {
            // Retried, then parked in the dead-letter table: an escalation must never pass without a follow-up.
            throw new DomainException(created.Error!);
        }
    }
}

/// <summary>
/// <c>cmp.FiscalDocRejected</c> → one activity in the finance operations queue (BO-WRK-04). One open activity per fiscal document and
/// source; the event carries only the source, which goes into the cause.
/// </summary>
internal sealed class FiscalDocRejectedHandler(ActivityEngine engine) : IEventHandler<FiscalDocRejectedV1>
{
    public const string Name = "WRK.FiscalDocRejected.RaiseReview";

    public async Task HandleAsync(EventEnvelope envelope, FiscalDocRejectedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(payload);
        var cause = $"FiscalDocRejected:{payload.SourceType}:{payload.SourceId}";
        var created = await engine.CreateAsync(
            new CreateActivityInput(
                WorkCatalog.CmpFiscalRejected,
                new ObjectRef(ModuleCode.CMP, "FiscalDocument", envelope.AggregateId),
                [],
                ModuleCode.CMP,
                cause.Length <= 200 ? cause : "FiscalDocRejected"),
            returnExistingOnDuplicate: true, cancellationToken).ConfigureAwait(false);
        if (created.IsFailure)
        {
            throw new DomainException(created.Error!);
        }
    }
}
