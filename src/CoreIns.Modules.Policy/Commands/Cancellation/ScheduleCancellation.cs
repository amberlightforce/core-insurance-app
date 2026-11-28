using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Policy.Domain;
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
using Microsoft.Extensions.Options;
using Npgsql;
using CancellationKind = CoreIns.Modules.Policy.Contracts.Api.CancellationKind;

namespace CoreIns.Modules.Policy.Commands.Cancellation;

/// <summary>Statuses of <c>pol.scheduled_cancellation</c>.</summary>
internal static class ScheduledCancellationStatus
{
    public const string Scheduled = "SCHEDULED";

    public const string Executed = "EXECUTED";

    public const string Rescinded = "RESCINDED";

    public const string Failed = "FAILED";
}

/// <summary><c>pol.Cancellation.schedule</c> (BO-POL-06): a cancellation at a future effective time.</summary>
internal sealed record ScheduleCancellation(CancellationScheduleRequest Request) : ICommand<CancellationScheduleResponse>;

internal sealed class ScheduleCancellationValidator : AbstractValidator<ScheduleCancellation>
{
    public ScheduleCancellationValidator()
    {
        RuleFor(c => c.Request.PolicyId.Value).NotEmpty().WithErrorCode("POLICY_REQUIRED");
        RuleFor(c => c.Request.Source).NotEmpty().MaximumLength(64).WithErrorCode("SOURCE_REQUIRED");
        RuleFor(c => c.Request.ReasonCode).NotEmpty().MaximumLength(64).WithErrorCode("REASON_REQUIRED");
    }
}

/// <summary>
/// Schedules the cancellation of the term in force at the effective time. The refund is quoted now by the cancellation machinery itself
/// (<see cref="CancelPolicyHandler"/> with <see cref="ScheduledCase.PreviewOnly"/>: the same servicing engine, nothing written) and is computed
/// again when the sweep runs it. One open scheduled cancellation per term. With <c>?dryRun=true</c> the pipeline rolls back, so only the preview survives.
/// </summary>
internal sealed class ScheduleCancellationHandler(
    PolicyDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IEventPublisher events,
    CancelPolicyHandler cancel, ICancellationRefundMethods refundMethods, IOptions<PolicyOptions> options)
    : ICommandHandler<ScheduleCancellation, CancellationScheduleResponse>
{
    public async Task<Result<CancellationScheduleResponse>> HandleAsync(ScheduleCancellation command, CancellationToken cancellationToken)
    {
        var r = command.Request;
        var policyId = r.PolicyId;
        var effective = r.EffectiveAt;
        var now = PolicyWriteLock.Truncate(clock.Now);
        if (effective <= now)
        {
            return DomainError.Of(ModuleCode.POL, PolicyErrorNames.EffdateLimit, "A scheduled cancellation needs a future effective time; to cancel now use pol.Cancellation.create.");
        }

        if (r.Source != CancellationSources.Policyholder || refundMethods.Resolve(r.Source) is not { } method)
        {
            return new DomainError(ErrorCode.For(ModuleCode.POL, PolicyErrorNames.Validation), $"Cancellation by source {r.Source} cannot be scheduled in this release.")
            {
                FieldErrors = [new FieldError("source", "SOURCE_NOT_AVAILABLE", "SOURCE_NOT_AVAILABLE")],
            };
        }

        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var terms = await db.Terms.AsNoTracking()
            .Where(x => x.PolicyId == policyId && x.LegalEntityId == legalEntity && x.RecordedTo == null)
            .Select(x => new { x.TermId, x.ValidFrom, x.ValidTo }).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (terms.Count == 0)
        {
            return JobSupport.NotFound("policy");
        }

        var term = terms.FirstOrDefault(x => x.ValidFrom <= effective && effective < x.ValidTo);
        if (term is null)
        {
            return DomainError.Of(ModuleCode.POL, PolicyErrorNames.EffdateLimit, "No term of the policy covers the scheduled effective time.");
        }

        if (await db.ScheduledCancellations.AsNoTracking().AnyAsync(x => x.TermId == term.TermId && x.Status == ScheduledCancellationStatus.Scheduled, cancellationToken).ConfigureAwait(false))
        {
            return Conflict();
        }

        // The quote: the cancellation machinery up to, not including, its writes.
        var quote = await cancel.HandleAsync(
            new CancelPolicy(
                new CancellationCreateRequest
                {
                    PolicyId = policyId, Source = r.Source, ReasonCode = r.ReasonCode, EffectiveAt = effective, Kind = CancellationKind.Standard,
                    EvidenceRefs = r.EvidenceRefs, RequestRef = r.RequestRef,
                },
                Scheduled: new ScheduledCase(term.TermId, effective, PreviewOnly: true)),
            cancellationToken).ConfigureAwait(false);
        if (quote.IsFailure)
        {
            return quote.Error!;
        }

        var id = JobId.New();
        var t = clock.Now;
        var refundMethod = quote.Value.ServicingPreview.RefundMethod ?? CancellationText.Code(method);
        db.ScheduledCancellations.Add(new ScheduledCancellationRow
        {
            ScheduledId = id, LegalEntityId = legalEntity, PolicyId = policyId, TermId = term.TermId, Source = r.Source, ReasonCode = r.ReasonCode,
            EffectiveAt = effective, RefundMethod = refundMethod,
            RequestRef = r.RequestRef, EvidenceRefs = r.EvidenceRefs is { Count: > 0 } ? JsonSerializer.Serialize(r.EvidenceRefs) : null,
            CreatedBy = context.Actor.ToString(), CreatedAt = t, UpdatedAt = t,
        });
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(CancellationScheduledV1.Descriptor), "Policy", policyId.Value.ToString(),
            new CancellationScheduledV1
            {
                JobId = id, TermId = term.TermId, Source = r.Source, Reason = r.ReasonCode,
                EffectiveDate = effective.ToBusinessDate(options.Value.Zone), RefundMethod = refundMethod,
            },
            BusinessKeys.Empty.With("policyId", policyId.Value.ToString()).With("policyTermId", term.TermId.Value.ToString()).With("jobId", id.Value.ToString()))
        {
            OccurredAt = t,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Conflict();
        }

        return new CancellationScheduleResponse { JobId = id, State = ScheduledCancellationStatus.Scheduled, EffectiveAt = effective, ServicingPreview = quote.Value.ServicingPreview };
    }

    private static DomainError Conflict() =>
        DomainError.Of(ModuleCode.POL, "JOB-CONFLICT", "The term already has a scheduled cancellation; rescind it first.");
}

/// <summary>Audit facts of <c>pol.Cancellation.schedule</c>.</summary>
internal sealed class ScheduleCancellationAuditor : ICommandAuditor<ScheduleCancellation, CancellationScheduleResponse>
{
    public CommandAuditFacts Describe(ScheduleCancellation command, Result<CancellationScheduleResponse>? result)
    {
        var facts = new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.POL, "Policy", command.Request.PolicyId) };
        return result is { IsSuccess: true } success
            ? facts with
            {
                BusinessKeys = BusinessKeys.Empty.With("policyId", command.Request.PolicyId.Value.ToString()).With("jobId", success.Value.JobId.Value.ToString()),
                Changes = AuditDiff.Compute(null, new
                {
                    source = command.Request.Source, reasonCode = command.Request.ReasonCode, effectiveAt = success.Value.EffectiveAt.ToString(),
                    refundDue = success.Value.ServicingPreview.RefundDue.Amount.ToString(CultureInfo.InvariantCulture),
                }),
            }
            : facts;
    }
}
