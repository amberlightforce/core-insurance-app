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
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Policy.Commands.Quotes;

/// <summary><c>pol.Job.notTaken</c>: the customer did not take the quote; the Quoted job is closed with a reason.</summary>
internal sealed record NotTaken(JobNotTakenRequest Request) : ICommand<JobNotTakenResponse>;

internal sealed class NotTakenValidator : AbstractValidator<NotTaken>
{
    public NotTakenValidator()
    {
        RuleFor(c => c.Request.ReasonCode).NotEmpty().MaximumLength(128).WithErrorCode("REASON_REQUIRED");
        RuleFor(c => c.Request.Text).MaximumLength(1000);
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
    }
}

/// <summary>
/// Closes a Quoted job as NotTaken (REQ-POL-073, REQ-POL-155): the current version expires, the job is terminal, so it can never
/// be bound, and <c>JobNotTaken</c> goes through the outbox. Idempotent: a job that is already NotTaken answers success with
/// <c>replayed = true</c>, nothing written, no second event (the first reason code stays). Draft jobs are withdrawn instead
/// (<c>pol.Job.withdraw</c>); the state machine allows NotTaken from Quoted only.
/// </summary>
internal sealed class NotTakenHandler(PolicyDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IEventPublisher events)
    : ICommandHandler<NotTaken, JobNotTakenResponse>
{
    public async Task<Result<JobNotTakenResponse>> HandleAsync(NotTaken command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var now = clock.Now;
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.JobId == request.JobId && j.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return JobSupport.NotFound("job");
        }

        var refused = QuoteVersioning.RequireSubmission(job);
        if (refused is not null)
        {
            return refused;
        }

        if (Codes.Parse<JobState>(job.State) == JobState.NotTaken)
        {
            return new JobNotTakenResponse
            {
                JobId = job.JobId, State = JobStateCode.NotTaken, ReasonCode = job.ReasonCode ?? request.ReasonCode, RecordVersion = job.RecordVersion, Replayed = true,
            };
        }

        refused = QuoteVersioning.CheckRecordVersion(job, request.ExpectedRecordVersion);
        if (refused is not null)
        {
            return refused;
        }

        var fired = JobSupport.Fire(job, JobTrigger.NotTake);
        if (fired.IsFailure)
        {
            return fired.Error!;
        }

        var version = await db.QuoteVersions.SingleAsync(v => v.JobId == job.JobId && v.VersionNo == job.CurrentVersionNo, cancellationToken).ConfigureAwait(false);
        var expired = JobSupport.Fire(version, QuoteTrigger.Expire);
        if (expired.IsSuccess)
        {
            version.State = Codes.Of(expired.Value);
            version.RecordVersion++;
            version.UpdatedAt = now;
        }

        job.State = Codes.Of(fired.Value);
        job.ReasonCode = request.ReasonCode;
        JobSupport.AddParticipant(job, context.Actor);
        job.RecordVersion++;
        job.UpdatedAt = now;
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(JobNotTakenV1.Descriptor), "Policy", job.PolicyId.Value.ToString(),
            new JobNotTakenV1 { JobId = job.JobId, Reason = request.ReasonCode },
            BusinessKeys.Empty.With("policyId", job.PolicyId.Value.ToString()).With("jobId", job.JobId.Value.ToString())));

        if (await QuoteVersioning.SaveAsync(db, cancellationToken).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        return new JobNotTakenResponse
        {
            JobId = job.JobId, State = JobStateCode.NotTaken, ReasonCode = request.ReasonCode, RecordVersion = job.RecordVersion, Replayed = false,
        };
    }
}

/// <summary>Audit facts of <c>pol.Job.notTaken</c>: the job, the reason code and the free text.</summary>
internal sealed class NotTakenAuditor : ICommandAuditor<NotTaken, JobNotTakenResponse>
{
    public CommandAuditFacts Describe(NotTaken command, Result<JobNotTakenResponse>? result)
    {
        var objectRef = ObjectRef.For(ModuleCode.POL, "Job", command.Request.JobId);
        if (result is not { IsSuccess: true } success)
        {
            return new CommandAuditFacts { ObjectRef = objectRef };
        }

        var r = success.Value;
        return new CommandAuditFacts
        {
            ObjectRef = objectRef,
            BusinessKeys = BusinessKeys.Empty.With("jobId", r.JobId.Value.ToString()),
            Changes = AuditDiff.Compute(
                new { state = "QUOTED" },
                new { state = r.State.ToString(), reasonCode = r.ReasonCode, text = command.Request.Text, replayed = r.Replayed }),
        };
    }
}
