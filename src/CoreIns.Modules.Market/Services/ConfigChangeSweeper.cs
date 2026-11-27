using CoreIns.Modules.Market.Commands;
using CoreIns.Modules.Market.Persistence;
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

namespace CoreIns.Modules.Market.Services;

/// <summary>
/// One sweep of the configuration-change due-date job (BO-MKT-02): finds the SCHEDULED requests whose effective instant has come and applies each in its
/// own scope and transaction as the service actor <c>mkt-config-sweeper</c>. At-least-once safe: the step re-checks the row under the state lock.
/// </summary>
internal sealed partial class ConfigChangeSweepRunner(IServiceScopeFactory scopes, IClock clock, IOptions<StampOptions> stamp, ILogger<ConfigChangeSweepRunner> logger)
{
    private const int BatchSize = 50;

    /// <summary>Runs one pass; returns how many requests were applied.</summary>
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
            var db = scope.ServiceProvider.GetRequiredService<MarketDbContext>();
            var now = clock.Now;
            due = await db.ConfigChangeRequests.AsNoTracking()
                .Where(r => r.Status == ConfigChangeSupport.Scheduled && r.EffectiveAt <= now
                    && db.LegalEntities.Any(e => e.LegalEntityId == r.LegalEntityId && e.Code == legalEntity.Value))
                .OrderBy(r => r.EffectiveAt).Select(r => r.Id).Take(BatchSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var applied = 0;
        foreach (var id in due)
        {
            try
            {
                var inner = scopes.CreateAsyncScope();
                await using (inner.ConfigureAwait(false))
                {
                    await PrepareAsync(inner.ServiceProvider, legalEntity, jurisdiction, cancellationToken).ConfigureAwait(false);
                    var handler = inner.ServiceProvider.GetRequiredService<ICommandHandler<ApplyDueConfigChange, string>>();
                    var result = await handler.HandleAsync(new ApplyDueConfigChange(id), cancellationToken).ConfigureAwait(false);
                    if (result.IsFailure)
                    {
                        LogFailed(logger, id, result.Error!.ToString());
                    }
                    else if (result.Value == ApplyDueConfigChangeHandler.Applied)
                    {
                        applied++;
                    }
                    else if (result.Value == ApplyDueConfigChangeHandler.Failed)
                    {
                        LogFailed(logger, id, "closed as REJECTED, see its decision reason");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, id, ex.GetType().Name);
            }
        }

        return applied;
    }

    private static async Task PrepareAsync(IServiceProvider services, LegalEntityCode legalEntity, Jurisdiction jurisdiction, CancellationToken cancellationToken)
    {
        var context = services.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.Service(ConfigChangeSupport.SweeperActor);
        context.LegalEntity = legalEntity;
        context.Jurisdiction = jurisdiction;
        context.Origin = EventOrigin.Live;
        context.ConfigurationHash = await services.GetRequiredService<IConfigurationResolver>().CurrentHashAsync(null, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Applying the scheduled configuration change {RequestId} failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, Guid requestId, string reason);
}

/// <summary>The hosted job: runs <see cref="ConfigChangeSweepRunner"/> once a minute (worker role only).</summary>
internal sealed partial class ConfigChangeSweeper(ConfigChangeSweepRunner runner, ILogger<ConfigChangeSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        do
        {
            try
            {
                await runner.RunAsync(stoppingToken).ConfigureAwait(false);
                CoreInsMetrics.SweeperSucceeded("mkt-config-change");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRunFailed(logger, ex);
                CoreInsMetrics.SweeperFailed("mkt-config-change");
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

    [LoggerMessage(Level = LogLevel.Error, Message = "The configuration change sweep failed; it runs again at the next tick.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
