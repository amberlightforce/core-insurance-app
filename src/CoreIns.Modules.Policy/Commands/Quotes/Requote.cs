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

/// <summary><c>pol.Job.requote</c>: re-rates and re-evaluates the current version as a new quote version.</summary>
internal sealed record Requote(JobRequoteRequest Request) : ICommand<JobRequoteResponse>;

internal sealed class RequoteValidator : AbstractValidator<Requote>
{
    public RequoteValidator()
    {
        RuleFor(c => c.Request.VersionNo).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.RatingArtefact).IsInEnum();
        RuleFor(c => c.Request.RatingArtefactHash).Null()
            .When(c => c.Request.RatingArtefact == JobRequoteRequest.RatingArtefactValue.InForceNow)
            .WithErrorCode("HASH_WITH_PINNED_ONLY");
    }
}

/// <summary>
/// Re-prices the current version (REQ-POL-011, REQ-POL-115). A job keeps its rating artefact once rated (REQ-PFC-221), so the
/// caller must say which artefact the new price is for, and the choice is recorded (audit and response):
/// <list type="bullet">
/// <item><c>IN_FORCE_NOW</c>: the pin is released and RAT rates under the artefact in force for the effective date (effective-dated
/// activation, BO-RAT-06); the job is pinned to that artefact from then on.</item>
/// <item><c>PINNED</c>: the job's pinned artefact (or <c>ratingArtefactHash</c>, which RAT checks against the product) is used again.</item>
/// </list>
/// A Quoted job is branched like an edit (the old version is Superseded and stays as it was, a copy becomes the new version) and the
/// new version goes through the same pipeline as <c>pol.Job.quote</c>: validation, rating, UW at PRE_QUOTE, validity. A failure
/// anywhere rolls the whole command back, so the old quote stays Quoted. A Draft job (for example one blocked at PRE_QUOTE) is
/// re-quoted in place. <c>QuoteIssued</c> is published by the quote step when the version is quoted.
/// </summary>
internal sealed class RequoteHandler(
    PolicyDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, QuoteJobHandler quote)
    : ICommandHandler<Requote, JobRequoteResponse>
{
    public async Task<Result<JobRequoteResponse>> HandleAsync(Requote command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var now = clock.Now;
        var loaded = await JobSupport.LoadAsync(db, JobSupport.LegalEntity(context, legalEntities), request.JobId, request.VersionNo, cancellationToken)
            .ConfigureAwait(false);
        if (loaded is not var (job, current))
        {
            return JobSupport.NotFound("job or quote version");
        }

        var refused = QuoteVersioning.RequireSubmission(job) ?? QuoteVersioning.CheckRecordVersion(job, request.ExpectedRecordVersion);
        if (refused is not null)
        {
            return refused;
        }

        if (current.VersionNo != job.CurrentVersionNo)
        {
            return JobSupport.Stale();
        }

        var previous = job.RatingArtefactHash;
        string? artefact = null;
        if (request.RatingArtefact == JobRequoteRequest.RatingArtefactValue.Pinned)
        {
            artefact = request.RatingArtefactHash?.Value ?? previous;
            if (artefact is null)
            {
                return new DomainError(ErrorCode.For(ModuleCode.POL, "VALIDATION"), "The job is not pinned to a rating artefact yet; name one or requote with IN_FORCE_NOW.")
                {
                    FieldErrors = [new FieldError("ratingArtefactHash", "ARTEFACT_REQUIRED", "pol.artefact_required")],
                };
            }
        }

        var state = Codes.Parse<JobState>(job.State);
        if (state is not (JobState.Draft or JobState.Quoted))
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", $"A {state} job cannot be requoted.");
        }

        var target = current;
        int? supersededVersionNo = null;
        if (state == JobState.Quoted)
        {
            var branched = QuoteVersioning.Branch(db, job, current, current, context, now);
            if (branched.IsFailure)
            {
                return branched.Error!;
            }

            target = branched.Value;
            supersededVersionNo = current.VersionNo;
        }

        job.RatingArtefactHash = artefact;
        if (await QuoteVersioning.SaveAsync(db, cancellationToken).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        var quoted = await quote.HandleAsync(new QuoteJob(new JobQuoteRequest { JobId = job.JobId, VersionNo = target.VersionNo }), cancellationToken).ConfigureAwait(false);
        if (quoted.IsFailure)
        {
            return quoted.Error!;
        }

        var q = quoted.Value;
        return new JobRequoteResponse
        {
            JobId = q.JobId,
            QuoteId = q.QuoteId,
            VersionNo = q.VersionNo,
            SupersededVersionNo = supersededVersionNo,
            State = q.State,
            Decision = Enum.Parse<JobRequoteResponse.DecisionValue>(q.Decision.ToString()),
            Referred = q.Referred,
            Bindable = q.Bindable,
            Premium = q.Premium,
            Taxes = q.Taxes,
            Total = q.Total,
            Issues = q.Issues,
            ValidUntil = q.ValidUntil,
            RatingArtefact = Enum.Parse<JobRequoteResponse.RatingArtefactValue>(request.RatingArtefact.ToString()),
            RatingArtefactHash = job.RatingArtefactHash is null ? null : Sha256Hash.Parse(job.RatingArtefactHash),
            PreviousRatingArtefactHash = previous is null ? null : Sha256Hash.Parse(previous),
            RecordVersion = job.RecordVersion,
        };
    }
}

/// <summary>Audit facts of <c>pol.Job.requote</c>: which artefact was asked for and used, and the version movement.</summary>
internal sealed class RequoteAuditor : ICommandAuditor<Requote, JobRequoteResponse>
{
    public CommandAuditFacts Describe(Requote command, Result<JobRequoteResponse>? result)
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
                new { versionNo = command.Request.VersionNo, ratingArtefactHash = r.PreviousRatingArtefactHash?.Value },
                new
                {
                    versionNo = r.VersionNo, ratingArtefactMode = Codes.Of(r.RatingArtefact), ratingArtefactHash = r.RatingArtefactHash?.Value,
                    decision = r.Decision.ToString(), total = r.Total.ToString(),
                }),
        };
    }
}
