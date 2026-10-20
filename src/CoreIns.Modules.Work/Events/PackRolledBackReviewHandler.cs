using CoreIns.Modules.Market.Contracts.Events;
using CoreIns.Modules.Work.Commands;
using CoreIns.Modules.Work.Domain;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Work.Events;

/// <summary>
/// <c>mkt.PackRolledBack</c> → one review activity in the pack-rollback queue (D-SL5-10): a person reviews the transactions POL listed under
/// the rolled-back version. One activity per activation (the cause), so a redelivery adds nothing. The activity changes no policy data.
/// </summary>
internal sealed class PackRolledBackReviewHandler(ActivityEngine engine) : IEventHandler<PackRolledBackV1>
{
    public const string Name = "WRK.PackRolledBack.RaiseReview";

    public async Task HandleAsync(EventEnvelope envelope, PackRolledBackV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(payload);
        var created = await engine.CreateAsync(
            new CreateActivityInput(
                WorkCatalog.PackRollbackReview,
                new ObjectRef(ModuleCode.MKT, "PackActivation", payload.ActivationId.ToString("D")),
                [],
                ModuleCode.MKT,
                $"PackRolledBack:{payload.ActivationId:D}"),
            returnExistingOnDuplicate: true, cancellationToken).ConfigureAwait(false);
        if (created.IsFailure)
        {
            // Retried, then parked in the dead-letter table: a rollback must never pass without a review.
            throw new DomainException(created.Error!);
        }
    }
}
