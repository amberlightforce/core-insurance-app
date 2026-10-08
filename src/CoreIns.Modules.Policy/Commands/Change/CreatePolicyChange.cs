using System.Globalization;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Policy.Commands.Change;

/// <summary><c>pol.PolicyChange.create</c>: starts a mid-term change job (Draft) on the term valid at the effective date.</summary>
internal sealed record CreatePolicyChange(PolicyChangeCreateRequest Request) : ICommand<PolicyChangeCreateResponse>;

internal sealed class CreatePolicyChangeValidator : AbstractValidator<CreatePolicyChange>
{
    public CreatePolicyChangeValidator() => RuleFor(c => c.Request.PolicyId.Value).NotEmpty().WithErrorCode("POLICY_REQUIRED");
}

/// <summary>
/// Starts a change (REQ-POL-190, -008, -135, -136, -104): the effective date defaults to now (for a Scheduled term, to its start)
/// and must lie inside the caller's limits (0 days back for a CSR, 30 for <c>Staff.Underwriter</c>, PRD-05 §10.2 defaults, illustrative;
/// the refusal carries the permitted range). The job is created on the term valid at that date, with <c>base_transaction_id</c> = the
/// term's head and the term's pinned product and rating artefacts (REQ-POL-093); version 1 of the quote is the risk as it stands at
/// the effective date. Refused: a term that is not Scheduled or InForce (ILLEGAL-TRANSITION), a cancelled cover (G1,
/// AFTER-CANCELLATION), an effective time earlier than the latest bound transaction (OUT-OF-SEQUENCE, D-SL3-02) and a second open
/// change on the term (JOB-CONFLICT, 409). Creating a job records no policy row, so it takes no policy lock; the bind re-checks
/// everything under it.
/// </summary>
internal sealed class CreatePolicyChangeHandler(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    INumberingService numbering,
    ChangeHistory history,
    IOptions<PolicyOptions> options,
    IOptions<ChangeOptions> changeOptions) : ICommandHandler<CreatePolicyChange, PolicyChangeCreateResponse>
{
    public async Task<Result<PolicyChangeCreateResponse>> HandleAsync(CreatePolicyChange command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var now = clock.Now;
        var zone = options.Value.Zone;
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var policyId = request.PolicyId;

        var policy = await db.Policies.AsNoTracking().SingleOrDefaultAsync(p => p.PolicyId == policyId && p.LegalEntityId == legalEntity, cancellationToken)
            .ConfigureAwait(false);
        if (policy is null)
        {
            return JobSupport.NotFound("policy");
        }

        var effectiveAt = PolicyWriteLock.Truncate(request.EffectiveAt);
        var terms = await db.Terms.AsNoTracking().Where(t => t.PolicyId == policyId && t.LegalEntityId == legalEntity && t.RecordedTo == null)
            .OrderBy(t => t.TermNumber).ToListAsync(cancellationToken).ConfigureAwait(false);
        var term = terms.FirstOrDefault(t => t.ValidFrom <= effectiveAt && effectiveAt < t.ValidTo);
        if (term is null)
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "No term of the policy is in force or scheduled at the effective date.");
        }

        var loaded = await history.LoadAsync(legalEntity, term.TermId, false, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var state = ChangeHistory.GuardState(loaded.Value, effectiveAt);
        if (state is not null)
        {
            return state;
        }

        var (earliest, latest) = EffectiveRange(term, now, zone);
        if (effectiveAt < earliest || effectiveAt > latest)
        {
            return new DomainError(ErrorCode.For(ModuleCode.POL, PolicyErrorNames.EffdateLimit), "The effective date is outside the range this role may change (REQ-POL-136).")
            {
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["earliest"] = earliest.ToString(), ["latest"] = latest.ToString() },
            };
        }

        var sequence = ChangeHistory.GuardSequence(loaded.Value, effectiveAt);
        if (sequence is not null)
        {
            return sequence;
        }

        if (await db.Jobs.AnyAsync(
                j => j.TargetTermId == term.TermId && j.JobType == ChangeNames.JobType && (j.State == "DRAFT" || j.State == "QUOTED" || j.State == "SCHEDULED"), cancellationToken)
            .ConfigureAwait(false))
        {
            return Conflict();
        }

        var segment = await db.Segments.AsNoTracking().SingleOrDefaultAsync(
            s => s.TermId == term.TermId && s.RecordedTo == null && s.ValidFrom <= effectiveAt && effectiveAt < s.ValidTo, cancellationToken).ConfigureAwait(false);
        if (segment is null)
        {
            return DomainError.Of(ModuleCode.POL, "SEGMENT-INVARIANT", "The term has no current segment at the effective date.");
        }

        var jobId = JobId.New();
        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.Job, now.ToBusinessDate(zone)), cancellationToken).ConfigureAwait(false);
        var actor = context.Actor.ToString();
        var job = new JobRow
        {
            JobId = jobId,
            LegalEntityId = legalEntity,
            Jurisdiction = policy.Jurisdiction,
            JobNumber = JobNumber.Parse(number.Value),
            JobType = ChangeNames.JobType,
            State = Codes.Of(JobStateModel.Machine.Start(JobState.Draft).Value),
            PolicyId = policyId,
            PolicyholderPartyId = policy.PolicyholderPartyId,
            AccountId = policy.AccountId,
            ProductCode = policy.ProductCode,
            ProductVersion = term.ProductVersion,
            ArtefactHash = term.ArtefactHash,
            RatingArtefactHash = term.RatingArtefactHash,
            ResolutionHash = term.ResolutionHash,
            Channel = context.Channel ?? "STAFF",
            ProducerCode = term.ProducerCode,
            QuoteType = "FULL",
            EffectiveAt = effectiveAt,
            ExpirationAt = term.ValidTo,
            Currency = term.Currency,
            CurrentVersionNo = 1,
            RecordVersion = 1,
            CreatedAt = now,
            CreatedBy = actor,
            Participants = [actor],
            UpdatedAt = now,
            TargetTermId = term.TermId,
            BaseTransactionId = term.HeadTransactionId,
        };
        db.Jobs.Add(job);
        db.QuoteVersions.Add(new QuoteVersionRow
        {
            QuoteId = QuoteId.New(),
            JobId = jobId,
            LegalEntityId = legalEntity,
            VersionNo = 1,
            State = Codes.Of(QuoteStateModel.Machine.Start(QuoteState.Draft).Value),
            DraftVersion = 0,
            RiskTree = segment.Snapshot,
            RecordVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two creates raced past the check: the partial unique index ux_job_open_servicing let one through. The loser is "changed meanwhile".
            return JobSupport.Stale();
        }

        return new PolicyChangeCreateResponse
        {
            JobId = jobId, State = JobStateCode.Draft, TermId = term.TermId, BaseTransactionId = term.HeadTransactionId.Value,
        };
    }

    /// <summary>The effective-date range of the caller (REQ-POL-135, -136): from the start of the Athens day N days back, to the end of the term.</summary>
    private (Instant Earliest, Instant Latest) EffectiveRange(PolicyTermRow term, Instant now, TimeZoneInfo zone)
    {
        var settings = changeOptions.Value;
        var days = context.Roles.Where(settings.BackdateDaysByRole.ContainsKey).Select(r => settings.BackdateDaysByRole[r]).DefaultIfEmpty(settings.BackdateDaysDefault).Max();
        var earliest = Instant.Max(now.ToBusinessDate(zone).AddDays(-days).StartOfDayIn(zone), term.ValidFrom);
        return (earliest, term.ValidTo.Minus(TimeSpan.FromTicks(10)));
    }

    private static DomainError Conflict() =>
        DomainError.Of(ModuleCode.POL, "JOB-CONFLICT", "The term already has an open change; finish or withdraw it first (D-SL3-11).");
}

/// <summary>Audit facts of <c>pol.PolicyChange.create</c>: the job and the policy (no personal data).</summary>
internal sealed class CreatePolicyChangeAuditor : ICommandAuditor<CreatePolicyChange, PolicyChangeCreateResponse>
{
    public CommandAuditFacts Describe(CreatePolicyChange command, Result<PolicyChangeCreateResponse>? result)
    {
        var policy = command.Request.PolicyId;
        if (result is not { IsSuccess: true } success)
        {
            return new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.POL, "Policy", policy) };
        }

        var jobId = success.Value.JobId;

        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.POL, "Job", jobId),
            BusinessKeys = BusinessKeys.Empty.With("jobId", jobId.Value.ToString()).With("policyId", policy.Value.ToString()),
            Changes = AuditDiff.Compute(null, new { effectiveAt = command.Request.EffectiveAt.ToString(), kind = "POLICY_CHANGE" }),
        };
    }
}
