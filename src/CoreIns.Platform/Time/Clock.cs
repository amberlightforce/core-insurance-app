using System.Diagnostics.CodeAnalysis;
using CoreIns.SharedKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Platform.Time;

/// <summary>
/// The platform time service (<c>plt.Time.now</c>, REQ-PLT-332, contract §3.9.13): the only source of the current
/// instant. Every module injects it; reading the system clock directly is a build error (COREINS002).
/// </summary>
public interface IClock
{
    /// <summary>The current UTC instant (microsecond precision).</summary>
    Instant Now { get; }
}

/// <summary>The real UTC clock. The only type allowed to read the system clock.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>The process-wide instance.</summary>
    public static SystemClock Instance { get; } = new();

    /// <inheritdoc />
    [SuppressMessage("CoreIns.Time", "COREINS002:Read the current time from IClock",
        Justification = "This is the IClock implementation; it is the one place that reads the system clock.")]
    public Instant Now => Instant.FromDateTimeOffset(TimeProvider.System.GetUtcNow());
}

/// <summary>
/// Non-production clock for testing time-dependent behaviour (REQ-PLT-332 "non-production offset"): the real clock
/// plus an adjustable offset, or a frozen instant. Registered only when <c>Platform:Time:Mode</c> is <c>Shiftable</c>
/// and the environment is not Production; the start-up guard refuses it in Production.
/// </summary>
public sealed class ShiftableClock : IClock
{
    private readonly IClock _inner;
    private readonly Lock _gate = new();
    private TimeSpan _offset;
    private Instant? _frozen;

    /// <summary>Creates the clock over a real clock.</summary>
    public ShiftableClock(IClock inner, TimeSpan initialOffset = default)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _offset = initialOffset;
    }

    /// <inheritdoc />
    public Instant Now
    {
        get
        {
            lock (_gate)
            {
                return _frozen ?? _inner.Now.Plus(_offset);
            }
        }
    }

    /// <summary>The current offset from the real clock.</summary>
    public TimeSpan Offset
    {
        get
        {
            lock (_gate)
            {
                return _offset;
            }
        }
    }

    /// <summary>Moves the clock to run at <paramref name="offset"/> from real time (unfreezes).</summary>
    public void SetOffset(TimeSpan offset)
    {
        lock (_gate)
        {
            _offset = offset;
            _frozen = null;
        }
    }

    /// <summary>Stops the clock at <paramref name="instant"/>.</summary>
    public void Freeze(Instant instant)
    {
        lock (_gate)
        {
            _frozen = instant;
        }
    }

    /// <summary>Moves a frozen clock forward, or adds to the offset of a running one.</summary>
    public void Advance(TimeSpan duration)
    {
        lock (_gate)
        {
            if (_frozen is { } frozen)
            {
                _frozen = frozen.Plus(duration);
            }
            else
            {
                _offset += duration;
            }
        }
    }

    /// <summary>Back to real time.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _offset = TimeSpan.Zero;
            _frozen = null;
        }
    }
}

/// <summary>A fully manual clock for unit tests and simulations.</summary>
public sealed class ManualClock(Instant start) : IClock
{
    private readonly Lock _gate = new();
    private Instant _now = start;

    /// <inheritdoc />
    public Instant Now
    {
        get
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan duration)
    {
        lock (_gate)
        {
            _now = _now.Plus(duration);
        }
    }

    /// <summary>Sets the clock.</summary>
    public void Set(Instant instant)
    {
        lock (_gate)
        {
            _now = instant;
        }
    }
}

/// <summary>Clock selection from configuration (<c>Platform:Time</c>).</summary>
public static class ClockConfiguration
{
    /// <summary>Configuration key of the clock mode: <c>System</c> (default) or <c>Shiftable</c>.</summary>
    public const string ModeKey = "Platform:Time:Mode";

    /// <summary>Configuration key of the initial offset of a shiftable clock (a <see cref="TimeSpan"/>, e.g. <c>30.00:00:00</c>).</summary>
    public const string OffsetKey = "Platform:Time:Offset";

    /// <summary>
    /// Builds the clock for this environment. A shiftable clock is refused in Production (REQ-PLT-332: never in prod).
    /// </summary>
    public static IClock Create(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var mode = configuration[ModeKey];
        if (string.IsNullOrWhiteSpace(mode) || mode.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            return SystemClock.Instance;
        }

        if (!mode.Equals("Shiftable", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unknown {ModeKey} '{mode}'. Expected System or Shiftable.");
        }

        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                $"{ModeKey}=Shiftable is not allowed in Production: the production clock is always the system clock (REQ-PLT-332).");
        }

        var offset = configuration[OffsetKey] is { Length: > 0 } text
            ? TimeSpan.Parse(text, System.Globalization.CultureInfo.InvariantCulture)
            : TimeSpan.Zero;
        return new ShiftableClock(SystemClock.Instance, offset);
    }
}
