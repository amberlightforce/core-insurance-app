using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Underwriting.Contracts.Events;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Policy.Events;

/// <summary>
/// <c>uw.DeclineIssued</c> → the job is Declined (REQ-POL-156, PRD-04 §15 "POL marks the job Declined"). Idempotent and
/// order-tolerant (D-ARC-26): a decline for a job that is no longer Draft or Quoted (bound, withdrawn, already declined)
/// changes nothing; a decline without a job (an intake decline) is not POL's.
/// </summary>
internal sealed class DeclineIssuedHandler(PolicyDbContext db, ILegalEntityDirectory legalEntities, IClock clock) : IEventHandler<DeclineIssuedV1>
{
    public const string Name = "POL.DeclineIssued.DeclineJob";

    public async Task HandleAsync(EventEnvelope envelope, DeclineIssuedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.JobId is not { } jobId)
        {
            return;
        }

        var legalEntity = legalEntities.Resolve(envelope.LegalEntity);
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.JobId == jobId && j.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return;
        }

        var declined = JobStateModel.Machine.Fire(Codes.Parse<JobState>(job.State), JobTrigger.Decline);
        if (declined.IsFailure)
        {
            return;
        }

        job.State = Codes.Of(declined.Value);
        job.DeclineId = payload.DeclineId;
        job.Referred = false;
        job.RecordVersion++;
        job.UpdatedAt = clock.Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
