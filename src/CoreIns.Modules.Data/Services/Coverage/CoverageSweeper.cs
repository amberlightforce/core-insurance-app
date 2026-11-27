using CoreIns.Platform.Events;
using CoreIns.Platform.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Data.Services.Coverage;

/// <summary>Settings of the daily coverage sweep (<c>Data:CoverageSweep</c>).</summary>
internal sealed class CoverageSweepOptions
{
    public const string Section = "Data:CoverageSweep";

    /// <summary>Seconds between sweeps (default one day).</summary>
    public int SweepIntervalSeconds { get; set; } = 86400;
}

/// <summary>
/// One control-total sweep (BO-DAT-05): reads the coverage of the stamp's legal entity and logs every gap (a handled event neither projected nor
/// quarantined) as a warning, so the log pipeline and alerting see it. Read-only. Returns the gap count.
/// TODO(hardening): raise a DataReconciliationBreakRaised event and persist the sweep result so the quality page can show history.
/// </summary>
internal sealed partial class CoverageSweepRunner(IServiceScopeFactory scopes, IOptions<StampOptions> stamp, ILogger<CoverageSweepRunner> logger)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var legalEntity = stamp.Value.LegalEntity is { Length: > 0 } code ? code : throw new InvalidOperationException("Stamp:LegalEntity is not configured.");
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var result = await scope.ServiceProvider.GetRequiredService<CoverageReader>().StatusAsync(legalEntity, null, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                throw new InvalidOperationException(result.Error!.ToString());
            }

            foreach (var line in result.Value.Tables!.Where(t => t.Gap > 0))
            {
                LogGap(logger, line.Name, line.Period, line.Gap, line.Received);
            }

            return result.Value.GapCount ?? 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Coverage gap in {Table} for {Period}: {Gap} of {Received} accepted events are neither projected nor quarantined.")]
    private static partial void LogGap(ILogger logger, string table, string period, int gap, int received);
}

/// <summary>The hosted job: runs <see cref="CoverageSweepRunner"/> every <see cref="CoverageSweepOptions.SweepIntervalSeconds"/> (worker role only).</summary>
internal sealed partial class CoverageSweeper(CoverageSweepRunner runner, IOptions<CoverageSweepOptions> options, ILogger<CoverageSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.SweepIntervalSeconds)));
        do
        {
            try
            {
                await runner.RunAsync(stoppingToken).ConfigureAwait(false);
                CoreInsMetrics.SweeperSucceeded("data-coverage");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRunFailed(logger, ex);
                CoreInsMetrics.SweeperFailed("data-coverage");
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

    [LoggerMessage(Level = LogLevel.Error, Message = "The coverage sweep failed; it runs again at the next tick.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
