using System.Text.Json;
using CoreIns.Modules.Policy.Commands.Cancellation.NonPayment;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Telemetry;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CancellationKind = CoreIns.Modules.Policy.Contracts.Api.CancellationKind;

namespace CoreIns.Modules.Policy.Commands.Cancellation;

/// <summary>Settings of the scheduled-cancellation sweep (BO-POL-06).</summary>
internal sealed class ScheduledCancellationOptions
{
    public const string Section = "Policy:ScheduledCancellation";

    /// <summary>Seconds between sweeps. 0 turns the hosted sweep off (tests call the runner).</summary>
    public int SweepIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// One sweep: runs every SCHEDULED cancellation whose effective time has come, once, for the configured legal entity, each in its own scope and
/// transaction, as the service actor <c>pol-cancel-sweeper</c>. The locked row is re-read inside the transaction (status and record version), so a
/// row rescinded or executed meanwhile is skipped; a concurrent rescind loses or wins cleanly through the row's concurrency token. The cancellation
/// is computed again at the effective time by <see cref="CancelPolicyHandler"/>: a permanent refusal marks the row FAILED, a transient one leaves it
/// SCHEDULED for the next sweep.
/// </summary>
internal sealed partial class ScheduledCancellationSweepRunner(IServiceScopeFactory scopes, IClock clock, IOptions<StampOptions> stamp, ILogger<ScheduledCancellationSweepRunner> logger)
{
    public const string ActorId = "pol-cancel-sweeper";

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var legalEntity = stamp.Value.LegalEntity is { Length: > 0 } code ? LegalEntityCode.Parse(code) : throw new InvalidOperationException("Stamp:LegalEntity is not configured.");
        var jurisdiction = Jurisdiction.Parse(stamp.Value.Country ?? throw new InvalidOperationException("Stamp:Country is not configured."));
        var now = clock.Now;
        IReadOnlyList<JobId> due;
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<PolicyDbContext>();
            var legalEntityId = scope.ServiceProvider.GetRequiredService<ILegalEntityDirectory>().Resolve(legalEntity);
            due = await db.ScheduledCancellations.AsNoTracking()
                .Where(n => n.LegalEntityId == legalEntityId && n.Status == ScheduledCancellationStatus.Scheduled && n.EffectiveAt <= now)
                .OrderBy(n => n.EffectiveAt).Select(n => n.ScheduledId).Take(100).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var done = 0;
        foreach (var id in due)
        {
            try
            {
                if (await RunOneAsync(id, legalEntity, jurisdiction, cancellationToken).ConfigureAwait(false))
                {
                    done++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Includes a lost race with a rescind (concurrency token): the row keeps the other writer's status.
                LogFailed(logger, id.Value, ex.GetType().Name);
            }
        }

        return done;
    }

    private async Task<bool> RunOneAsync(JobId id, LegalEntityCode legalEntity, Jurisdiction jurisdiction, CancellationToken cancellationToken)
    {
        string? permanent = null;
        var inner = scopes.CreateAsyncScope();
        await using (inner.ConfigureAwait(false))
        {
            var services = inner.ServiceProvider;
            await PrepareAsync(services, legalEntity, jurisdiction, cancellationToken).ConfigureAwait(false);
            var session = services.GetRequiredService<DbSession>();
            var transaction = await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var db = services.GetRequiredService<PolicyDbContext>();
                var row = await db.ScheduledCancellations.SingleAsync(n => n.ScheduledId == id, cancellationToken).ConfigureAwait(false);
                if (row.Status != ScheduledCancellationStatus.Scheduled)
                {
                    return false; // rescinded or executed since the due list was read
                }

                var executed = await services.GetRequiredService<ScheduledCancellationService>().ExecuteAsync(row, cancellationToken).ConfigureAwait(false);
                if (executed.IsSuccess)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                }

                LogRefused(logger, id.Value, executed.Error!.ToString());
                if (!CancellationForNonPaymentRequestedHandler.IsTransient(executed.Error!))
                {
                    permanent = executed.Error!.Code.ToString();
                }
                // The owner transaction rolls back on dispose: the cancellation handler may have written part of its work.
            }
        }

        if (permanent is not null)
        {
            // A refusal a retry cannot cure (term already cancelled, out of sequence, gate failed): park the row instead of retrying every sweep.
            var failScope = scopes.CreateAsyncScope();
            await using (failScope.ConfigureAwait(false))
            {
                var services = failScope.ServiceProvider;
                await PrepareAsync(services, legalEntity, jurisdiction, cancellationToken).ConfigureAwait(false);
                var failTransaction = await services.GetRequiredService<DbSession>().BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (failTransaction.ConfigureAwait(false))
                {
                    var db = services.GetRequiredService<PolicyDbContext>();
                    var row = await db.ScheduledCancellations.SingleAsync(n => n.ScheduledId == id, cancellationToken).ConfigureAwait(false);
                    if (row.Status == ScheduledCancellationStatus.Scheduled)
                    {
                        row.Status = ScheduledCancellationStatus.Failed;
                        row.Outcome = permanent.Length > 200 ? permanent[..200] : permanent;
                        row.UpdatedAt = clock.Now;
                        row.RecordVersion++;
                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await failTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return false;
    }

    private static async Task PrepareAsync(IServiceProvider services, LegalEntityCode legalEntity, Jurisdiction jurisdiction, CancellationToken cancellationToken)
    {
        var context = services.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.Service(ActorId);
        context.LegalEntity = legalEntity;
        context.Jurisdiction = jurisdiction;
        context.Origin = EventOrigin.Live;
        context.ConfigurationHash = await services.GetRequiredService<Platform.Configuration.IConfigurationResolver>().CurrentHashAsync(null, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The scheduled cancellation {ScheduledId} failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, Guid scheduledId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The scheduled cancellation {ScheduledId} was refused: {Reason}")]
    private static partial void LogRefused(ILogger logger, Guid scheduledId, string reason);
}

/// <summary>Runs one due scheduled cancellation through the cancellation machinery; the caller owns the transaction and rolls back on failure.</summary>
internal sealed class ScheduledCancellationService(PolicyDbContext db, CancelPolicyHandler cancel, IClock clock)
{
    public async Task<Result<JobId>> ExecuteAsync(ScheduledCancellationRow row, CancellationToken cancellationToken)
    {
        var evidence = row.EvidenceRefs is null ? null : JsonSerializer.Deserialize<List<string>>(row.EvidenceRefs);
        var request = new CancellationCreateRequest
        {
            PolicyId = row.PolicyId, Source = row.Source, ReasonCode = row.ReasonCode, EffectiveAt = row.EffectiveAt, Kind = CancellationKind.Standard,
            EvidenceRefs = evidence, RequestRef = row.RequestRef,
        };
        var result = await cancel.HandleAsync(new CancelPolicy(request, Scheduled: new ScheduledCase(row.TermId, row.EffectiveAt, PreviewOnly: false)), cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        row.Status = ScheduledCancellationStatus.Executed;
        row.ExecutedJobId = result.Value.JobId;
        row.UpdatedAt = clock.Now;
        row.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return JobSupport.Stale(); // a rescind won the race: roll the cancellation back
        }

        return result.Value.JobId;
    }
}

/// <summary>The hosted job that runs <see cref="ScheduledCancellationSweepRunner"/> every <see cref="ScheduledCancellationOptions.SweepIntervalSeconds"/> seconds.</summary>
internal sealed partial class ScheduledCancellationSweeper(ScheduledCancellationSweepRunner runner, IOptions<ScheduledCancellationOptions> options, ILogger<ScheduledCancellationSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.SweepIntervalSeconds <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.SweepIntervalSeconds));
        try
        {
            do
            {
                try
                {
                    await runner.RunAsync(stoppingToken).ConfigureAwait(false);
                    CoreInsMetrics.SweeperSucceeded("scheduled-cancellation");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogRunFailed(logger, ex);
                    CoreInsMetrics.SweeperFailed("scheduled-cancellation");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The scheduled-cancellation sweep failed; it runs again at the next tick.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}

/// <summary>DI of the scheduled cancellation commands and sweep (BO-POL-06); <c>PolicyModule</c> appends one call.</summary>
internal static class ScheduledCancellationRegistration
{
    public static IServiceCollection AddScheduledCancellation(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<ScheduledCancellationOptions>().Bind(configuration.GetSection(ScheduledCancellationOptions.Section));
        services.AddScoped<ScheduledCancellationService>();

        services.AddScoped<IValidator<ScheduleCancellation>, ScheduleCancellationValidator>();
        services.AddCommandAuditor<ScheduleCancellation, CancellationScheduleResponse, ScheduleCancellationAuditor>();
        services.AddCommand<ScheduleCancellation, CancellationScheduleResponse, ScheduleCancellationHandler>(CommandDescriptor.For("pol.Cancellation.schedule") with { SupportsDryRun = true });

        services.AddScoped<IValidator<RescindCancellation>, RescindCancellationValidator>();
        services.AddCommandAuditor<RescindCancellation, CancellationRescindResponse, RescindCancellationAuditor>();
        services.AddCommand<RescindCancellation, CancellationRescindResponse, RescindCancellationHandler>(CommandDescriptor.For("pol.Cancellation.rescind") with { SupportsDryRun = true });

        services.AddSingleton<ScheduledCancellationSweepRunner>();
        if (string.Equals(configuration["APP_ROLE"], "worker", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHostedService<ScheduledCancellationSweeper>();
        }

        return services;
    }
}
