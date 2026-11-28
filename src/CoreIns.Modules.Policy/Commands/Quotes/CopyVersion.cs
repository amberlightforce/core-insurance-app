using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Policy.Commands.Quotes;

/// <summary><c>pol.Job.copyVersion</c>: copies a quote version into a new version of the same job or into a new job.</summary>
internal sealed record CopyVersion(JobCopyVersionRequest Request) : ICommand<JobCopyVersionResponse>;

internal sealed class CopyVersionValidator : AbstractValidator<CopyVersion>
{
    public CopyVersionValidator()
    {
        RuleFor(c => c.Request.SourceVersion).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.Target).IsInEnum();
    }
}

/// <summary>
/// <c>NEW_VERSION</c>: any version of a Quoted job is copied into a new Draft version (restoring an older version); the current
/// version is superseded, nothing existing changes. <c>NEW_JOB</c>: a new Draft submission with a copy of the version risk data and
/// the source's product resolution (artefacts, manifest); the effective date moves to now when it lies in the past (REQ-POL-137).
/// The source job is only read (the record version is still checked). The copy never carries prices, issues or worksheets.
/// TODO(hardening): re-resolve the product version and rating artefact for a copy into a new job instead of taking the source's.
/// </summary>
internal sealed class CopyVersionHandler(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    INumberingService numbering,
    IEventPublisher events,
    IOptions<PolicyOptions> options) : ICommandHandler<CopyVersion, JobCopyVersionResponse>
{
    public async Task<Result<JobCopyVersionResponse>> HandleAsync(CopyVersion command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var now = clock.Now;
        var loaded = await JobSupport.LoadAsync(db, JobSupport.LegalEntity(context, legalEntities), request.JobId, request.SourceVersion, cancellationToken)
            .ConfigureAwait(false);
        if (loaded is not var (job, source))
        {
            return JobSupport.NotFound("job or quote version");
        }

        var refused = QuoteVersioning.RequireSubmission(job) ?? QuoteVersioning.CheckRecordVersion(job, request.ExpectedRecordVersion);
        if (refused is not null)
        {
            return refused;
        }

        return request.Target == JobCopyVersionRequest.TargetValue.NewVersion
            ? await IntoNewVersionAsync(job, source, now, cancellationToken).ConfigureAwait(false)
            : await IntoNewJobAsync(job, source, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<JobCopyVersionResponse>> IntoNewVersionAsync(JobRow job, QuoteVersionRow source, Instant now, CancellationToken cancellationToken)
    {
        var current = source.VersionNo == job.CurrentVersionNo
            ? source
            : await db.QuoteVersions.SingleAsync(v => v.JobId == job.JobId && v.VersionNo == job.CurrentVersionNo, cancellationToken).ConfigureAwait(false);
        var created = QuoteVersioning.Branch(db, job, current, source, context, now);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        if (await QuoteVersioning.SaveAsync(db, cancellationToken).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        var version = created.Value;
        return new JobCopyVersionResponse
        {
            JobId = job.JobId,
            JobNumber = job.JobNumber,
            VersionNo = version.VersionNo,
            QuoteId = version.QuoteId,
            DraftVersion = version.DraftVersion,
            State = Codes.Api(Codes.Parse<JobState>(job.State)),
            RecordVersion = job.RecordVersion,
            SupersededVersionNo = current.VersionNo,
            SourceJobId = job.JobId.Value,
            SourceVersionNo = source.VersionNo,
        };
    }

    private async Task<Result<JobCopyVersionResponse>> IntoNewJobAsync(JobRow source, QuoteVersionRow version, Instant now, CancellationToken cancellationToken)
    {
        var zone = options.Value.Zone;
        var effective = source.EffectiveAt < now ? now : source.EffectiveAt;
        var expiration = effective == source.EffectiveAt ? source.ExpirationAt : PolicyTime.AnnualEnd(effective, zone);
        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.Job, now.ToBusinessDate(zone)), cancellationToken).ConfigureAwait(false);
        var actor = context.Actor.ToString();
        var jobId = JobId.New();
        var policyId = PolicyId.New();
        var job = new JobRow
        {
            JobId = jobId,
            LegalEntityId = source.LegalEntityId,
            Jurisdiction = source.Jurisdiction,
            JobNumber = JobNumber.Parse(number.Value),
            JobType = Codes.Of(JobType.Submission),
            State = Codes.Of(JobStateModel.Machine.Start(JobState.Draft).Value),
            PolicyId = policyId,
            PolicyholderPartyId = source.PolicyholderPartyId,
            AccountId = source.AccountId,
            ProductCode = source.ProductCode,
            ProductVersion = source.ProductVersion,
            ArtefactHash = source.ArtefactHash,
            RatingArtefactHash = source.RatingArtefactHash,
            ResolutionHash = source.ResolutionHash,
            ResolutionManifest = source.ResolutionManifest,
            Channel = source.Channel,
            ProducerCode = source.ProducerCode,
            QuoteType = source.QuoteType,
            EffectiveAt = effective,
            ExpirationAt = expiration,
            Currency = source.Currency,
            CurrentVersionNo = 1,
            RecordVersion = 1,
            CreatedAt = now,
            CreatedBy = actor,
            Participants = [actor],
            UpdatedAt = now,
        };
        var created = new QuoteVersionRow
        {
            QuoteId = QuoteId.New(),
            JobId = jobId,
            LegalEntityId = source.LegalEntityId,
            VersionNo = 1,
            State = Codes.Of(QuoteStateModel.Machine.Start(QuoteState.Draft).Value),
            DraftVersion = 0,
            RiskTree = version.RiskTree,
            RecordVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Jobs.Add(job);
        db.QuoteVersions.Add(created);
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(SubmissionCreatedV1.Descriptor), "Policy", policyId.Value.ToString(),
            new SubmissionCreatedV1
            {
                JobId = jobId, AccountId = source.AccountId, ProductCode = source.ProductCode, ProductVersion = ProductVersionNumber.Parse(source.ProductVersion),
                Channel = source.Channel, ProducerCode = source.ProducerCode,
            },
            BusinessKeys.Empty.With("policyId", policyId.Value.ToString()).With("jobId", jobId.Value.ToString())));

        if (await QuoteVersioning.SaveAsync(db, cancellationToken).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        return new JobCopyVersionResponse
        {
            JobId = jobId,
            JobNumber = job.JobNumber,
            VersionNo = 1,
            QuoteId = created.QuoteId,
            DraftVersion = 0,
            State = JobStateCode.Draft,
            RecordVersion = job.RecordVersion,
            SourceJobId = source.JobId.Value,
            SourceVersionNo = version.VersionNo,
        };
    }
}

/// <summary>Audit facts of <c>pol.Job.copyVersion</c>: source and target, no risk data.</summary>
internal sealed class CopyVersionAuditor : ICommandAuditor<CopyVersion, JobCopyVersionResponse>
{
    public CommandAuditFacts Describe(CopyVersion command, Result<JobCopyVersionResponse>? result)
    {
        if (result is not { IsSuccess: true } success)
        {
            return new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.POL, "Job", command.Request.JobId) };
        }

        var r = success.Value;
        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.POL, "Job", r.JobId),
            BusinessKeys = BusinessKeys.Empty.With("jobId", r.JobId.Value.ToString()).With("quoteId", r.QuoteId.Value.ToString())
                .With("sourceJobId", command.Request.JobId.Value.ToString()),
            Changes = AuditDiff.Compute(
                null, new { target = command.Request.Target.ToString(), sourceVersion = command.Request.SourceVersion, versionNo = r.VersionNo, jobNumber = r.JobNumber?.Value }),
        };
    }
}
