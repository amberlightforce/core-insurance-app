using CoreIns.Modules.Compliance.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Configuration;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Compliance.Clocks;

/// <summary>
/// One sweep (REQ-CMP-097): finds the clocks whose warning or due instant has passed on the platform clock and runs
/// <see cref="SweepClock"/> for each in its own scope and transaction, so one failure never blocks the others. It runs as the
/// service actor <c>cmp-clock-sweeper</c> in the stamp's legal entity. At-least-once safe: the step re-checks the locked row.
/// </summary>
internal sealed partial class ClockSweepRunner(
    IServiceScopeFactory scopes,
    IClock clock,
    IOptions<StampOptions> stamp,
    ILogger<ClockSweepRunner> logger)
{
    public const string ActorId = "cmp-clock-sweeper";

    private const int BatchSize = 200;

    /// <summary>Runs one pass; returns how many clocks changed state.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var legalEntity = stamp.Value.LegalEntity is { Length: > 0 } code
            ? LegalEntityCode.Parse(code)
            : throw new InvalidOperationException("Stamp:LegalEntity is not configured.");
        var jurisdiction = Jurisdiction.Parse(stamp.Value.Country ?? throw new InvalidOperationException("Stamp:Country is not configured."));
        var now = clock.Now;

        IReadOnlyList<StatutoryClockId> due;
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await PrepareAsync(scope.ServiceProvider, legalEntity, jurisdiction, cancellationToken).ConfigureAwait(false);
            var db = scope.ServiceProvider.GetRequiredService<ComplianceDbContext>();
            var legalEntityId = scope.ServiceProvider.GetRequiredService<ILegalEntityDirectory>().Resolve(legalEntity);
            due = await db.Clocks.AsNoTracking()
                .Where(c => c.LegalEntityId == legalEntityId
                    && (((c.Status == ClockStatus.Running || c.Status == ClockStatus.Warned) && c.Deadline < now)
                        || (c.Status == ClockStatus.Running && c.WarnAt != null && c.WarnAt <= now)))
                .OrderBy(c => c.Deadline).Select(c => c.ClockId).Take(BatchSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var changed = 0;
        foreach (var id in due)
        {
            try
            {
                var inner = scopes.CreateAsyncScope();
                await using (inner.ConfigureAwait(false))
                {
                    await PrepareAsync(inner.ServiceProvider, legalEntity, jurisdiction, cancellationToken).ConfigureAwait(false);
                    var handler = inner.ServiceProvider.GetRequiredService<ICommandHandler<SweepClock, string>>();
                    var result = await handler.HandleAsync(new SweepClock(id), cancellationToken).ConfigureAwait(false);
                    if (result.IsSuccess && result.Value != SweepClockHandler.NotDue)
                    {
                        changed++;
                    }
                    else if (result.IsFailure)
                    {
                        LogFailed(logger, id.Value, result.Error!.ToString());
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, id.Value, ex.GetType().Name);
            }
        }

        return changed;
    }

    private static async Task PrepareAsync(IServiceProvider services, LegalEntityCode legalEntity, Jurisdiction jurisdiction, CancellationToken cancellationToken)
    {
        var context = services.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.Service(ActorId);
        context.LegalEntity = legalEntity;
        context.Jurisdiction = jurisdiction;
        context.Origin = EventOrigin.Live;

        // Events need the configuration hash in force (the HTTP middleware pins it for a request; a job pins it here).
        context.ConfigurationHash = await services.GetRequiredService<IConfigurationResolver>().CurrentHashAsync(null, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Clock sweep failed for clock {ClockId}: {Reason}")]
    private static partial void LogFailed(ILogger logger, Guid clockId, string reason);
}

/// <summary>The hosted job: runs <see cref="ClockSweepRunner"/> every <see cref="ClockOptions.SweepIntervalSeconds"/> seconds (worker role only).</summary>
internal sealed partial class ClockSweeper(ClockSweepRunner runner, IOptions<ClockOptions> options, ILogger<ClockSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.SweepIntervalSeconds)));
        do
        {
            try
            {
                await runner.RunAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRunFailed(logger, ex);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "The clock sweep failed; it runs again at the next tick.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
