using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Policy.Commands;

/// <summary><c>pol.Job.updateDraft</c>: applies intent edits (vehicles, drivers, coverages, answers) to a quote version.</summary>
internal sealed record UpdateDraft(JobUpdateDraftRequest Request) : ICommand<JobUpdateDraftResponse>;

internal sealed class UpdateDraftValidator : AbstractValidator<UpdateDraft>
{
    public UpdateDraftValidator()
    {
        RuleFor(c => c.Request.VersionNo).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.ExpectedDraftVersion).GreaterThanOrEqualTo(0);
        RuleFor(c => c.Request.Instructions).NotEmpty().Must(i => i.Count <= 100).WithErrorCode("TOO_MANY_INSTRUCTIONS");
        RuleForEach(c => c.Request.Instructions).ChildRules(instruction =>
        {
            instruction.When(i => i.Vehicle is not null, () =>
            {
                instruction.RuleFor(i => i.Vehicle!.Plate).NotEmpty().MaximumLength(32);
                instruction.RuleFor(i => i.Vehicle!.Vin).MaximumLength(32);
                instruction.RuleFor(i => i.Vehicle!.PlateNormalised).Null().WithErrorCode("OUTPUT_ONLY");
            });
            instruction.When(i => i.Driver is not null, () => instruction.RuleFor(i => i.Driver!.PartyId.Value).NotEmpty().WithErrorCode("PARTY_REQUIRED"));
            instruction.RuleForEach(i => i.Coverages).ChildRules(coverage => coverage.RuleFor(c => c.CoverageCode).NotEmpty().MaximumLength(128));
        });
    }
}

/// <summary>
/// Edits a draft (REQ-POL-010, REQ-POL-001): the instructions are applied in order to the version's risk tree, every new
/// element gets a static locator (REQ-POL-036), and the draft version moves on (optimistic concurrency through
/// <c>expectedDraftVersion</c>, POL-ERR-STALE). Editing a Quoted job takes the Edit transition back to Draft
/// (D-CON-08c, REQ-POL-153): the quoted version is superseded and the edits land on a new version, so a quoted price
/// never changes in place. Findings that block quoting are returned, not raised, so a partial draft can be saved.
/// </summary>
internal sealed class UpdateDraftHandler(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    RiskTrees riskTrees) : ICommandHandler<UpdateDraft, JobUpdateDraftResponse>
{
    private const int MaxVersions = 20;

    public async Task<Result<JobUpdateDraftResponse>> HandleAsync(UpdateDraft command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var now = clock.Now;
        var loaded = await JobSupport.LoadAsync(db, JobSupport.LegalEntity(context, legalEntities), request.JobId, request.VersionNo, cancellationToken)
            .ConfigureAwait(false);
        if (loaded is not var (job, version))
        {
            return JobSupport.NotFound("job or quote version");
        }

        if (version.VersionNo != job.CurrentVersionNo || version.DraftVersion != request.ExpectedDraftVersion)
        {
            return JobSupport.Stale();
        }

        var target = version;
        if (Codes.Parse<JobState>(job.State) == JobState.Quoted)
        {
            // Quoted → Draft (Edit): supersede the quoted version and continue on a copy.
            var edit = JobSupport.Fire(job, JobTrigger.Edit);
            var superseded = JobSupport.Fire(version, QuoteTrigger.Supersede);
            if (edit.IsFailure || superseded.IsFailure)
            {
                // The job or the version was read while a concurrent edit was moving it (the winner superseded the quote
                // between our two reads): that is a stale request, not an illegal transition.
                return JobSupport.Stale();
            }

            if (version.VersionNo >= MaxVersions)
            {
                return DomainError.Of(ModuleCode.POL, "VALIDATION", $"A job holds at most {MaxVersions} quote versions.");
            }

            job.State = Codes.Of(edit.Value);
            version.State = Codes.Of(superseded.Value);
            version.RecordVersion++;
            version.UpdatedAt = now;
            target = new QuoteVersionRow
            {
                QuoteId = QuoteId.New(),
                JobId = job.JobId,
                LegalEntityId = job.LegalEntityId,
                VersionNo = version.VersionNo + 1,
                State = Codes.Of(QuoteStateModel.Machine.Start(QuoteState.Draft).Value),
                DraftVersion = version.DraftVersion,
                RiskTree = version.RiskTree,
                RecordVersion = 1,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.QuoteVersions.Add(target);
            job.CurrentVersionNo = target.VersionNo;
        }
        else if (Codes.Parse<JobState>(job.State) == JobState.Draft && Codes.Parse<QuoteState>(version.State) != QuoteState.Draft)
        {
            // A Draft job whose version is no longer a draft: a concurrent edit superseded it between our reads.
            return JobSupport.Stale();
        }
        else if (Codes.Parse<JobState>(job.State) != JobState.Draft || Codes.Parse<QuoteState>(version.State) != QuoteState.Draft)
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", $"A {job.State} job cannot be edited.");
        }

        var tree = await riskTrees.ApplyAsync(JobSupport.Tree(target), request.Instructions, job.Jurisdiction, cancellationToken).ConfigureAwait(false);
        if (tree.IsFailure)
        {
            return tree.Error!;
        }

        target.RiskTree = JobSupport.Json(tree.Value);
        target.DraftVersion++;
        target.UpdatedAt = now;
        JobSupport.AddParticipant(job, context.Actor);
        job.RecordVersion++;
        job.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return JobSupport.Stale();
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // A parallel edit of the same quoted job created the next version first.
            return JobSupport.Stale();
        }

        return new JobUpdateDraftResponse
        {
            JobId = job.JobId,
            VersionNo = target.VersionNo,
            DraftVersion = target.DraftVersion,
            State = Codes.Api(Codes.Parse<JobState>(job.State)),
            RiskTree = tree.Value,
            Validation = RiskTrees.Findings(tree.Value),
        };
    }
}

/// <summary>Audit facts of <c>pol.Job.updateDraft</c>: the job, the version and the instruction kinds (element contents stay in the job).</summary>
internal sealed class UpdateDraftAuditor : ICommandAuditor<UpdateDraft, JobUpdateDraftResponse>
{
    public CommandAuditFacts Describe(UpdateDraft command, Result<JobUpdateDraftResponse>? result)
    {
        if (result is not { IsSuccess: true } success)
        {
            return new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.POL, "Job", command.Request.JobId) };
        }

        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.POL, "Job", success.Value.JobId),
            BusinessKeys = BusinessKeys.Empty.With("jobId", success.Value.JobId.Value.ToString()),
            Changes = AuditDiff.Compute(
                new { versionNo = command.Request.VersionNo, draftVersion = command.Request.ExpectedDraftVersion },
                new { versionNo = success.Value.VersionNo, draftVersion = success.Value.DraftVersion, instructions = command.Request.Instructions.Select(i => i.Op.ToString()).ToArray() }),
        };
    }
}
