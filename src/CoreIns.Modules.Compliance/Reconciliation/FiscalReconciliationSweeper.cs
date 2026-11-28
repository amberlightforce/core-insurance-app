using CoreIns.Platform.Commands;
using CoreIns.Platform.Configuration;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Telemetry;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Compliance.Reconciliation;

/// <summary>Options of the nightly fiscal reconciliation (<c>Compliance:FiscalReconciliation</c>).</summary>
internal sealed class FiscalReconciliationOptions
{
    public const string Section = "Compliance:FiscalReconciliation";

    /// <summary>Switch of the hosted nightly run (worker role only). The run command stays available either way.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Minutes between runs (1440 = nightly).</summary>
    public int IntervalMinutes { get; set; } = 1440;
}

/// <summary>One scheduled pass: runs the reconciliation for the stamp's legal entity as the service actor <c>cmp-fiscal-reconciler</c>.</summary>
internal sealed partial class FiscalReconciliationRunner(
    IServiceScopeFactory scopes, IOptions<StampOptions> stamp, ILogger<FiscalReconciliationRunner> logger)
{
    public const string ActorId = "cmp-fiscal-reconciler";

    /// <summary>Runs one pass; returns the number of open breaks after it.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var legalEntity = stamp.Value.LegalEntity is { Length: > 0 } code
            ? LegalEntityCode.Parse(code)
            : throw new InvalidOperationException("Stamp:LegalEntity is not configured.");
        var jurisdiction = Jurisdiction.Parse(stamp.Value.Country ?? throw new InvalidOperationException("Stamp:Country is not configured."));
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var services = scope.ServiceProvider;
            var context = services.GetRequiredService<RequestContext>();
            context.Actor = ActorRef.Service(ActorId);
            context.LegalEntity = legalEntity;
            context.Jurisdiction = jurisdiction;
            context.Origin = EventOrigin.Live;
            context.ConfigurationHash = await services.GetRequiredService<IConfigurationResolver>().CurrentHashAsync(null, cancellationToken).ConfigureAwait(false);
            var handler = services.GetRequiredService<ICommandHandler<SweepFiscalReconciliation, int>>();
            var result = await handler.HandleAsync(new SweepFiscalReconciliation(), cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                LogFailed(logger, result.Error!.ToString());
                return 0;
            }

            return result.Value;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The fiscal reconciliation failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}

/// <summary>The hosted job (worker role only, switchable with <see cref="FiscalReconciliationOptions.Enabled"/>).</summary>
internal sealed partial class FiscalReconciliationSweeper(
    FiscalReconciliationRunner runner, IOptions<FiscalReconciliationOptions> options, ILogger<FiscalReconciliationSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.IntervalMinutes)));
        do
        {
            try
            {
                await runner.RunAsync(stoppingToken).ConfigureAwait(false);
                CoreInsMetrics.SweeperSucceeded("fiscal-reconciliation");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRunFailed(logger, ex);
                CoreInsMetrics.SweeperFailed("fiscal-reconciliation");
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

    [LoggerMessage(Level = LogLevel.Error, Message = "The fiscal reconciliation run failed; it runs again at the next tick.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
