using CoreIns.Modules.Product.Commands;
using CoreIns.Modules.Product.Domain;
using CoreIns.Modules.Product.Persistence;
using CoreIns.Platform.Commands;
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

namespace CoreIns.Modules.Product.Services;

/// <summary>
/// One sweep of the release due-date job (BO-PFC-04, REQ-PFC-212): finds the SCHEDULED releases whose activation date has come and
/// publishes each in its own scope and transaction as the service actor <c>pfc-release-sweeper</c>; publishing creates the Locked version
/// and emits <c>ProductVersionPublishedV1</c>. At-least-once safe: the step re-checks the row under lock.
/// </summary>
internal sealed partial class ReleaseSweepRunner(
    IServiceScopeFactory scopes,
    IClock clock,
    IOptions<StampOptions> stamp,
    ILogger<ReleaseSweepRunner> logger)
{
    public const string ActorId = "pfc-release-sweeper";

    private const int BatchSize = 50;

    /// <summary>Runs one pass; returns how many releases were published.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        // TODO(hardening): one pass per legal entity once a catalogue of entities is available; today the stamp names the single one.
        var legalEntity = stamp.Value.LegalEntity is { Length: > 0 } code
            ? LegalEntityCode.Parse(code)
            : throw new InvalidOperationException("Stamp:LegalEntity is not configured.");
        var jurisdiction = Jurisdiction.Parse(stamp.Value.Country ?? throw new InvalidOperationException("Stamp:Country is not configured."));

        IReadOnlyList<Guid> due;
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await PrepareAsync(scope.ServiceProvider, legalEntity, jurisdiction, cancellationToken).ConfigureAwait(false);
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var legalEntities = scope.ServiceProvider.GetRequiredService<ILegalEntityDirectory>();
            var legalEntityId = legalEntities.Resolve(legalEntity);
            var today = FallbackPlanner.DateIn(clock.Now, legalEntities.TimeZoneOf(legalEntity));
            due = await db.Releases.AsNoTracking()
                .Where(r => r.LegalEntityId == legalEntityId && r.Status == ReleaseSupport.Scheduled && r.ActivationDate <= today)
                .OrderBy(r => r.ActivationDate).Select(r => r.ReleaseId).Take(BatchSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var published = 0;
        foreach (var id in due)
        {
            try
            {
                var inner = scopes.CreateAsyncScope();
                await using (inner.ConfigureAwait(false))
                {
                    await PrepareAsync(inner.ServiceProvider, legalEntity, jurisdiction, cancellationToken).ConfigureAwait(false);
                    var handler = inner.ServiceProvider.GetRequiredService<ICommandHandler<PublishDueRelease, string>>();
                    var result = await handler.HandleAsync(new PublishDueRelease(id), cancellationToken).ConfigureAwait(false);
                    if (result.IsFailure)
                    {
                        // TODO(hardening): a release that keeps failing (for example a window overlap that appeared after scheduling) needs an alert and a dead-letter state.
                        LogFailed(logger, id, result.Error!.ToString());
                    }
                    else if (result.Value != PublishDueReleaseHandler.Skipped)
                    {
                        published++;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, id, ex.GetType().Name);
            }
        }

        return published;
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Publishing the scheduled release {ReleaseId} failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, Guid releaseId, string reason);
}

/// <summary>The hosted job: runs <see cref="ReleaseSweepRunner"/> once a minute (worker role only).</summary>
internal sealed partial class ReleaseSweeper(ReleaseSweepRunner runner, ILogger<ReleaseSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        do
        {
            try
            {
                await runner.RunAsync(stoppingToken).ConfigureAwait(false);
                CoreInsMetrics.SweeperSucceeded("pfc-release");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRunFailed(logger, ex);
                CoreInsMetrics.SweeperFailed("pfc-release");
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

    [LoggerMessage(Level = LogLevel.Error, Message = "The release sweep failed; it runs again at the next tick.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
