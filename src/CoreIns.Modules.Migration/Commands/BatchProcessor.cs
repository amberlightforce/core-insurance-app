using CoreIns.Modules.Migration.Domain;
using CoreIns.Modules.Migration.Persistence;
using CoreIns.Platform.Configuration;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Telemetry;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Migration.Commands;

/// <summary>Settings of the background batch run (<c>Migration:Batches</c>).</summary>
internal sealed class MigrationBatchOptions
{
    public const string Section = "Migration:Batches";

    /// <summary>Items per chunk; the cursor and counters are saved after each chunk and pause is checked between chunks.</summary>
    public int ChunkSize { get; set; } = 100;

    /// <summary>A batch of at most this many items is finished inside the approve request; a larger one is left to the worker.</summary>
    public int InlineRunMaxItems { get; set; } = 100;

    /// <summary>Seconds between worker polls.</summary>
    public int PollSeconds { get; set; } = 5;

    /// <summary>Seconds a worker owns a run without renewing; renewed after every chunk.</summary>
    public int LeaseSeconds { get; set; } = 60;
}

/// <summary>
/// Runs a RUNNING batch in chunks (BO-MIG-01). Only PENDING items are taken, in seq order, so a resumed run (after pause or a
/// crash) continues where it stopped and an item is never run twice; the importers' xref idempotency covers a chunk lost to a crash.
/// A worker owns a batch through a lease that is renewed per chunk.
/// </summary>
internal sealed class BatchProcessor(MigrationDbContext db, BatchRunner runner, IClock clock, IOptions<MigrationBatchOptions> options)
{
    /// <summary>Processes the batch until it is finished, paused, or <paramref name="maxChunks"/> chunks are done; true when finished.</summary>
    public async Task<bool> ProcessAsync(Guid batchId, int? maxChunks, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!await ClaimAsync(batchId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var chunks = 0;
        while (true)
        {
            db.ChangeTracker.Clear();
            var batch = await db.Batches.AsNoTracking().SingleOrDefaultAsync(b => b.BatchId == batchId, cancellationToken).ConfigureAwait(false);
            if (batch is null || batch.Status != BatchStatus.Running || (maxChunks is { } max && chunks >= max))
            {
                return false;
            }

            var chunk = await db.Items.Where(i => i.BatchId == batchId && i.Outcome == ItemOutcome.Pending)
                .OrderBy(i => i.Seq).Take(Math.Max(1, settings.ChunkSize)).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (chunk.Count == 0)
            {
                await FinishAsync(batchId, cancellationToken).ConfigureAwait(false);
                return true;
            }

            await runner.RunAsync(batch, chunk, dryRun: false, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var last = chunk[^1].Seq;
            await UpdateAsync(batchId, b =>
            {
                b.Cursor = Math.Max(b.Cursor, last);
                b.LeaseUntil = clock.Now.Plus(TimeSpan.FromSeconds(settings.LeaseSeconds));
                b.UpdatedAt = clock.Now;
            }, requireRunning: false, cancellationToken).ConfigureAwait(false);
            await RefreshCountersAsync(batchId, cancellationToken).ConfigureAwait(false);
            chunks++;
        }
    }

    /// <summary>Takes the lease of a RUNNING batch whose lease is free or expired. The record version arbitrates two claimants.</summary>
    private async Task<bool> ClaimAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var now = clock.Now;
        var lease = now.Plus(TimeSpan.FromSeconds(options.Value.LeaseSeconds));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            db.ChangeTracker.Clear();
            var batch = await db.Batches.SingleOrDefaultAsync(b => b.BatchId == batchId, cancellationToken).ConfigureAwait(false);
            if (batch is null || batch.Status != BatchStatus.Running || (batch.LeaseUntil is { } until && until > now))
            {
                return false;
            }

            batch.LeaseUntil = lease;
            batch.UpdatedAt = now;
            batch.RecordVersion++;
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Somebody else changed the batch (a pause, or another claimant): look again.
            }
        }

        return false;
    }

    private async Task RefreshCountersAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var processed = await db.Items.CountAsync(i => i.BatchId == batchId && i.Outcome != ItemOutcome.Pending, cancellationToken).ConfigureAwait(false);
        var failed = await db.Items.CountAsync(i => i.BatchId == batchId && i.Outcome == ItemOutcome.Failed, cancellationToken).ConfigureAwait(false);
        await UpdateAsync(batchId, b =>
        {
            b.ProcessedCount = processed;
            b.FailedCount = failed;
        }, requireRunning: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishAsync(Guid batchId, CancellationToken cancellationToken)
    {
        await RefreshCountersAsync(batchId, cancellationToken).ConfigureAwait(false);
        await UpdateAsync(batchId, b =>
        {
            b.Status = b.FailedCount > 0 ? BatchStatus.CompletedWithErrors : BatchStatus.Completed;
            b.LeaseUntil = null;
            b.UpdatedAt = clock.Now;
            b.RecordVersion++;
        }, requireRunning: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies a change to a fresh copy of the batch; a concurrent change (a pause) makes it retry, and a batch that is no longer running is left alone when required.</summary>
    private async Task UpdateAsync(Guid batchId, Action<BatchRow> change, bool requireRunning, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            db.ChangeTracker.Clear();
            var batch = await db.Batches.SingleOrDefaultAsync(b => b.BatchId == batchId, cancellationToken).ConfigureAwait(false);
            if (batch is null || (requireRunning && batch.Status != BatchStatus.Running))
            {
                return;
            }

            change(batch);
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

        throw new InvalidOperationException("The batch could not be updated: it keeps changing concurrently.");
    }
}

/// <summary>
/// One worker pass (BO-MIG-01): finds the RUNNING batches of the stamp's legal entity and runs each in its own scope as the service
/// actor <c>mig-batch-worker</c>. Used by <see cref="BatchWorker"/> and directly by tests.
/// </summary>
internal sealed partial class BatchWorkerRunner(IServiceScopeFactory scopes, IClock clock, IOptions<StampOptions> stamp, ILogger<BatchWorkerRunner> logger)
{
    public const string ActorId = "mig-batch-worker";

    /// <summary>Runs one pass; returns how many batches finished.</summary>
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
            var db = scope.ServiceProvider.GetRequiredService<MigrationDbContext>();
            var legalEntityId = scope.ServiceProvider.GetRequiredService<ILegalEntityDirectory>().Resolve(legalEntity);
            running = await db.Batches.AsNoTracking()
                .Where(b => b.LegalEntityId == legalEntityId && b.Status == BatchStatus.Running && (b.LeaseUntil == null || b.LeaseUntil < now))
                .OrderBy(b => b.UpdatedAt).Select(b => b.BatchId).ToListAsync(cancellationToken).ConfigureAwait(false);
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
                    var processor = inner.ServiceProvider.GetRequiredService<BatchProcessor>();
                    if (await processor.ProcessAsync(id, maxChunks, cancellationToken).ConfigureAwait(false))
                    {
                        finished++;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // TODO(hardening): a batch that fails every pass (an importer that throws) is retried at every lease expiry; add a poison limit.
                LogFailed(logger, id, ex);
            }
        }

        return finished;
    }

    private static async Task PrepareAsync(IServiceProvider services, LegalEntityCode legalEntity, Jurisdiction jurisdiction, CancellationToken cancellationToken)
    {
        var context = services.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.Service(ActorId);
        context.LegalEntity = legalEntity;
        context.Jurisdiction = jurisdiction;
        context.Origin = EventOrigin.Live;
        context.ConfigurationHash = await services.GetRequiredService<IConfigurationResolver>().CurrentHashAsync(null, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Migration batch {BatchId} failed in the worker; it is picked up again when its lease expires.")]
    private static partial void LogFailed(ILogger logger, Guid batchId, Exception exception);
}

/// <summary>The hosted job (worker role only): runs <see cref="BatchWorkerRunner"/> every <see cref="MigrationBatchOptions.PollSeconds"/> seconds.</summary>
internal sealed partial class BatchWorker(BatchWorkerRunner runner, IOptions<MigrationBatchOptions> options, ILogger<BatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds)));
        do
        {
            try
            {
                await runner.RunAsync(null, stoppingToken).ConfigureAwait(false);
                CoreInsMetrics.SweeperSucceeded("migration-batch");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRunFailed(logger, ex);
                CoreInsMetrics.SweeperFailed("migration-batch");
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The migration batch worker pass failed; it runs again at the next tick.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
