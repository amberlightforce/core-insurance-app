using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.StateMachines;

namespace CoreIns.Modules.Compliance.Contracts;

/// <summary>Kind of statutory clock (R-78, D7, D-CON-03).</summary>
public enum StatutoryClockKind
{
    /// <summary>Must be met before expiry: stopped early → Met; expiry while running → Breached (recorded on the obligations register).</summary>
    Deadline,

    /// <summary>A waiting period: expiry → Elapsed and the follow-on action runs (e.g. BIL cancels on <c>BIL_NONPAY_NOTICE</c>); it never breaches.</summary>
    WaitingPeriod,

    /// <summary>A deadline on a fixed calendar date (D7: renewal and non-renewal notices). Treated as a deadline (assumption, see F-1b report).</summary>
    FixedDate,
}

/// <summary>Clock instance states (contract §3.2.4 with R-78, PRD-18 §9.2.9).</summary>
public enum StatutoryClockState
{
    /// <summary>Running (<c>ClockStarted</c>).</summary>
    Running,

    /// <summary>Paused.</summary>
    Paused,

    /// <summary>A warning threshold passed (<c>ClockWarned</c>).</summary>
    Warned,

    /// <summary>Stopped before expiry (<c>ClockMet</c>; outcome Met, Cured, Exercised or Withdrawn).</summary>
    Met,

    /// <summary>A deadline expired while running (<c>ClockBreached</c>); late completion stays Breached (MetLate).</summary>
    Breached,

    /// <summary>A waiting period expired (<c>ClockElapsed</c>).</summary>
    Elapsed,

    /// <summary>Cancelled (<c>ClockCancelled</c>).</summary>
    Cancelled,
}

/// <summary>Triggers of <see cref="StatutoryClockState"/>.</summary>
public enum StatutoryClockTrigger
{
    /// <summary>Pause.</summary>
    Pause,

    /// <summary>Resume.</summary>
    Resume,

    /// <summary>A warning threshold is reached.</summary>
    Warn,

    /// <summary>The stop event arrives before expiry.</summary>
    Stop,

    /// <summary>The clock reaches its due instant while running.</summary>
    Expire,

    /// <summary>The obligation is completed after a breach (recorded as MetLate; the state stays Breached).</summary>
    CompleteLate,

    /// <summary>Cancel.</summary>
    Cancel,
}

/// <summary>How a stopped clock ended (outcome of <see cref="StatutoryClockState.Met"/>).</summary>
public enum ClockStopOutcome
{
    /// <summary>Obligation met.</summary>
    Met,

    /// <summary>Default cured during a waiting period.</summary>
    Cured,

    /// <summary>A right was exercised during a waiting period.</summary>
    Exercised,

    /// <summary>The request was withdrawn.</summary>
    Withdrawn,
}

/// <summary>The canonical statutory clock lifecycle, one table per <see cref="StatutoryClockKind"/>.</summary>
public static class StatutoryClockStateModel
{
    /// <summary>DEADLINE clocks: expiry breaches.</summary>
    public static StateMachine<StatutoryClockState, StatutoryClockTrigger> Deadline { get; } = Build("StatutoryClock.Deadline", StatutoryClockState.Breached);

    /// <summary>WAITING_PERIOD clocks: expiry elapses, never breaches.</summary>
    public static StateMachine<StatutoryClockState, StatutoryClockTrigger> WaitingPeriod { get; } = Build("StatutoryClock.WaitingPeriod", StatutoryClockState.Elapsed);

    /// <summary>FIXED_DATE clocks: deadlines on a fixed date (same table as DEADLINE).</summary>
    public static StateMachine<StatutoryClockState, StatutoryClockTrigger> FixedDate { get; } = Build("StatutoryClock.FixedDate", StatutoryClockState.Breached);

    /// <summary>The table for a kind.</summary>
    public static StateMachine<StatutoryClockState, StatutoryClockTrigger> For(StatutoryClockKind kind) => kind switch
    {
        StatutoryClockKind.Deadline => Deadline,
        StatutoryClockKind.WaitingPeriod => WaitingPeriod,
        StatutoryClockKind.FixedDate => FixedDate,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static StateMachine<StatutoryClockState, StatutoryClockTrigger> Build(string name, StatutoryClockState expiry)
    {
        var builder = StateMachine.Define<StatutoryClockState, StatutoryClockTrigger>(ModuleCode.CMP, name)
            .Initial(StatutoryClockState.Running)
            .Permit(StatutoryClockState.Running, StatutoryClockTrigger.Pause, StatutoryClockState.Paused)
            .Permit(StatutoryClockState.Paused, StatutoryClockTrigger.Resume, StatutoryClockState.Running)
            .Permit(StatutoryClockState.Running, StatutoryClockTrigger.Warn, StatutoryClockState.Warned)
            .Permit([StatutoryClockState.Running, StatutoryClockState.Warned], StatutoryClockTrigger.Stop, StatutoryClockState.Met)
            .Permit([StatutoryClockState.Running, StatutoryClockState.Warned], StatutoryClockTrigger.Expire, expiry)
            .Permit([StatutoryClockState.Running, StatutoryClockState.Paused, StatutoryClockState.Warned], StatutoryClockTrigger.Cancel, StatutoryClockState.Cancelled);

        if (expiry == StatutoryClockState.Breached)
        {
            builder.Permit(StatutoryClockState.Breached, StatutoryClockTrigger.CompleteLate, StatutoryClockState.Breached)
                .Terminal(StatutoryClockState.Met, StatutoryClockState.Cancelled);

            // A deadline never elapses.
            return Complete(builder, StatutoryClockState.Elapsed);
        }

        // A waiting period never breaches (R-78).
        builder.Terminal(StatutoryClockState.Met, StatutoryClockState.Elapsed, StatutoryClockState.Cancelled);
        return Complete(builder, StatutoryClockState.Breached);
    }

    private static StateMachine<StatutoryClockState, StatutoryClockTrigger> Complete(
        StateMachineBuilder<StatutoryClockState, StatutoryClockTrigger> builder, StatutoryClockState unused) =>
        builder.NotUsed(unused).Build();
}
