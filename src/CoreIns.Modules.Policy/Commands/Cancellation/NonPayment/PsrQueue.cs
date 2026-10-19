using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Http;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Policy.Commands.Cancellation.NonPayment;

/// <summary><c>pol.Psr.decide</c>: staff decide a PENDING non-payment exception, PROCEED (cancel now) or HOLD (keep in force) (D-SL6-04).</summary>
internal sealed record DecidePsrException(PsrDecideRequest Request) : ICommand<PsrExceptionView>;

internal sealed class DecidePsrExceptionValidator : AbstractValidator<DecidePsrException>
{
    public DecidePsrExceptionValidator()
    {
        RuleFor(c => c.Request.ExceptionId).NotEmpty().WithErrorCode("EXCEPTION_REQUIRED");
        RuleFor(c => c.Request.Note).NotEmpty().MaximumLength(500).WithErrorCode("NOTE_REQUIRED");
    }
}

/// <summary>
/// PROCEED runs the cancellation through the same machinery as the automatic path, effective at the later of BIL's date and now (never backdated
/// beyond what BIL asked for); a refusal rolls the whole decision back and the exception stays PENDING. HOLD closes the exception and keeps
/// the policy in force. A second decision is POL-ERR-ILLEGAL-TRANSITION.
/// </summary>
internal sealed class DecidePsrExceptionHandler(
    PolicyDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, NonPaymentCancellationService service, PsrReader reader)
    : ICommandHandler<DecidePsrException, PsrExceptionView>
{
    public async Task<Result<PsrExceptionView>> HandleAsync(DecidePsrException command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var exception = await db.PsrExceptions.SingleOrDefaultAsync(e => e.ExceptionId == request.ExceptionId && e.LegalEntityId == legalEntity, cancellationToken)
            .ConfigureAwait(false);
        if (exception is null)
        {
            return JobSupport.NotFound("PSR exception");
        }

        if (exception.Status != "PENDING")
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The exception has already been decided.");
        }

        var row = await db.NonPaymentCancellations.SingleAsync(n => n.NonPaymentId == exception.NonPaymentId, cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        if (request.Decision == PsrDecideRequest.DecisionValue.Proceed)
        {
            var executed = await service.ExecuteAsync(row, row.EffectiveAt > now ? row.EffectiveAt : now, cancellationToken).ConfigureAwait(false);
            if (executed.IsFailure)
            {
                return executed.Error!;
            }

            exception.Status = "PROCEED";
        }
        else
        {
            exception.Status = "HOLD";
            row.Status = NonPaymentStatus.Held;
            row.UpdatedAt = now;
            row.RecordVersion++;
        }

        exception.DecidedBy = context.Actor.ToString();
        exception.DecidedAt = now;
        exception.DecisionNote = request.Note.Trim();
        exception.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The exception has already been decided.");
        }

        return await reader.ToViewAsync(exception, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Audit facts of the decision: the exception and its id as business key.</summary>
internal sealed class DecidePsrExceptionAuditor : ICommandAuditor<DecidePsrException, PsrExceptionView>
{
    public CommandAuditFacts Describe(DecidePsrException command, Result<PsrExceptionView>? result) =>
        new()
        {
            ObjectRef = new ObjectRef(ModuleCode.POL, "PsrException", command.Request.ExceptionId.ToString("D")),
            BusinessKeys = BusinessKeys.Empty.With("exceptionId", command.Request.ExceptionId.ToString()),
        };
}

/// <summary>pol.Psr.list: the PSR queue of non-payment cancellations that were not run automatically.</summary>
internal sealed class PsrReader(PolicyDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
{
    public async Task<Result<PsrExceptionListPage>> ListAsync(string? status, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        // TODO(hardening): keyset cursor; an offset cursor can skip or repeat rows while the queue changes.
        var skip = 0;
        if (limit is < 1 or > 200 || (cursor is not null && (!int.TryParse(cursor, out skip) || skip < 0)))
        {
            return DomainError.Of(ModuleCode.POL, "VALIDATION", "cursor is malformed or limit is outside 1..200.");
        }

        if (status is not null && status is not ("PENDING" or "PROCEED" or "HOLD" or "RESCINDED"))
        {
            return DomainError.Of(ModuleCode.POL, "VALIDATION", "status must be PENDING, PROCEED, HOLD or RESCINDED.");
        }

        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var query = db.PsrExceptions.AsNoTracking().Where(e => e.LegalEntityId == legalEntity);
        if (status is not null)
        {
            query = query.Where(e => e.Status == status);
        }

        var take = limit ?? 25;
        var rows = await query.OrderBy(e => e.RaisedAt).ThenBy(e => e.ExceptionId).Skip(skip).Take(take + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(take).ToList();
        return new PsrExceptionListPage
        {
            Items = await ToViewsAsync(page, cancellationToken).ConfigureAwait(false),
            NextCursor = rows.Count > take ? (skip + take).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            Limit = take,
        };
    }

    public async Task<PsrExceptionView> ToViewAsync(PsrExceptionRow row, CancellationToken cancellationToken) =>
        (await ToViewsAsync([row], cancellationToken).ConfigureAwait(false))[0];

    private async Task<IReadOnlyList<PsrExceptionView>> ToViewsAsync(IReadOnlyList<PsrExceptionRow> rows, CancellationToken cancellationToken)
    {
        var policyIds = rows.Select(r => r.PolicyId).Distinct().ToList();
        var numbers = await db.Policies.AsNoTracking().Where(p => policyIds.Contains(p.PolicyId)).Select(p => new { p.PolicyId, p.PolicyNumber })
            .ToDictionaryAsync(p => p.PolicyId, p => p.PolicyNumber, cancellationToken).ConfigureAwait(false);
        var nonPayIds = rows.Select(r => r.NonPaymentId).ToList();
        var requests = await db.NonPaymentCancellations.AsNoTracking().Where(n => nonPayIds.Contains(n.NonPaymentId))
            .ToDictionaryAsync(n => n.NonPaymentId, cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new PsrExceptionView
        {
            ExceptionId = r.ExceptionId,
            PolicyId = r.PolicyId,
            PolicyNumber = numbers[r.PolicyId],
            TermId = r.TermId,
            RequestId = requests[r.NonPaymentId].RequestId,
            Reason = r.Reason switch
            {
                PsrReasons.Vulnerable => PsrExceptionView.ReasonValue.VulnerableCustomer,
                PsrReasons.NoticeMissing => PsrExceptionView.ReasonValue.NoticeReferenceMissing,
                _ => PsrExceptionView.ReasonValue.Hold,
            },
            Status = r.Status switch
            {
                "PROCEED" => PsrExceptionStatus.Proceed,
                "HOLD" => PsrExceptionStatus.Hold,
                "RESCINDED" => PsrExceptionStatus.Rescinded,
                _ => PsrExceptionStatus.Pending,
            },
            EffectiveAt = requests[r.NonPaymentId].EffectiveAt,
            RaisedAt = r.RaisedAt,
            DecidedBy = r.DecidedBy,
            DecidedAt = r.DecidedAt,
            DecisionNote = r.DecisionNote,
            JobId = requests[r.NonPaymentId].JobId,
        })];
    }
}

/// <summary>REST facade of <c>pol.Psr.*</c> (contracts/openapi/pol.yaml): thin.</summary>
[ApiController]
[Route("api/pol/v1/psr-exceptions")]
internal sealed class PsrController : ControllerBase
{
    public const string ListPermission = "pol.Psr.list";
    public const string DecidePermission = "pol.Psr.decide";

    /// <summary>pol.Psr.list.</summary>
    [HttpGet]
    [Authorize(Policy = ListPermission)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? status, [FromQuery] int? limit, [FromQuery] string? cursor, [FromServices] PsrReader reader, CancellationToken cancellationToken) =>
        (await reader.ListAsync(status, limit, cursor, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pol.Psr.decide.</summary>
    [HttpPost("decide")]
    [Authorize(Policy = DecidePermission)]
    public async Task<IResult> DecideAsync(
        [FromBody] PsrDecideRequest request, [FromServices] ICommandHandler<DecidePsrException, PsrExceptionView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DecidePsrException(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}

/// <summary>Settings of the non-payment sweep (the scheduled cancellations whose effective moment has come).</summary>
internal sealed class NonPaymentOptions
{
    public const string Section = "Policy:NonPayment";

    /// <summary>Seconds between sweeps. 0 turns the hosted sweep off (tests call the runner).</summary>
    public int SweepIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// One sweep: runs every SCHEDULED non-payment cancellation whose effective moment has passed, each in its own scope and transaction, as the
/// service actor <c>pol-nonpay-sweeper</c>. A row already rescinded or executed is skipped (the locked row is re-read).
/// </summary>
internal sealed partial class NonPaymentSweepRunner(IServiceScopeFactory scopes, IClock clock, IOptions<StampOptions> stamp, ILogger<NonPaymentSweepRunner> logger)
{
    public const string ActorId = "pol-nonpay-sweeper";

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var legalEntity = stamp.Value.LegalEntity is { Length: > 0 } code ? LegalEntityCode.Parse(code) : throw new InvalidOperationException("Stamp:LegalEntity is not configured.");
        var jurisdiction = Jurisdiction.Parse(stamp.Value.Country ?? throw new InvalidOperationException("Stamp:Country is not configured."));
        var now = clock.Now;
        IReadOnlyList<Guid> due;
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<PolicyDbContext>();
            var legalEntityId = scope.ServiceProvider.GetRequiredService<ILegalEntityDirectory>().Resolve(legalEntity);
            due = await db.NonPaymentCancellations.AsNoTracking()
                .Where(n => n.LegalEntityId == legalEntityId && n.Status == NonPaymentStatus.Scheduled && n.EffectiveAt <= now)
                .OrderBy(n => n.EffectiveAt).Select(n => n.NonPaymentId).Take(100).ToListAsync(cancellationToken).ConfigureAwait(false);
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
                    context.ConfigurationHash = await services.GetRequiredService<Platform.Configuration.IConfigurationResolver>().CurrentHashAsync(null, cancellationToken).ConfigureAwait(false);
                    var session = services.GetRequiredService<DbSession>();
                    var transaction = await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                    await using (transaction.ConfigureAwait(false))
                    {
                        var db = services.GetRequiredService<PolicyDbContext>();
                        var row = await db.NonPaymentCancellations.SingleAsync(n => n.NonPaymentId == id, cancellationToken).ConfigureAwait(false);
                        if (row.Status != NonPaymentStatus.Scheduled)
                        {
                            continue;
                        }

                        var executed = await services.GetRequiredService<NonPaymentCancellationService>().ExecuteAsync(row, row.EffectiveAt, cancellationToken).ConfigureAwait(false);
                        if (executed.IsFailure)
                        {
                            LogFailed(logger, id, executed.Error!.ToString());
                            continue; // the owner transaction rolls back on dispose
                        }

                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        done++;
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

    [LoggerMessage(Level = LogLevel.Error, Message = "The scheduled non-payment cancellation {NonPaymentId} failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, Guid nonPaymentId, string reason);
}

/// <summary>The hosted job that runs <see cref="NonPaymentSweepRunner"/> every <see cref="NonPaymentOptions.SweepIntervalSeconds"/> seconds.</summary>
internal sealed partial class NonPaymentSweeper(NonPaymentSweepRunner runner, IOptions<NonPaymentOptions> options, ILogger<NonPaymentSweeper> logger) : BackgroundService
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
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogRunFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The non-payment sweep failed; it runs again at the next tick.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}

/// <summary>DI of the non-payment consumer, the PSR queue and the sweep (SL6-POL-NONPAY).</summary>
internal static class NonPaymentRegistration
{
    public static IServiceCollection AddNonPaymentCancellation(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.AddOptions<NonPaymentOptions>().Bind(configuration.GetSection(NonPaymentOptions.Section));
        services.TryAddScoped<IVulnerabilityCheck, PartyVulnerabilityCheck>();
        services.AddScoped<NonPaymentCancellationService>();
        services.AddScoped<PsrReader>();
        services.AddScoped<IValidator<DecidePsrException>, DecidePsrExceptionValidator>();
        services.AddCommandAuditor<DecidePsrException, PsrExceptionView, DecidePsrExceptionAuditor>();
        services.AddCommand<DecidePsrException, PsrExceptionView, DecidePsrExceptionHandler>(CommandDescriptor.For("pol.Psr.decide"));
        services.AddEventHandler<CancellationForNonPaymentRequestedV1, CancellationForNonPaymentRequestedHandler>(
            EventDescriptor.From(CancellationForNonPaymentRequestedV1.Descriptor), CancellationForNonPaymentRequestedHandler.Name, ModuleCode.POL);
        services.AddEventHandler<DelinquencyResolvedV1, DelinquencyResolvedHandler>(
            EventDescriptor.From(DelinquencyResolvedV1.Descriptor), DelinquencyResolvedHandler.Name, ModuleCode.POL);
        services.AddSingleton<NonPaymentSweepRunner>();
        if (string.Equals(configuration["APP_ROLE"], "worker", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHostedService<NonPaymentSweeper>();
        }

        return services;
    }
}
