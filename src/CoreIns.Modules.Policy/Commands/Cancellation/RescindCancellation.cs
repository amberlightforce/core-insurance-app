using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Policy.Commands.Cancellation;

/// <summary><c>pol.Cancellation.rescind</c> (BO-POL-06): withdraw a scheduled cancellation that has not run yet.</summary>
internal sealed record RescindCancellation(CancellationRescindRequest Request) : ICommand<CancellationRescindResponse>;

internal sealed class RescindCancellationValidator : AbstractValidator<RescindCancellation>
{
    public RescindCancellationValidator()
    {
        RuleFor(c => c.Request.PolicyId.Value).NotEmpty().WithErrorCode("POLICY_REQUIRED");
        RuleFor(c => c.Request.JobId.Value).NotEmpty().WithErrorCode("JOB_REQUIRED");
        RuleFor(c => c.Request.ReasonCode).NotEmpty().MaximumLength(64).WithErrorCode("REASON_REQUIRED");
    }
}

/// <summary>
/// Rescinds a SCHEDULED cancellation strictly before its effective time, whatever the state of the policy or term (suspended, in force,
/// pending). The row's record version is a concurrency token, so a rescind racing the sweep loses cleanly: exactly one of them wins.
/// </summary>
internal sealed class RescindCancellationHandler(
    PolicyDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IEventPublisher events)
    : ICommandHandler<RescindCancellation, CancellationRescindResponse>
{
    public async Task<Result<CancellationRescindResponse>> HandleAsync(RescindCancellation command, CancellationToken cancellationToken)
    {
        var r = command.Request;
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var scheduledId = r.JobId;
        var policyId = r.PolicyId;
        var row = await db.ScheduledCancellations
            .SingleOrDefaultAsync(x => x.ScheduledId == scheduledId && x.PolicyId == policyId && x.LegalEntityId == legalEntity, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return JobSupport.NotFound("scheduled cancellation");
        }

        if (row.Status != ScheduledCancellationStatus.Scheduled)
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", $"The cancellation is {row.Status}; only a scheduled one can be rescinded.");
        }

        var now = clock.Now;
        if (row.EffectiveAt <= now)
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The effective time of the cancellation has been reached; it can no longer be rescinded.");
        }

        row.Status = ScheduledCancellationStatus.Rescinded;
        row.Outcome = r.ReasonCode;
        row.UpdatedAt = now;
        row.RecordVersion++;
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(CancellationRescindedV1.Descriptor), "Policy", row.PolicyId.Value.ToString(),
            new CancellationRescindedV1 { JobId = row.ScheduledId, TermId = row.TermId, Reason = r.ReasonCode },
            BusinessKeys.Empty.With("policyId", row.PolicyId.Value.ToString()).With("policyTermId", row.TermId.Value.ToString()).With("jobId", row.ScheduledId.Value.ToString()))
        {
            OccurredAt = now,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return JobSupport.Stale();
        }

        return new CancellationRescindResponse { JobId = row.ScheduledId, State = ScheduledCancellationStatus.Rescinded };
    }
}

/// <summary>Audit facts of <c>pol.Cancellation.rescind</c>.</summary>
internal sealed class RescindCancellationAuditor : ICommandAuditor<RescindCancellation, CancellationRescindResponse>
{
    public CommandAuditFacts Describe(RescindCancellation command, Result<CancellationRescindResponse>? result)
    {
        var facts = new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.POL, "Policy", command.Request.PolicyId) };
        return result is { IsSuccess: true }
            ? facts with
            {
                BusinessKeys = BusinessKeys.Empty.With("policyId", command.Request.PolicyId.Value.ToString()).With("jobId", command.Request.JobId.Value.ToString()),
                Changes = AuditDiff.Compute(null, new { reasonCode = command.Request.ReasonCode, status = ScheduledCancellationStatus.Rescinded }),
            }
            : facts;
    }
}
