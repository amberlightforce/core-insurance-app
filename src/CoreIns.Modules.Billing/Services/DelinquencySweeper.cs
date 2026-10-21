using CoreIns.Modules.Billing.Commands;
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

namespace CoreIns.Modules.Billing.Services;

/// <summary>
/// One overdue sweep: runs <c>bil.Delinquency.sweep</c> once for the stamp's legal entity as the service actor
/// <c>bil-delinquency-sweeper</c>, in its own scope. The command is idempotent (an invoice with an open case is skipped), so a
/// repeated or concurrent pass is safe.
/// </summary>
internal sealed partial class DelinquencySweepRunner(
    IServiceScopeFactory scopes,
    IOptions<StampOptions> stamp,
    ILogger<DelinquencySweepRunner> logger)
{
    public const string ActorId = "bil-delinquency-sweeper";

    /// <summary>Runs one pass; returns the number of cases opened. Fails closed when the stamp is not configured.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        // TODO(hardening): one pass per legal entity once a catalogue of entities is available; today the stamp names the single one.
        var legalEntity = stamp.Value.LegalEntity is { Length: > 0 } code
            ? LegalEntityCode.Parse(code)
            : throw new InvalidOperationException("Stamp:LegalEntity is not configured.");
        var jurisdiction = Jurisdiction.Parse(stamp.Value.Country ?? throw new InvalidOperationException("Stamp:Country is not configured."));

        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
            context.Actor = ActorRef.Service(ActorId);
            context.LegalEntity = legalEntity;
            context.Jurisdiction = jurisdiction;
            context.Origin = EventOrigin.Live;
            context.ConfigurationHash = await scope.ServiceProvider.GetRequiredService<IConfigurationResolver>()
                .CurrentHashAsync(null, cancellationToken).ConfigureAwait(false);

            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<SweepDelinquency, int>>();
            var result = await handler.HandleAsync(new SweepDelinquency(), cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                LogFailed(logger, result.Error!.ToString());
                return 0;
            }

            if (result.Value > 0)
            {
                LogOpened(logger, result.Value);
            }

            return result.Value;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The delinquency sweep failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The delinquency sweep opened {Count} case(s).")]
    private static partial void LogOpened(ILogger logger, int count);
}

/// <summary>The hosted job: runs <see cref="DelinquencySweepRunner"/> every <see cref="BillingOptions.DelinquencySweepIntervalSeconds"/> seconds (worker role only).</summary>
internal sealed partial class DelinquencySweeper(DelinquencySweepRunner runner, IOptions<BillingOptions> options, ILogger<DelinquencySweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.DelinquencySweepIntervalSeconds)));
        do
        {
            try
            {
                await runner.RunAsync(stoppingToken).ConfigureAwait(false);
                CoreInsMetrics.SweeperSucceeded("delinquency");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRunFailed(logger, ex);
                CoreInsMetrics.SweeperFailed("delinquency");
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

    [LoggerMessage(Level = LogLevel.Error, Message = "The delinquency sweep failed; it runs again at the next tick.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
