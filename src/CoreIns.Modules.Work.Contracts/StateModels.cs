using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.StateMachines;

namespace CoreIns.Modules.Work.Contracts;

/// <summary>Activity states (contract §3.2.4, PRD-13). Assignment state (Unassigned, Queued, Assigned) is separate.</summary>
public enum ActivityState
{
    /// <summary>Open (<c>ActivityCreated</c>; sub-states New, InProgress, Waiting).</summary>
    Open,

    /// <summary>Completed (<c>ActivityCompleted</c>).</summary>
    Completed,

    /// <summary>Skipped (not mandatory; <c>ActivitySkipped</c>).</summary>
    Skipped,

    /// <summary>Cancelled (<c>ActivityCancelled</c>).</summary>
    Cancelled,
}

/// <summary>Triggers of <see cref="ActivityState"/>.</summary>
public enum ActivityTrigger
{
    /// <summary>Complete with a valid outcome.</summary>
    Complete,

    /// <summary>Skip.</summary>
    Skip,

    /// <summary>Cancel.</summary>
    Cancel,
}

/// <summary>The canonical Activity lifecycle.</summary>
public static class ActivityStateModel
{
    /// <summary>The table.</summary>
    public static StateMachine<ActivityState, ActivityTrigger> Machine { get; } =
        StateMachine.Define<ActivityState, ActivityTrigger>(ModuleCode.WRK, "Activity")
            .Initial(ActivityState.Open)
            .Permit(ActivityState.Open, ActivityTrigger.Complete, ActivityState.Completed)
            .Permit(ActivityState.Open, ActivityTrigger.Skip, ActivityState.Skipped)
            .Permit(ActivityState.Open, ActivityTrigger.Cancel, ActivityState.Cancelled)
            .Terminal(ActivityState.Completed, ActivityState.Skipped, ActivityState.Cancelled)
            .Build();
}
