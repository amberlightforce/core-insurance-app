using CoreIns.Modules.Channels.Commands;
using CoreIns.Modules.Channels.Domain;
using CoreIns.Modules.Channels.Persistence;
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

namespace CoreIns.Modules.Channels.Services;

/// <summary>
/// One sweep: finds the withdrawal requests that are still CONFIRMED after the grace period (the POL handoff failed or POL was not
/// bound) and runs <see cref="ResubmitWithdrawal"/> for each in its own scope and transaction, as the service actor
/// <c>chn-withdrawal-sweeper</c>. At-least-once safe: POL is idempotent on the channel reference and the step re-checks the row.
/// </summary>
internal sealed partial class WithdrawalSweepRunner(
    IServiceScopeFactory scopes,
    IClock clock,
    IOptions<StampOptions> stamp,
    IOptions<ChannelsOptions> options,
    ILogger<WithdrawalSweepRunner> logger)
{
    public const string ActorId = "chn-withdrawal-sweeper";

    private const int BatchSize = 100;

    /// <summary>Runs one pass; returns how many requests changed state.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        // TODO(hardening): one pass per legal entity once a catalogue of entities is available; today the stamp names the single one.
        var legalEntity = stamp.Value.LegalEntity is { Length: > 0 } code
            ? LegalEntityCode.Parse(code)
            : throw new InvalidOperationException("Stamp:LegalEntity is not configured.");
        var jurisdiction = Jurisdiction.Parse(stamp.Value.Country ?? throw new InvalidOperationException("Stamp:Country is not configured."));
        var cutoff = clock.Now.Minus(TimeSpan.FromSeconds(options.Value.SweepGraceSeconds));

        IReadOnlyList<Guid> due;
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await PrepareAsync(scope.ServiceProvider, legalEntity, jurisdiction, cancellationToken).ConfigureAwait(false);
            var db = scope.ServiceProvider.GetRequiredService<ChannelsDbContext>();
            var legalEntityId = scope.ServiceProvider.GetRequiredService<ILegalEntityDirectory>().Resolve(legalEntity);
            due = await db.WithdrawalRequests.AsNoTracking()
                .Where(r => r.LegalEntityId == legalEntityId && r.Status == WithdrawalStatuses.Confirmed && r.ConfirmedAt != null && r.ConfirmedAt <= cutoff)
                .OrderBy(r => r.ConfirmedAt).Select(r => r.WithdrawalRequestId).Take(BatchSize)
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
                    var handler = inner.ServiceProvider.GetRequiredService<ICommandHandler<ResubmitWithdrawal, string>>();
                    var result = await handler.HandleAsync(new ResubmitWithdrawal(id), cancellationToken).ConfigureAwait(false);
                    if (result.IsFailure)
                    {
                        LogFailed(logger, id, result.Error!.ToString());
                    }
                    else if (result.Value is not (ResubmitWithdrawalHandler.Skipped or WithdrawalStatuses.Confirmed))
                    {
                        changed++;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, id, ex.GetType().Name);
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
        context.ConfigurationHash = await services.GetRequiredService<IConfigurationResolver>().CurrentHashAsync(null, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Withdrawal re-submit failed for request {WithdrawalRequestId}: {Reason}")]
    private static partial void LogFailed(ILogger logger, Guid withdrawalRequestId, string reason);
}

/// <summary>The hosted job: runs <see cref="WithdrawalSweepRunner"/> every <see cref="ChannelsOptions.SweepIntervalSeconds"/> seconds (worker role only).</summary>
internal sealed partial class WithdrawalSweeper(WithdrawalSweepRunner runner, IOptions<ChannelsOptions> options, ILogger<WithdrawalSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.SweepIntervalSeconds)));
        do
        {
            try
            {
                await runner.RunAsync(stoppingToken).ConfigureAwait(false);
                CoreInsMetrics.SweeperSucceeded("withdrawal");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRunFailed(logger, ex);
                CoreInsMetrics.SweeperFailed("withdrawal");
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

    [LoggerMessage(Level = LogLevel.Error, Message = "The withdrawal sweep failed; it runs again at the next tick.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
