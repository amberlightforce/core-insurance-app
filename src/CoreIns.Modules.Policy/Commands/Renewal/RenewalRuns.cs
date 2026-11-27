using System.ComponentModel.DataAnnotations;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Domain.Servicing;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Policy.Commands.Renewal;

/// <summary>Statuses and outcomes of <c>pol.renewal_run</c> / <c>pol.renewal_run_item</c> (BO-POL-10).</summary>
internal static class RenewalRunCodes
{
    public const string Running = "RUNNING";
    public const string Paused = "PAUSED";
    public const string Completed = "COMPLETED";
    public const string CompletedWithErrors = "COMPLETED_WITH_ERRORS";

    public const string Pending = "PENDING";
    public const string Created = "CREATED";
    public const string WouldCreate = "WOULD_CREATE";
    public const string SkippedNonRenewed = "SKIPPED_NON_RENEWED";
    public const string SkippedVulnerable = "SKIPPED_VULNERABLE";
    public const string SkippedExisting = "SKIPPED_EXISTING";
    public const string Failed = "FAILED";

    /// <summary>A guard against a run that selects far more terms than one worker pass can reasonably carry. TODO(hardening): page the selection.</summary>
    public const int MaxItems = 20000;

    public static IReadOnlyList<string> Outcomes { get; } = [Pending, Created, WouldCreate, SkippedNonRenewed, SkippedVulnerable, SkippedExisting, Failed];
}

/// <summary>Settings of the background renewal run (<c>Policy:RenewalRun</c>).</summary>
internal sealed class RenewalRunOptions
{
    public const string Section = "Policy:RenewalRun";

    /// <summary>Items per chunk; pause and the lease are checked between chunks.</summary>
    [Range(1, 1000)]
    public int ChunkSize { get; set; } = 20;

    /// <summary>Seconds between worker polls. 0 turns the hosted worker off (tests call the runner).</summary>
    [Range(0, 3600)]
    public int PollSeconds { get; set; } = 30;

    /// <summary>Seconds a worker owns a run without renewing; renewed after every chunk.</summary>
    [Range(5, 3600)]
    public int LeaseSeconds { get; set; } = 120;
}

/// <summary>Selection of the terms a run covers: the renewal window in Athens calendar days (REQ-POL-245, REQ-POL-247).</summary>
internal static class RenewalRunSelection
{
    /// <summary>True when the term ends after <paramref name="now"/> and within <paramref name="windowDays"/> Athens days (a leap day counts as a day).</summary>
    public static bool InWindow(Instant now, Instant validTo, int windowDays, TimeZoneInfo zone) =>
        validTo > now && DayCount.Days(now, validTo, zone) <= windowDays;
}

/// <summary>Builds the <see cref="RenewalRunView"/> of a run: counters per outcome and up to 500 items, failed ones first.</summary>
internal sealed class RenewalRunReader(PolicyDbContext db)
{
    private const int MaxItems = 500;

    public async Task<RenewalRunView?> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.RenewalRuns.AsNoTracking().SingleOrDefaultAsync(r => r.RunId == runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return null;
        }

        var grouped = await db.RenewalRunItems.AsNoTracking().Where(i => i.RunId == runId).GroupBy(i => i.Outcome)
            .Select(g => new { Outcome = g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var counts = RenewalRunCodes.Outcomes.ToDictionary(o => o, o => grouped.FirstOrDefault(g => g.Outcome == o)?.Count ?? 0);
        var rows = await db.RenewalRunItems.AsNoTracking().Where(i => i.RunId == runId)
            .OrderBy(i => i.Outcome == RenewalRunCodes.Failed ? 0 : 1).ThenBy(i => i.Seq).Take(MaxItems).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new RenewalRunView
        {
            RunId = run.RunId,
            Status = run.Status switch
            {
                RenewalRunCodes.Paused => RenewalRunView.StatusValue.Paused,
                RenewalRunCodes.Completed => RenewalRunView.StatusValue.Completed,
                RenewalRunCodes.CompletedWithErrors => RenewalRunView.StatusValue.CompletedWithErrors,
                _ => RenewalRunView.StatusValue.Running,
            },
            DryRun = run.DryRun,
            Product = run.ProductCode,
            WindowDays = run.WindowDays,
            Total = run.Total,
            Counts = counts,
            Items = rows.Select(i => new RenewalRunItemView
            {
                ItemId = i.ItemId,
                Seq = i.Seq,
                TermId = i.TermId,
                PolicyId = i.PolicyId,
                Outcome = i.Outcome switch
                {
                    RenewalRunCodes.Created => RenewalRunItemOutcome.Created,
                    RenewalRunCodes.WouldCreate => RenewalRunItemOutcome.WouldCreate,
                    RenewalRunCodes.SkippedNonRenewed => RenewalRunItemOutcome.SkippedNonRenewed,
                    RenewalRunCodes.SkippedVulnerable => RenewalRunItemOutcome.SkippedVulnerable,
                    RenewalRunCodes.SkippedExisting => RenewalRunItemOutcome.SkippedExisting,
                    RenewalRunCodes.Failed => RenewalRunItemOutcome.Failed,
                    _ => RenewalRunItemOutcome.Pending,
                },
                ErrorCode = i.ErrorCode,
                Detail = i.Detail,
                JobId = i.JobId,
                Attempts = i.Attempts,
                UpdatedAt = i.UpdatedAt,
            }).ToList(),
            RequestedBy = run.RequestedBy,
            CreatedAt = run.CreatedAt,
            FinishedAt = run.FinishedAt,
        };
    }
}

/// <summary>Shared pieces of the run commands.</summary>
internal static class RenewalRunSupport
{
    public static DomainError NotFound() => JobSupport.NotFound("renewal run");

    public static DomainError RunActive() =>
        DomainError.Of(ModuleCode.POL, "RUN-ACTIVE", "A renewal run of this legal entity is already running or paused.");

    public static DomainError Illegal(string message) => DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", message);

    public static DomainError Stale() => DomainError.Of(ModuleCode.POL, "STALE", "The renewal run changed meanwhile; load it again.");

    public static Task<bool> ActiveRunExistsAsync(PolicyDbContext db, LegalEntityId legalEntity, Guid? except, CancellationToken cancellationToken) =>
        db.RenewalRuns.AnyAsync(
            r => r.LegalEntityId == legalEntity && (r.Status == RenewalRunCodes.Running || r.Status == RenewalRunCodes.Paused) && r.RunId != except, cancellationToken);

    public static CommandAuditFacts Audit(RenewalRunView? run, string action)
    {
        if (run is null)
        {
            return new CommandAuditFacts();
        }

        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.POL, "RenewalRun", run.RunId.ToString("D")),
            BusinessKeys = BusinessKeys.Empty.With("runId", run.RunId.ToString("D")),
            Changes = AuditDiff.Compute(null, new { action, status = run.Status.ToString(), run.Total, run.DryRun }),
        };
    }
}

/// <summary><c>pol.RenewalRun.start</c>: selects the terms inside the renewal window and queues them (or resumes a paused run).</summary>
internal sealed record StartRenewalRun(RenewalRunStartRequest Request) : ICommand<RenewalRunStartResponse>;

internal sealed class StartRenewalRunValidator : AbstractValidator<StartRenewalRun>
{
    public StartRenewalRunValidator()
    {
        RuleFor(c => c.Request.Product).MaximumLength(64).WithErrorCode("PRODUCT_TOO_LONG");
        RuleFor(c => c.Request.WindowDays!.Value).InclusiveBetween(1, 365).When(c => c.Request.WindowDays is not null).WithErrorCode("WINDOW_INVALID");
    }
}

/// <summary>
/// Starts a renewal run (REQ-POL-247): one live (RUNNING or PAUSED) run per legal entity, enforced by a partial unique index. The terms that
/// are Scheduled or InForce, end after now and within <c>windowDays</c> Athens days (default and maximum <c>pol.renewal.lead_days</c>, the
/// product filter optional) become PENDING items in end-date order; the worker processes them (<see cref="RenewalRunWorkerRunner"/>). With
/// <c>resumeRunId</c> a PAUSED run is made RUNNING again instead.
/// </summary>
internal sealed class StartRenewalRunHandler(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    RenewalRunReader reader,
    IOptions<PolicyOptions> options,
    IOptions<RenewalOptions> renewalOptions) : ICommandHandler<StartRenewalRun, RenewalRunStartResponse>
{
    public async Task<Result<RenewalRunStartResponse>> HandleAsync(StartRenewalRun command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var now = clock.Now;
        if (request.ResumeRunId is { } resumeId)
        {
            var paused = await db.RenewalRuns.SingleOrDefaultAsync(r => r.RunId == resumeId && r.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
            if (paused is null)
            {
                return RenewalRunSupport.NotFound();
            }

            if (paused.Status != RenewalRunCodes.Paused)
            {
                return RenewalRunSupport.Illegal($"A {paused.Status} renewal run cannot be resumed; only a paused one can.");
            }

            paused.Status = RenewalRunCodes.Running;
            paused.LeaseUntil = null;
            paused.UpdatedAt = now;
            paused.RecordVersion++;
            return await SaveAsync(resumeId, cancellationToken).ConfigureAwait(false);
        }

        var lead = renewalOptions.Value.LeadDays;
        var window = request.WindowDays ?? lead;
        if (window > lead)
        {
            return RenewalSupport.Validation("windowDays", "WINDOW_EXCEEDS_LEAD_DAYS", $"The window cannot exceed the renewal lead time of {lead} days.");
        }

        if (await RenewalRunSupport.ActiveRunExistsAsync(db, legalEntity, null, cancellationToken).ConfigureAwait(false))
        {
            return RenewalRunSupport.RunActive();
        }

        var zone = options.Value.Zone;
        var scheduled = Codes.Of(PolicyTermState.Scheduled);
        var inForce = Codes.Of(PolicyTermState.InForce);
        var horizon = now.Plus(TimeSpan.FromDays(window + 2)); // coarse upper bound; the Athens day count decides
        var product = string.IsNullOrWhiteSpace(request.Product) ? null : request.Product.Trim();
        var candidates = await (
                from t in db.Terms.AsNoTracking()
                join p in db.Policies.AsNoTracking() on t.PolicyId equals p.PolicyId
                where t.LegalEntityId == legalEntity && t.RecordedTo == null && (t.State == scheduled || t.State == inForce)
                      && t.ValidTo > now && t.ValidTo <= horizon && (product == null || p.ProductCode == product)
                orderby t.ValidTo, t.TermId
                select new { t.TermId, t.PolicyId, t.ValidTo })
            .Take(RenewalRunCodes.MaxItems + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var selected = candidates.Where(c => RenewalRunSelection.InWindow(now, c.ValidTo, window, zone)).ToList();
        if (selected.Count > RenewalRunCodes.MaxItems)
        {
            return RenewalSupport.Validation("windowDays", "TOO_MANY_TERMS", $"More than {RenewalRunCodes.MaxItems} terms match; narrow the window or filter by product.");
        }

        var runId = Guid.CreateVersion7();
        db.RenewalRuns.Add(new RenewalRunRow
        {
            RunId = runId,
            LegalEntityId = legalEntity,
            Status = RenewalRunCodes.Running,
            DryRun = request.DryRun == true,
            ProductCode = product,
            WindowDays = window,
            SelectedAt = now,
            Total = selected.Count,
            RequestedBy = context.Actor.ToString(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        var seq = 0;
        foreach (var item in selected)
        {
            db.RenewalRunItems.Add(new RenewalRunItemRow
            {
                ItemId = Guid.CreateVersion7(),
                RunId = runId,
                LegalEntityId = legalEntity,
                Seq = ++seq,
                TermId = item.TermId,
                PolicyId = item.PolicyId,
                Outcome = RenewalRunCodes.Pending,
                UpdatedAt = now,
            });
        }

        return await SaveAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<RenewalRunStartResponse>> SaveAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A racing start created the live run first (ux_renewal_run_active).
            return RenewalRunSupport.RunActive();
        }
        catch (DbUpdateConcurrencyException)
        {
            return RenewalRunSupport.Stale();
        }

        db.ChangeTracker.Clear();
        return new RenewalRunStartResponse { Run = (await reader.GetAsync(runId, cancellationToken).ConfigureAwait(false))! };
    }
}

internal sealed class StartRenewalRunAuditor : ICommandAuditor<StartRenewalRun, RenewalRunStartResponse>
{
    public CommandAuditFacts Describe(StartRenewalRun command, Result<RenewalRunStartResponse>? result) =>
        RenewalRunSupport.Audit(result is { IsSuccess: true } ok ? ok.Value.Run : null, command.Request.ResumeRunId is null ? "start" : "resume");
}

/// <summary><c>pol.RenewalRun.pause</c>: the worker stops after the chunk it is on; the run keeps its place.</summary>
internal sealed record PauseRenewalRun(RenewalRunPauseRequest Request) : ICommand<RenewalRunPauseResponse>;

internal sealed class PauseRenewalRunValidator : AbstractValidator<PauseRenewalRun>
{
    public PauseRenewalRunValidator() => RuleFor(c => c.Request.RunId).NotEmpty().WithErrorCode("RUN_REQUIRED");
}

internal sealed class PauseRenewalRunHandler(
    PolicyDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, RenewalRunReader reader)
    : ICommandHandler<PauseRenewalRun, RenewalRunPauseResponse>
{
    public async Task<Result<RenewalRunPauseResponse>> HandleAsync(PauseRenewalRun command, CancellationToken cancellationToken)
    {
        var runId = command.Request.RunId;
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var run = await db.RenewalRuns.SingleOrDefaultAsync(r => r.RunId == runId && r.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return RenewalRunSupport.NotFound();
        }

        if (run.Status == RenewalRunCodes.Running)
        {
            run.Status = RenewalRunCodes.Paused;
            run.LeaseUntil = null;
            run.UpdatedAt = clock.Now;
            run.RecordVersion++;
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                return RenewalRunSupport.Stale();
            }

            db.ChangeTracker.Clear();
        }
        else if (run.Status != RenewalRunCodes.Paused)
        {
            return RenewalRunSupport.Illegal($"A {run.Status} renewal run cannot be paused.");
        }

        return new RenewalRunPauseResponse { Run = (await reader.GetAsync(runId, cancellationToken).ConfigureAwait(false))! };
    }
}

internal sealed class PauseRenewalRunAuditor : ICommandAuditor<PauseRenewalRun, RenewalRunPauseResponse>
{
    public CommandAuditFacts Describe(PauseRenewalRun command, Result<RenewalRunPauseResponse>? result) =>
        RenewalRunSupport.Audit(result is { IsSuccess: true } ok ? ok.Value.Run : null, "pause");
}

/// <summary><c>pol.RenewalRun.retryItem</c>: a FAILED item goes back to PENDING; a finished run becomes RUNNING again.</summary>
internal sealed record RetryRenewalRunItem(RenewalRunRetryItemRequest Request) : ICommand<RenewalRunRetryItemResponse>;

internal sealed class RetryRenewalRunItemValidator : AbstractValidator<RetryRenewalRunItem>
{
    public RetryRenewalRunItemValidator()
    {
        RuleFor(c => c.Request.RunId).NotEmpty().WithErrorCode("RUN_REQUIRED");
        RuleFor(c => c.Request.ItemId).NotEmpty().WithErrorCode("ITEM_REQUIRED");
    }
}

internal sealed class RetryRenewalRunItemHandler(
    PolicyDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, RenewalRunReader reader)
    : ICommandHandler<RetryRenewalRunItem, RenewalRunRetryItemResponse>
{
    public async Task<Result<RenewalRunRetryItemResponse>> HandleAsync(RetryRenewalRunItem command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var run = await db.RenewalRuns.SingleOrDefaultAsync(r => r.RunId == request.RunId && r.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        var item = run is null
            ? null
            : await db.RenewalRunItems.SingleOrDefaultAsync(i => i.ItemId == request.ItemId && i.RunId == request.RunId, cancellationToken).ConfigureAwait(false);
        if (run is null || item is null)
        {
            return RenewalRunSupport.NotFound();
        }

        if (run.DryRun)
        {
            return RenewalRunSupport.Illegal("Items of a dry run are not retried; start a real run.");
        }

        if (item.Outcome != RenewalRunCodes.Failed)
        {
            return RenewalRunSupport.Illegal($"Only a FAILED item can be retried; this one is {item.Outcome}.");
        }

        var finished = run.Status is RenewalRunCodes.Completed or RenewalRunCodes.CompletedWithErrors;
        if (finished && await RenewalRunSupport.ActiveRunExistsAsync(db, legalEntity, run.RunId, cancellationToken).ConfigureAwait(false))
        {
            return RenewalRunSupport.RunActive();
        }

        var now = clock.Now;
        item.Outcome = RenewalRunCodes.Pending;
        item.ErrorCode = null;
        item.UpdatedAt = now;
        item.RecordVersion++;
        if (finished)
        {
            run.Status = RenewalRunCodes.Running;
            run.FinishedAt = null;
            run.LeaseUntil = null;
        }

        run.UpdatedAt = now;
        run.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return RenewalRunSupport.RunActive();
        }
        catch (DbUpdateConcurrencyException)
        {
            return RenewalRunSupport.Stale();
        }

        db.ChangeTracker.Clear();
        return new RenewalRunRetryItemResponse { Run = (await reader.GetAsync(request.RunId, cancellationToken).ConfigureAwait(false))! };
    }
}

internal sealed class RetryRenewalRunItemAuditor : ICommandAuditor<RetryRenewalRunItem, RenewalRunRetryItemResponse>
{
    public CommandAuditFacts Describe(RetryRenewalRunItem command, Result<RenewalRunRetryItemResponse>? result) =>
        RenewalRunSupport.Audit(result is { IsSuccess: true } ok ? ok.Value.Run : null, "retry-item");
}
