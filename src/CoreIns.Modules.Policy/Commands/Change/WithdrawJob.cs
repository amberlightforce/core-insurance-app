using System.Text.Json;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Policy.Commands.Change;

/// <summary><c>pol.Job.withdraw</c>: withdraws a Draft or Quoted job (Draft/Quoted → Withdrawn), freeing the term's one open change slot.</summary>
internal sealed record WithdrawJob(JobWithdrawRequest Request) : ICommand<JobWithdrawResponse>;

internal sealed class WithdrawJobValidator : AbstractValidator<WithdrawJob>
{
    public WithdrawJobValidator() => RuleFor(c => c.Request.JobId).NotNull().WithErrorCode("JOB_REQUIRED");
}

/// <summary>
/// Withdraws a job through the existing job state machine (an illegal state is POL-ERR-ILLEGAL-TRANSITION). It writes only the
/// working job row, which carries no record-time stamp, so it takes no policy lock; a racing bind of the same job loses or wins by
/// the job's record version (POL-ERR-STALE), never a 500. <c>JobWithdrawn</c> goes through the outbox with the job type and a reason code.
/// </summary>
internal sealed class WithdrawJobHandler(PolicyDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IEventPublisher events)
    : ICommandHandler<WithdrawJob, JobWithdrawResponse>
{
    public async Task<Result<JobWithdrawResponse>> HandleAsync(WithdrawJob command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var now = clock.Now;
        var jobId = request.JobId!.Value;
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.JobId == jobId && j.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return JobSupport.NotFound("job");
        }

        var withdrawn = JobSupport.Fire(job, JobTrigger.Withdraw);
        if (withdrawn.IsFailure)
        {
            return withdrawn.Error!;
        }

        job.State = Codes.Of(withdrawn.Value);
        JobSupport.AddParticipant(job, context.Actor);
        job.RecordVersion++;
        job.UpdatedAt = now;
        var reason = request.ReasonCode is { ValueKind: JsonValueKind.String } code ? code.GetString()! : "WITHDRAWN";
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(JobWithdrawnV1.Descriptor), "Policy", job.PolicyId.Value.ToString(),
            new JobWithdrawnV1 { JobId = job.JobId, JobType = job.JobType, Reason = reason },
            BusinessKeys.Empty.With("policyId", job.PolicyId.Value.ToString()).With("jobId", job.JobId.Value.ToString())));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return JobSupport.Stale();
        }

        return new JobWithdrawResponse { Job = JsonSerializer.SerializeToElement(new { jobId = job.JobId.Value, state = job.State }) };
    }
}

/// <summary>Audit facts of <c>pol.Job.withdraw</c>: the job only.</summary>
internal sealed class WithdrawJobAuditor : ICommandAuditor<WithdrawJob, JobWithdrawResponse>
{
    public CommandAuditFacts Describe(WithdrawJob command, Result<JobWithdrawResponse>? result) =>
        command.Request.JobId is { } id
            ? new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.POL, "Job", id), BusinessKeys = BusinessKeys.Empty.With("jobId", id.Value.ToString()) }
            : new CommandAuditFacts();
}
