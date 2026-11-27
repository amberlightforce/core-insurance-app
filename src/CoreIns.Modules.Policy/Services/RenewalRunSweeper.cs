using System.Security.Cryptography;
using System.Text;
using CoreIns.Modules.Policy.Commands;
using CoreIns.Modules.Policy.Commands.Cancellation.NonPayment;
using CoreIns.Modules.Policy.Commands.Renewal;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Configuration;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Telemetry;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Policy.Services;

/// <summary>What happened to one term of a run.</summary>
internal sealed record RenewalItemResult(string Outcome, string? ErrorCode = null, string? Detail = null, JobId? JobId = null);

/// <summary>
/// Handles one term of a renewal run in its own scope and transaction boundary (BO-POL-10). The renewal is created and offered through the
/// same commands as "Renew now" (<c>pol.Renewal.create</c>, <c>pol.Renewal.offer</c>), so every rule of those commands applies. In order:
/// a non-renewed term is skipped; a term already renewed or with an open renewal of another origin is skipped; a vulnerable or unknown
/// policyholder goes to the PSR queue instead of being renewed (NX-HD-02); the product must still be sold for renewal at the new term's start
/// (PFC availability); a dry run stops here; then create and offer. The idempotency keys derive from run, term and attempt, so a replay of
/// the same step returns the stored result; a draft renewal this run created earlier (a crash between create and offer) is offered, not duplicated.
/// </summary>
internal sealed partial class RenewalRunItemProcessor(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IVulnerabilityCheck vulnerability,
    PsrWorkTasks psrTasks,
    Dependency<IProductAvailabilityService> availability,
    ICommandHandler<CreateRenewal, RenewalCreateResponse> create,
    ICommandHandler<OfferRenewal, RenewalOfferResponse> offer,
    ILogger<RenewalRunItemProcessor> logger)
{
    public async Task<RenewalItemResult> ProcessAsync(RenewalRunRow run, RenewalRunItemRow item, CancellationToken cancellationToken)
    {
        try
        {
            return await ProcessCoreAsync(run, item, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return new RenewalItemResult(RenewalRunCodes.Failed, ex.Error.Code.ToString(), Truncate(ex.Error.Detail ?? string.Empty));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, item.ItemId, ex);
            return new RenewalItemResult(RenewalRunCodes.Failed, "POL-ERR-INTERNAL", ex.GetType().Name);
        }
    }

    private async Task<RenewalItemResult> ProcessCoreAsync(RenewalRunRow run, RenewalRunItemRow item, CancellationToken cancellationToken)
    {
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var term = await db.Terms.AsNoTracking()
            .SingleOrDefaultAsync(t => t.TermId == item.TermId && t.LegalEntityId == legalEntity && t.RecordedTo == null, cancellationToken).ConfigureAwait(false);
        var policy = await db.Policies.AsNoTracking().SingleOrDefaultAsync(p => p.PolicyId == item.PolicyId && p.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (term is null || policy is null)
        {
            return new RenewalItemResult(RenewalRunCodes.Failed, "POL-ERR-NOT-FOUND", "The term no longer exists.");
        }

        if (await db.NonRenewals.AnyAsync(n => n.TermId == item.TermId, cancellationToken).ConfigureAwait(false))
        {
            return new RenewalItemResult(RenewalRunCodes.SkippedNonRenewed, Detail: "The term was non-renewed.");
        }

        if (await db.Terms.AnyAsync(t => t.PredecessorTermId == item.TermId && t.RecordedTo == null, cancellationToken).ConfigureAwait(false))
        {
            return new RenewalItemResult(RenewalRunCodes.SkippedExisting, Detail: "The term has already been renewed.");
        }

        var actor = ActorRef.Service(RenewalRunWorkerRunner.ActorId).ToString();
        var draft = Codes.Of(JobState.Draft);
        var open = await db.Jobs.AsNoTracking()
            .Where(j => j.ExpiringTermId == item.TermId && j.LegalEntityId == legalEntity && j.JobType == Codes.Of(JobType.Renewal)
                        && (j.State == draft || j.State == Codes.Of(JobState.Quoted) || j.State == Codes.Of(JobState.Scheduled)))
            .OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        JobId? jobId = null;
        if (open is not null)
        {
            if (open.CreatedBy != actor || open.CreatedAt < run.CreatedAt)
            {
                return new RenewalItemResult(RenewalRunCodes.SkippedExisting, Detail: "The term already has an open renewal.", JobId: open.JobId);
            }

            // This run's own job (a crash or a failed offer left it behind): carry on from where it stopped.
            jobId = open.JobId;
            if (open.State != draft)
            {
                return new RenewalItemResult(RenewalRunCodes.Created, JobId: jobId);
            }
        }
        else
        {
            if (await vulnerability.IsVulnerableAsync(policy.PolicyholderPartyId, cancellationToken).ConfigureAwait(false))
            {
                if (!run.DryRun)
                {
                    await psrTasks.RaiseAsync(
                        "WRK-POL-NONPAY-PSR-EXCEPTION", new ObjectRef(ModuleCode.POL, "Policy", policy.PolicyId.Value.ToString("D")),
                        $"RENEWAL_RUN:{item.TermId.Value:D}", cancellationToken).ConfigureAwait(false);
                }

                return new RenewalItemResult(RenewalRunCodes.SkippedVulnerable, Detail: "Vulnerable or unknown policyholder: PSR review.");
            }

            var sold = await availability.Value.CheckAsync(
                new AvailabilityCheckRequest
                {
                    Channel = RenewalSupport.ChannelStaff,
                    TransactionType = AvailabilityCheckRequest.TransactionTypeValue.Renewal,
                    Product = policy.ProductCode,
                },
                ValidAt.From(term.ValidTo), cancellationToken).ConfigureAwait(false);
            if (!sold.Available)
            {
                return new RenewalItemResult(RenewalRunCodes.Failed, "POL-ERR-PRODUCT-UNAVAILABLE", $"{policy.ProductCode} is not sold for renewal at the term end.");
            }

            if (run.DryRun)
            {
                return new RenewalItemResult(RenewalRunCodes.WouldCreate);
            }

            context.IdempotencyKey = Key(run.RunId, item, "create");
            var created = await create.HandleAsync(new CreateRenewal(new RenewalCreateRequest { TermId = item.TermId }), cancellationToken).ConfigureAwait(false);
            if (created.IsFailure)
            {
                return new RenewalItemResult(RenewalRunCodes.Failed, created.Error!.Code.ToString(), Truncate(created.Error.Detail ?? string.Empty));
            }

            jobId = created.Value.JobId;
        }

        if (run.DryRun)
        {
            return new RenewalItemResult(RenewalRunCodes.WouldCreate, JobId: jobId);
        }

        context.IdempotencyKey = Key(run.RunId, item, "offer");
        var offered = await offer.HandleAsync(new OfferRenewal(new RenewalOfferRequest { JobId = jobId!.Value, TermId = item.TermId }), cancellationToken).ConfigureAwait(false);
        if (offered.IsFailure)
        {
            return new RenewalItemResult(RenewalRunCodes.Failed, offered.Error!.Code.ToString(), Truncate("Created, not offered: " + offered.Error.Detail ?? string.Empty), jobId);
        }

        return new RenewalItemResult(RenewalRunCodes.Created, Detail: offered.Value.Referred == true ? "Held back by an underwriting referral." : null, JobId: jobId);
    }

    private static IdempotencyKey Key(Guid runId, RenewalRunItemRow item, string step) =>
        IdempotencyKey.From(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"renewal-run|{runId:D}|{item.TermId.Value:D}|{step}|{item.Attempts}")).AsSpan(0, 16)));

    private static string Truncate(string text) => text.Length <= 400 ? text : text[..400];

    [LoggerMessage(Level = LogLevel.Error, Message = "Renewal run item {ItemId} failed unexpectedly; it is recorded as FAILED and can be retried.")]
    private static partial void LogFailed(ILogger logger, Guid itemId, Exception exception);
}

/// <summary>
/// Runs a RUNNING renewal run in chunks (BO-POL-10; the cursor/lease pattern of the migration batch processor). Only PENDING items are taken,
/// in seq order, so a resumed run (after pause, retry or a crash) continues where it stopped and no item is handled twice; a worker owns the
/// run through a lease renewed per chunk and the record version arbitrates two claimants. Each item runs in its own scope as the service actor.
/// </summary>
internal sealed partial class RenewalRunWorkerRunner(IServiceScopeFactory scopes, IClock clock, IOptions<StampOptions> stamp, IOptions<RenewalRunOptions> options, ILogger<RenewalRunWorkerRunner> logger)
{
    public const string ActorId = "pol-renewal-run-worker";

    /// <summary>The service role of the worker: it holds <c>pty.Party.revealP2</c> (rating reads the birth date) and nothing else.</summary>
    public const string ServiceRole = "Service.RenewalRun";

    /// <summary>Runs one pass over the RUNNING runs of the stamp's legal entity; returns how many runs finished.</summary>
    public async Task<int> RunAsync(int? maxChunks, CancellationToken cancellationToken)
    {
        var legalEntity = stamp.Value.LegalEntity is { Length: > 0 } code
            ? LegalEntityCode.Parse(code)
            : throw new InvalidOperationException("Stamp:LegalEntity is not configured.");
        var jurisdiction = Jurisdiction.Parse(stamp.Value.Country ?? throw new InvalidOperationException("Stamp:Country is not configured."));
        var now = clock.Now;

        IReadOnlyList<Guid> running;
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await PrepareAsync(scope.ServiceProvider, legalEntity, jurisdiction, cancellationToken).ConfigureAwait(false);
            var db = scope.ServiceProvider.GetRequiredService<PolicyDbContext>();
            var legalEntityId = scope.ServiceProvider.GetRequiredService<ILegalEntityDirectory>().Resolve(legalEntity);
            running = await db.RenewalRuns.AsNoTracking()
                .Where(r => r.LegalEntityId == legalEntityId && r.Status == RenewalRunCodes.Running && (r.LeaseUntil == null || r.LeaseUntil < now))
                .OrderBy(r => r.CreatedAt).Select(r => r.RunId).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var finished = 0;
        foreach (var id in running)
        {
            try
            {
                var inner = scopes.CreateAsyncScope();
                await using (inner.ConfigureAwait(false))
                {
                    await PrepareAsync(inner.ServiceProvider, legalEntity, jurisdiction, cancellationToken).ConfigureAwait(false);
                    if (await ProcessAsync(inner.ServiceProvider, legalEntity, jurisdiction, id, maxChunks, cancellationToken).ConfigureAwait(false))
                    {
                        finished++;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // TODO(hardening): a run that fails every pass is retried at every lease expiry; add a poison limit.
                LogRunFailed(logger, id, ex);
            }
        }

        return finished;
    }

    private async Task<bool> ProcessAsync(IServiceProvider services, LegalEntityCode legalEntity, Jurisdiction jurisdiction, Guid runId, int? maxChunks, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<PolicyDbContext>();
        var settings = options.Value;
        if (!await ClaimAsync(db, runId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var chunks = 0;
        while (true)
        {
            db.ChangeTracker.Clear();
            var run = await db.RenewalRuns.AsNoTracking().SingleOrDefaultAsync(r => r.RunId == runId, cancellationToken).ConfigureAwait(false);
            if (run is null || run.Status != RenewalRunCodes.Running || (maxChunks is { } max && chunks >= max))
            {
                return false;
            }

            var chunk = await db.RenewalRunItems.AsNoTracking().Where(i => i.RunId == runId && i.Outcome == RenewalRunCodes.Pending)
                .OrderBy(i => i.Seq).Take(Math.Max(1, settings.ChunkSize)).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (chunk.Count == 0)
            {
                await FinishAsync(services, db, runId, cancellationToken).ConfigureAwait(false);
                return true;
            }

            foreach (var item in chunk)
            {
                var result = await RunItemAsync(legalEntity, jurisdiction, run, item, cancellationToken).ConfigureAwait(false);
                await SaveItemAsync(db, item.ItemId, result, cancellationToken).ConfigureAwait(false);
            }

            var last = chunk[^1].Seq;
            await UpdateRunAsync(db, runId, r =>
            {
                r.Cursor = Math.Max(r.Cursor, last);
                r.LeaseUntil = clock.Now.Plus(TimeSpan.FromSeconds(settings.LeaseSeconds));
                r.UpdatedAt = clock.Now;
            }, requireRunning: false, cancellationToken).ConfigureAwait(false);
            chunks++;
        }
    }

    private async Task<RenewalItemResult> RunItemAsync(LegalEntityCode legalEntity, Jurisdiction jurisdiction, RenewalRunRow run, RenewalRunItemRow item, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await PrepareAsync(scope.ServiceProvider, legalEntity, jurisdiction, cancellationToken).ConfigureAwait(false);
            return await scope.ServiceProvider.GetRequiredService<RenewalRunItemProcessor>().ProcessAsync(run, item, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SaveItemAsync(PolicyDbContext db, Guid itemId, RenewalItemResult result, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var row = await db.RenewalRunItems.SingleAsync(i => i.ItemId == itemId, cancellationToken).ConfigureAwait(false);
        row.Outcome = result.Outcome;
        row.ErrorCode = result.ErrorCode;
        row.Detail = result.Detail;
        row.JobId = result.JobId ?? row.JobId;
        row.Attempts++;
        row.UpdatedAt = clock.Now;
        row.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    /// <summary>Takes the lease of a RUNNING run whose lease is free or expired. The record version arbitrates two claimants.</summary>
    private async Task<bool> ClaimAsync(PolicyDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        var now = clock.Now;
        var lease = now.Plus(TimeSpan.FromSeconds(options.Value.LeaseSeconds));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            db.ChangeTracker.Clear();
            var run = await db.RenewalRuns.SingleOrDefaultAsync(r => r.RunId == runId, cancellationToken).ConfigureAwait(false);
            if (run is null || run.Status != RenewalRunCodes.Running || (run.LeaseUntil is { } until && until > now))
            {
                return false;
            }

            run.LeaseUntil = lease;
            run.UpdatedAt = now;
            run.RecordVersion++;
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Somebody else changed the run (a pause, or another claimant): look again.
            }
        }

        return false;
    }

    /// <summary>Completes the run and publishes <c>RenewalRunCompleted</c> in one transaction.</summary>
    private async Task FinishAsync(IServiceProvider services, PolicyDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        var session = services.GetRequiredService<DbSession>();
        var events = services.GetRequiredService<IEventPublisher>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            db.ChangeTracker.Clear();
            var transaction = await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var run = await db.RenewalRuns.SingleOrDefaultAsync(r => r.RunId == runId, cancellationToken).ConfigureAwait(false);
                if (run is null || run.Status != RenewalRunCodes.Running)
                {
                    return;
                }

                var grouped = await db.RenewalRunItems.AsNoTracking().Where(i => i.RunId == runId).GroupBy(i => i.Outcome)
                    .Select(g => new { Outcome = g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
                var counts = RenewalRunCodes.Outcomes.ToDictionary(o => o, o => grouped.FirstOrDefault(g => g.Outcome == o)?.Count ?? 0);
                var now = clock.Now;
                run.Status = counts[RenewalRunCodes.Failed] > 0 ? RenewalRunCodes.CompletedWithErrors : RenewalRunCodes.Completed;
                run.LeaseUntil = null;
                run.FinishedAt = now;
                run.UpdatedAt = now;
                run.RecordVersion++;
                if (!run.DryRun)
                {
                    events.Publish(new OutgoingEvent(
                        EventDescriptor.From(RenewalRunCompletedV1.Descriptor), "LegalEntity", run.LegalEntityId.Value.ToString("D"),
                        new RenewalRunCompletedV1 { RunId = runId, Counts = counts },
                        BusinessKeys.Empty.With("runId", runId.ToString("D")))
                    {
                        OccurredAt = now,
                    });
                }

                try
                {
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (DbUpdateConcurrencyException)
                {
                    // A pause or retry changed the run between the read and the write: look again.
                }
            }
        }

        throw new InvalidOperationException("The renewal run could not be completed: it keeps changing concurrently.");
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Renewal run {RunId} failed in the worker; it is picked up again when its lease expires.")]
    private static partial void LogRunFailed(ILogger logger, Guid runId, Exception exception);

    private static async Task UpdateRunAsync(PolicyDbContext db, Guid runId, Action<RenewalRunRow> change, bool requireRunning, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            db.ChangeTracker.Clear();
            var run = await db.RenewalRuns.SingleOrDefaultAsync(r => r.RunId == runId, cancellationToken).ConfigureAwait(false);
            if (run is null || (requireRunning && run.Status != RenewalRunCodes.Running))
            {
                return;
            }

            change(run);
            run.RecordVersion++;
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Re-read and apply again.
            }
        }

        throw new InvalidOperationException("The renewal run could not be updated: it keeps changing concurrently.");
    }

    private static async Task PrepareAsync(IServiceProvider services, LegalEntityCode legalEntity, Jurisdiction jurisdiction, CancellationToken cancellationToken)
    {
        var context = services.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.Service(ActorId);
        context.Roles = [ServiceRole];
        context.LegalEntity = legalEntity;
        context.Jurisdiction = jurisdiction;
        context.Origin = EventOrigin.Live;
        context.ConfigurationHash = await services.GetRequiredService<IConfigurationResolver>().CurrentHashAsync(null, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The hosted job (worker role only): runs <see cref="RenewalRunWorkerRunner"/> every <see cref="RenewalRunOptions.PollSeconds"/> seconds.</summary>
internal sealed partial class RenewalRunSweeper(RenewalRunWorkerRunner runner, IOptions<RenewalRunOptions> options, ILogger<RenewalRunSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.PollSeconds <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollSeconds));
        try
        {
            do
            {
                try
                {
                    await runner.RunAsync(null, stoppingToken).ConfigureAwait(false);
                    CoreInsMetrics.SweeperSucceeded("renewal-run");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogRunFailed(logger, ex);
                    CoreInsMetrics.SweeperFailed("renewal-run");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The renewal run pass failed; it runs again at the next tick.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
