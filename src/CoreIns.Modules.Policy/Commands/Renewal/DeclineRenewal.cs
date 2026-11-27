using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Policy.Services;
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
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Policy.Commands.Renewal;

/// <summary><c>pol.Renewal.decline</c>: the customer (or staff for them) declines the renewal of a term.</summary>
internal sealed record DeclineRenewal(RenewalDeclineRequest Request) : ICommand<RenewalDeclineResponse>;

internal sealed class DeclineRenewalValidator : AbstractValidator<DeclineRenewal>
{
    public DeclineRenewalValidator()
    {
        RuleFor(c => c.Request.JobId.Value).NotEmpty().WithErrorCode("JOB_REQUIRED");
        RuleFor(c => c.Request.TermId.Value).NotEmpty().WithErrorCode("TERM_REQUIRED");
        RuleFor(c => c.Request.ReasonCode).NotEmpty().MaximumLength(64).WithErrorCode("REASON_REQUIRED");
    }
}

/// <summary>
/// Declines the renewal job of a term (REQ-POL-009, REQ-POL-257), under the policy lock. An offered (Quoted) renewal becomes NotTaken, a
/// draft one Withdrawn; a renewal that was already accepted (term n+1 bound) or closed is refused. The expiring term is untouched: it
/// simply expires. Emits <c>PolicyNonRenewed</c> (source CUSTOMER_DECLINE). Dry-run computes the same and rolls back.
/// </summary>
internal sealed class DeclineRenewalHandler(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    IEventPublisher events,
    IOptions<PolicyOptions> options) : ICommandHandler<DeclineRenewal, RenewalDeclineResponse>
{
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);

    public async Task<Result<RenewalDeclineResponse>> HandleAsync(DeclineRenewal command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        if (await RenewalSupport.PolicyOfAsync(db, legalEntity, request.TermId, cancellationToken).ConfigureAwait(false) is not { } policyId)
        {
            return JobSupport.NotFound("term");
        }

        var locked = await PolicyWriteLock.AcquireAsync(db, clock, options.Value.LockWait, legalEntity, policyId, cancellationToken).ConfigureAwait(false);
        if (locked.IsFailure)
        {
            return locked.Error!;
        }

        var now = locked.Value;
        if (request.DeclinedAt > now.Plus(Skew))
        {
            return RenewalSupport.Validation("declinedAt", "DECLINED_IN_FUTURE", "The decline cannot be dated after the time it is recorded.");
        }

        var job = await RenewalSupport.RenewalJobAsync(db, legalEntity, request.JobId, request.TermId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return JobSupport.NotFound("renewal job of the term");
        }

        var state = Codes.Parse<JobState>(job.State);
        var trigger = state switch
        {
            JobState.Quoted when job.SubState == Codes.Of(JobSubState.Accepted) => (JobTrigger?)null,
            JobState.Quoted => JobTrigger.NotTake,
            JobState.Draft => JobTrigger.Withdraw,
            _ => null,
        };
        if (trigger is null)
        {
            return DomainError.Of(
                ModuleCode.POL, "ILLEGAL-TRANSITION",
                state == JobState.Bound ? "The renewal was already accepted; the next term exists." : $"A {state} renewal cannot be declined.");
        }

        var term = await RenewalSupport.CurrentTermAsync(db, legalEntity, request.TermId, cancellationToken).ConfigureAwait(false);
        if (term is null)
        {
            return JobSupport.NotFound("term");
        }

        if (await db.Terms.AnyAsync(t => t.PredecessorTermId == request.TermId && t.RecordedTo == null, cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The term has already been renewed.");
        }

        var fired = JobSupport.Fire(job, trigger.Value);
        if (fired.IsFailure)
        {
            return fired.Error!;
        }

        var actor = context.Actor.ToString();
        job.State = Codes.Of(fired.Value);
        job.ReasonCode = request.ReasonCode;
        job.RecordVersion++;
        job.UpdatedAt = now;
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PolicyNonRenewedV1.Descriptor), "Policy", policyId.Value.ToString(),
            new PolicyNonRenewedV1 { TermId = request.TermId, Source = "CUSTOMER_DECLINE", Reason = request.ReasonCode },
            BusinessKeys.Empty.With("policyId", policyId.Value.ToString()).With("policyTermId", request.TermId.Value.ToString())
                .With("jobId", job.JobId.Value.ToString()).With("declinedBy", actor))
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

        return new RenewalDeclineResponse
        {
            JobId = job.JobId, TermId = request.TermId, State = Codes.Api(fired.Value), ReasonCode = request.ReasonCode, RecordedAt = now,
        };
    }
}

/// <summary>Audit facts of <c>pol.Renewal.decline</c>.</summary>
internal sealed class DeclineRenewalAuditor : ICommandAuditor<DeclineRenewal, RenewalDeclineResponse>
{
    public CommandAuditFacts Describe(DeclineRenewal command, Result<RenewalDeclineResponse>? result)
    {
        var facts = new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.POL, "Job", command.Request.JobId) };
        return result is { IsSuccess: true } success
            ? facts with
            {
                BusinessKeys = BusinessKeys.Empty.With("jobId", success.Value.JobId.Value.ToString()).With("termId", command.Request.TermId.Value.ToString()),
                Changes = AuditDiff.Compute(null, new { state = success.Value.State.ToString(), reason = command.Request.ReasonCode, evidence = command.Request.Evidence }),
            }
            : facts;
    }
}
