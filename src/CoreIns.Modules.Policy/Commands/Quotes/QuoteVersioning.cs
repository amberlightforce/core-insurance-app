using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreIns.Modules.Policy.Commands.Quotes;

/// <summary>
/// Shared steps of the quote lifecycle commands (BO-POL-03): the preconditions every one of them checks and the branching of a
/// Quoted job into a new editable version. A version is immutable once it leaves Draft: a change is always a new version.
/// </summary>
internal static class QuoteVersioning
{
    /// <summary>Most versions a job may hold (as in <c>pol.Job.updateDraft</c>).</summary>
    public const int MaxVersions = 20;

    /// <summary>Quote lifecycle commands apply to new-business jobs; changes, cancellations and renewals have their own commands.</summary>
    public static DomainError? RequireSubmission(JobRow job) =>
        job.JobType == Codes.Of(JobType.Submission)
            ? null
            : DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", $"A {job.JobType} job has no quote versions to manage here.");

    /// <summary>The optimistic-concurrency check on the job record version (POL-ERR-STALE).</summary>
    public static DomainError? CheckRecordVersion(JobRow job, int expected) => job.RecordVersion == expected ? null : JobSupport.Stale();

    /// <summary>
    /// Takes a Quoted job back to Draft (Edit), supersedes its current version and adds a new Draft version holding a copy of
    /// <paramref name="source"/> risk tree. Pricing, worksheet and issues of the source are not copied: the new version is
    /// unpriced. Nothing is saved.
    /// </summary>
    public static Result<QuoteVersionRow> Branch(
        PolicyDbContext db, JobRow job, QuoteVersionRow current, QuoteVersionRow source, RequestContext context, Instant now)
    {
        if (Codes.Parse<JobState>(job.State) != JobState.Quoted)
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", $"A {job.State} job has no quoted version to copy; edit the draft directly.");
        }

        var edit = JobSupport.Fire(job, JobTrigger.Edit);
        var superseded = JobSupport.Fire(current, QuoteTrigger.Supersede);
        if (edit.IsFailure || superseded.IsFailure)
        {
            return (edit.IsFailure ? edit.Error : superseded.Error)!;
        }

        if (job.CurrentVersionNo >= MaxVersions)
        {
            return DomainError.Of(ModuleCode.POL, "VALIDATION", $"A job holds at most {MaxVersions} quote versions.");
        }

        job.State = Codes.Of(edit.Value);
        current.State = Codes.Of(superseded.Value);
        current.RecordVersion++;
        current.UpdatedAt = now;
        var created = new QuoteVersionRow
        {
            QuoteId = QuoteId.New(),
            JobId = job.JobId,
            LegalEntityId = job.LegalEntityId,
            VersionNo = job.CurrentVersionNo + 1,
            State = Codes.Of(QuoteStateModel.Machine.Start(QuoteState.Draft).Value),
            DraftVersion = source.DraftVersion,
            RiskTree = source.RiskTree,
            RecordVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.QuoteVersions.Add(created);
        job.CurrentVersionNo = created.VersionNo;
        JobSupport.AddParticipant(job, context.Actor);
        job.RecordVersion++;
        job.UpdatedAt = now;
        return created;
    }

    /// <summary>Saves; a racing writer (record version moved, or the next version number taken) is POL-ERR-STALE, never a 500.</summary>
    public static async Task<DomainError?> SaveAsync(PolicyDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return JobSupport.Stale();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return JobSupport.Stale();
        }
    }
}
