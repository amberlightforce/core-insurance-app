using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;

namespace CoreIns.Modules.Policy.Commands.Quotes;

/// <summary><c>pol.Job.newVersion</c>: the current quote version of a Quoted job becomes the source of a new editable version.</summary>
internal sealed record NewVersion(JobNewVersionRequest Request) : ICommand<JobNewVersionResponse>;

internal sealed class NewVersionValidator : AbstractValidator<NewVersion>
{
    public NewVersionValidator()
    {
        RuleFor(c => c.Request.SourceVersion).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
    }
}

/// <summary>
/// Copies the current version of a job into a new Draft version (REQ-POL-150, REQ-POL-153): Quoted to Draft, the old version is
/// Superseded and never changes again, the new one holds a copy of the risk tree and no price. The job keeps its pinned
/// rating artefact; only <c>pol.Job.requote</c> can change it. Concurrency: <c>expectedRecordVersion</c>.
/// </summary>
internal sealed class NewVersionHandler(PolicyDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
    : ICommandHandler<NewVersion, JobNewVersionResponse>
{
    public async Task<Result<JobNewVersionResponse>> HandleAsync(NewVersion command, CancellationToken cancellationToken)
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

        if (source.VersionNo != job.CurrentVersionNo)
        {
            return JobSupport.Stale();
        }

        var created = QuoteVersioning.Branch(db, job, source, source, context, now);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        if (await QuoteVersioning.SaveAsync(db, cancellationToken).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        var version = created.Value;
        return new JobNewVersionResponse
        {
            JobId = job.JobId,
            JobNumber = job.JobNumber,
            VersionNo = version.VersionNo,
            QuoteId = version.QuoteId,
            DraftVersion = version.DraftVersion,
            State = Codes.Api(Codes.Parse<JobState>(job.State)),
            RecordVersion = job.RecordVersion,
            SupersededVersionNo = source.VersionNo,
        };
    }
}

/// <summary>Audit facts of <c>pol.Job.newVersion</c>.</summary>
internal sealed class NewVersionAuditor : ICommandAuditor<NewVersion, JobNewVersionResponse>
{
    public CommandAuditFacts Describe(NewVersion command, Result<JobNewVersionResponse>? result)
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
            BusinessKeys = BusinessKeys.Empty.With("jobId", r.JobId.Value.ToString()).With("quoteId", r.QuoteId.Value.ToString()),
            Changes = AuditDiff.Compute(
                new { versionNo = command.Request.SourceVersion, recordVersion = command.Request.ExpectedRecordVersion },
                new { versionNo = r.VersionNo, recordVersion = r.RecordVersion, superseded = r.SupersededVersionNo }),
        };
    }
}
