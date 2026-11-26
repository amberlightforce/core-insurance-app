using CoreIns.Modules.Work.Commands;
using CoreIns.Modules.Work.Persistence;
using CoreIns.Platform;
using CoreIns.Platform.Configuration;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Telemetry;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Work.Jobs;

/// <summary>Settings of the SLA sweep.</summary>
internal sealed class WorkOptions
{
    public const string Section = "Work";

    /// <summary>Seconds between SLA sweeps. 0 turns the hosted sweep off (tests call the runner).</summary>
    public int SlaSweepIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// One SLA sweep: every open activity whose target has passed and was not yet marked gets <c>SLABreached</c> published, exactly once (the row
/// is marked in the same transaction), each in its own scope as the service actor <c>wrk-sla-sweeper</c>.
/// </summary>
internal sealed partial class SlaSweepRunner(IServiceScopeFactory scopes, IClock clock, IOptions<StampOptions> stamp, ILogger<SlaSweepRunner> logger)
{
    public const string ActorId = "wrk-sla-sweeper";

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var legalEntity = stamp.Value.LegalEntity is { Length: > 0 } code ? LegalEntityCode.Parse(code) : throw new InvalidOperationException("Stamp:LegalEntity is not configured.");
        var jurisdiction = Jurisdiction.Parse(stamp.Value.Country ?? throw new InvalidOperationException("Stamp:Country is not configured."));
        var now = clock.Now;
        IReadOnlyList<Guid> due;
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
            var legalEntityId = scope.ServiceProvider.GetRequiredService<ILegalEntityDirectory>().Resolve(legalEntity);
            due = await db.Activities.AsNoTracking()
                .Where(a => a.LegalEntityId == legalEntityId && a.State == ActivityRow.Open && a.BreachedAt == null && a.TargetAt != null && a.TargetAt < now)
                .OrderBy(a => a.TargetAt).Select(a => a.ActivityId).Take(200).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var done = 0;
        foreach (var id in due)
        {
            try
            {
                var inner = scopes.CreateAsyncScope();
                await using (inner.ConfigureAwait(false))
                {
                    var services = inner.ServiceProvider;
                    var context = services.GetRequiredService<RequestContext>();
                    context.Actor = ActorRef.Service(ActorId);
                    context.LegalEntity = legalEntity;
                    context.Jurisdiction = jurisdiction;
                    context.Origin = EventOrigin.Live;
                    context.ConfigurationHash = await services.GetRequiredService<IConfigurationResolver>().CurrentHashAsync(null, cancellationToken).ConfigureAwait(false);
                    var session = services.GetRequiredService<DbSession>();
                    var transaction = await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                    await using (transaction.ConfigureAwait(false))
                    {
                        if (await services.GetRequiredService<ActivityEngine>().BreachAsync(id, cancellationToken).ConfigureAwait(false))
                        {
                            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                            done++;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, id, ex.GetType().Name);
            }
        }

        return done;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The SLA breach of activity {ActivityId} failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, Guid activityId, string reason);
}

/// <summary>The hosted job (worker role) that runs <see cref="SlaSweepRunner"/> every <see cref="WorkOptions.SlaSweepIntervalSeconds"/> seconds.</summary>
internal sealed partial class SlaSweeper(SlaSweepRunner runner, IOptions<WorkOptions> options, ILogger<SlaSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.SlaSweepIntervalSeconds <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.SlaSweepIntervalSeconds));
        try
        {
            do
            {
                try
                {
                    await runner.RunAsync(stoppingToken).ConfigureAwait(false);
                    CoreInsMetrics.SweeperSucceeded("work-sla");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogRunFailed(logger, ex);
                    CoreInsMetrics.SweeperFailed("work-sla");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The SLA sweep failed; it runs again at the next tick.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
