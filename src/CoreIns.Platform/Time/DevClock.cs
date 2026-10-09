using System.Diagnostics;
using System.Text.Json.Nodes;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace CoreIns.Platform.Time;

/// <summary>
/// A source of a clock offset shared by every process of the stack (api and worker). The Development dev clock
/// (<see cref="DevClockStore"/>) is the only implementation; it exists only when <see cref="DevClockRegistration.Guard"/> returned true.
/// </summary>
public interface IClockOffsetSource
{
    /// <summary>The offset to add to the real clock (never negative, never decreasing; at most about half a second stale).</summary>
    TimeSpan Offset { get; }
}

/// <summary>Body of <c>POST /api/plt/v1/dev/clock/advance</c>: how far to move the dev clock forward.</summary>
public sealed record DevClockAdvanceRequest
{
    /// <summary>Whole days to add (0 or more).</summary>
    public int? Days { get; init; }

    /// <summary>Whole hours to add (0 or more).</summary>
    public int? Hours { get; init; }
}

/// <summary>The dev clock as seen by the caller (no personal data).</summary>
/// <param name="OffsetSeconds">Total offset from the real clock, in seconds.</param>
/// <param name="Now">The current time of the dev clock (UTC).</param>
public sealed record DevClockState(long OffsetSeconds, DateTimeOffset Now);

/// <summary>
/// The single-row, forward-only dev clock offset in <c>plt.dev_clock</c> (D-SL3-12). Reads are cached for
/// <see cref="CacheTtl"/> so the api and the worker see an advance within a second; the database trigger refuses any
/// decrease, and the cached value never goes backwards either.
/// </summary>
public sealed class DevClockStore(NpgsqlDataSource dataSource) : IClockOffsetSource
{
    /// <summary>How long a read of the offset is reused.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMilliseconds(500); // keep in step with Refresh (Stopwatch.Frequency / 2)

    /// <summary>The largest total offset the table accepts (100 years), keeping every instant representable.</summary>
    public const long MaxOffsetMicros = 100L * 365 * 86_400 * 1_000_000;

    private readonly Lock _gate = new();
    private long _cachedMicros;
    private long _validUntilTimestamp;

    /// <inheritdoc />
    public TimeSpan Offset
    {
        get
        {
            lock (_gate)
            {
                if (Stopwatch.GetTimestamp() >= _validUntilTimestamp)
                {
                    Refresh();
                }

                return TimeSpan.FromTicks(_cachedMicros * 10);
            }
        }
    }

    /// <summary>Forgets the cache so the next read goes to the database (called after an advance).</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _validUntilTimestamp = 0;
        }
    }

    internal static async Task<(long Before, long After)?> AdvanceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long deltaMicros, CancellationToken cancellationToken)
    {
        // One atomic UPDATE: concurrent advances queue on the row lock and their deltas add up; the WHERE keeps the total in range.
        await using var command = new NpgsqlCommand(
            "UPDATE plt.dev_clock SET offset_micros = offset_micros + @delta, version = version + 1, updated_at = now() "
            + "WHERE id = 1 AND @delta > 0 AND offset_micros + @delta <= @max RETURNING offset_micros",
            connection,
            transaction);
        command.Parameters.AddWithValue("delta", deltaMicros);
        command.Parameters.AddWithValue("max", MaxOffsetMicros);
        var after = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return after is long value ? (value - deltaMicros, value) : null;
    }

    private void Refresh()
    {
        try
        {
            using var command = dataSource.CreateCommand("SELECT offset_micros FROM plt.dev_clock WHERE id = 1");
            if (command.ExecuteScalar() is long micros)
            {
                _cachedMicros = Math.Max(_cachedMicros, Math.Clamp(micros, 0, MaxOffsetMicros));
            }
        }
        catch (Exception ex) when (ex is PostgresException or NpgsqlException or InvalidOperationException)
        {
            // Before the migration ran, or while the database is unreachable: keep the last known offset (starts at zero).
        }

        _validUntilTimestamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
    }
}

/// <summary><c>plt.DevClock.advance</c>: move the dev clock forward (Development with a shiftable clock only).</summary>
internal sealed record AdvanceDevClock(DevClockAdvanceRequest? Request) : ICommand<DevClockState>
{
    /// <summary>The requested advance in seconds (0 when the body is missing).</summary>
    public long Seconds => (((long)(Request?.Days ?? 0) * 24) + (Request?.Hours ?? 0)) * 3600;
}

internal sealed class AdvanceDevClockValidator : AbstractValidator<AdvanceDevClock>
{
    /// <summary>The most one call may add: ten years.</summary>
    public const int MaxDays = 3650;

    public AdvanceDevClockValidator()
    {
        RuleFor(c => c.Request).NotNull();
        When(c => c.Request is not null, () =>
        {
            RuleFor(c => c.Request!.Days).InclusiveBetween(0, MaxDays).When(c => c.Request!.Days is not null);
            RuleFor(c => c.Request!.Hours).InclusiveBetween(0, MaxDays * 24).When(c => c.Request!.Hours is not null);
            RuleFor(c => c.Seconds)
                .GreaterThan(0)
                .WithName("days")
                .WithErrorCode("NOT-FORWARD")
                .WithMessage("The dev clock only moves forward: days and hours must add up to a positive amount.");
        });
    }
}

internal sealed class AdvanceDevClockHandler(DbSession session)
    : ICommandHandler<AdvanceDevClock, DevClockState>
{
    public async Task<Result<DevClockState>> HandleAsync(AdvanceDevClock command, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = session.Transaction ?? throw new InvalidOperationException("plt.DevClock.advance runs inside the command transaction.");
        var moved = await DevClockStore.AdvanceAsync(connection, transaction, command.Seconds * 1_000_000, cancellationToken).ConfigureAwait(false);
        if (moved is null)
        {
            return DomainError.Of(ModuleCode.PLT, PlatformErrors.Validation, "The dev clock cannot be advanced that far (limit: 100 years in total).");
        }

        // The new offset is not committed yet (and the shared cache is refreshed by the endpoint after the commit), so the answer
        // is computed from the new total rather than read back from the clock.
        var now = SystemClock.Instance.Now.Plus(TimeSpan.FromTicks(moved.Value.After * 10));
        return new DevClockState(moved.Value.After / 1_000_000, now.ToDateTimeOffset());
    }
}

internal sealed class AdvanceDevClockAuditor : ICommandAuditor<AdvanceDevClock, DevClockState>
{
    public CommandAuditFacts Describe(AdvanceDevClock command, Result<DevClockState>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.PLT, "DevClock", "1"),
        Changes = result is { IsSuccess: true } ok
            ?
            [
                new AuditChange("offsetSeconds", JsonValue.Create(ok.Value.OffsetSeconds - command.Seconds), JsonValue.Create(ok.Value.OffsetSeconds)),
                new AuditChange("advanceSeconds", null, JsonValue.Create(command.Seconds)),
            ]
            : [],
    };
}

/// <summary>
/// Wiring of the Development dev clock (D-SL3-12). Everything here is registered only when <see cref="Guard"/> returns
/// true: the environment is Development and <c>Platform:Time:Mode=Shiftable</c>. In Production a Shiftable clock stops the
/// Host (<see cref="Guard"/> throws), so neither the offset reader, the command nor the routes can exist there.
/// </summary>
public static class DevClockRegistration
{
    /// <summary>Permission of the advance (<c>Platform:Permissions</c>; Platform.Admin only).</summary>
    public const string AdvancePermission = "plt.DevClock.advance";

    /// <summary>Permission of the read (Platform.Admin only).</summary>
    public const string GetPermission = "plt.DevClock.get";

    /// <summary>
    /// Refuses a shiftable clock in Production (REQ-PLT-332) before the host is built, and returns whether the dev clock is
    /// active: Development with <c>Platform:Time:Mode=Shiftable</c>.
    /// </summary>
    public static bool Guard(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var shiftable = string.Equals(configuration[ClockConfiguration.ModeKey], "Shiftable", StringComparison.OrdinalIgnoreCase);
        if (shiftable && environment.IsProduction())
        {
            throw new InvalidOperationException(
                $"{ClockConfiguration.ModeKey}=Shiftable is not allowed in Production: the production clock is always the system clock (REQ-PLT-332). The Host refuses to start.");
        }

        return shiftable && environment.IsDevelopment();
    }

    /// <summary>Registers the offset reader (every app role) and, for the api, the advance command. Call only when <see cref="Guard"/> returned true.</summary>
    public static IServiceCollection AddDevClock(this IServiceCollection services, bool api)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<DevClockStore>();
        services.TryAddSingleton<IClockOffsetSource>(sp => sp.GetRequiredService<DevClockStore>());
        if (api)
        {
            services.AddScoped<IValidator<AdvanceDevClock>, AdvanceDevClockValidator>();
            services.AddCommandAuditor<AdvanceDevClock, DevClockState, AdvanceDevClockAuditor>();
            services.AddCommand<AdvanceDevClock, DevClockState, AdvanceDevClockHandler>(CommandDescriptor.For("plt.DevClock.advance"));
        }

        return services;
    }

    /// <summary>Maps <c>GET /api/plt/v1/dev/clock</c> and <c>POST /api/plt/v1/dev/clock/advance</c>. Call only when <see cref="Guard"/> returned true.</summary>
    public static void MapDevClock(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/plt/v1/dev/clock");
        group.MapGet(string.Empty, (DevClockStore store, IClock clock) =>
            Results.Ok(new DevClockState(store.Offset.Ticks / TimeSpan.TicksPerSecond, clock.Now.ToDateTimeOffset())))
            .RequireAuthorization(GetPermission);
        group.MapPost(
            "/advance",
            async (DevClockAdvanceRequest? body, HttpContext http, ICommandHandler<AdvanceDevClock, DevClockState> handler, DevClockStore store, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(new AdvanceDevClock(body), cancellationToken).ConfigureAwait(false);

                // The advance has committed (or failed) by now: the next read, here and in the worker's next poll, must see the database.
                store.Invalidate();
                return result.ToHttpResult(http);
            })
            .RequireAuthorization(AdvancePermission);
    }
}
