using System.Diagnostics.CodeAnalysis;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.StateMachines;

namespace CoreIns.Modules.Market.Contracts;

/// <summary>Pack version states (PRD-17 §7.3.2: Built → Signed → Certified or Rejected → Published → Deprecated → Removed).</summary>
public enum PackState
{
    /// <summary>Built.</summary>
    Built,

    /// <summary>Signed.</summary>
    [SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Canonical state name from PRD-17 §7.3.2.")]
    Signed,

    /// <summary>Certification gates passed.</summary>
    Certified,

    /// <summary>Certification failed.</summary>
    Rejected,

    /// <summary>Published (activatable per legal entity).</summary>
    Published,

    /// <summary>Deprecated (<c>PackDeprecated</c>).</summary>
    Deprecated,

    /// <summary>Removed.</summary>
    Removed,
}

/// <summary>Triggers of <see cref="PackState"/>.</summary>
public enum PackTrigger
{
    /// <summary>Sign.</summary>
    Sign,

    /// <summary>Certify.</summary>
    Certify,

    /// <summary>Reject certification.</summary>
    Reject,

    /// <summary>Publish.</summary>
    Publish,

    /// <summary>Deprecate.</summary>
    Deprecate,

    /// <summary>Remove.</summary>
    Remove,
}

/// <summary>The canonical Pack version lifecycle.</summary>
public static class PackStateModel
{
    /// <summary>The table.</summary>
    public static StateMachine<PackState, PackTrigger> Machine { get; } =
        StateMachine.Define<PackState, PackTrigger>(ModuleCode.MKT, "Pack")
            .Initial(PackState.Built)
            .Permit(PackState.Built, PackTrigger.Sign, PackState.Signed)
            .Permit(PackState.Signed, PackTrigger.Certify, PackState.Certified)
            .Permit(PackState.Signed, PackTrigger.Reject, PackState.Rejected)
            .Permit(PackState.Certified, PackTrigger.Publish, PackState.Published)
            .Permit(PackState.Published, PackTrigger.Deprecate, PackState.Deprecated)
            .Permit(PackState.Deprecated, PackTrigger.Remove, PackState.Removed)
            .Terminal(PackState.Rejected, PackState.Removed)
            .Build();
}

/// <summary>Per-entity pack activation states (PRD-17 PackActivation: Requested → PendingApproval → Scheduled → Active → Superseded; withdrawable).</summary>
public enum PackActivationState
{
    /// <summary>Requested.</summary>
    Requested,

    /// <summary>Waiting for maker-checker approval.</summary>
    PendingApproval,

    /// <summary>Approved; waiting for its activation instant.</summary>
    Scheduled,

    /// <summary>In force (<c>PackActivated</c> / <c>PackRolledBack</c>).</summary>
    Active,

    /// <summary>Replaced by a later activation.</summary>
    Superseded,

    /// <summary>Withdrawn before its instant.</summary>
    Withdrawn,
}

/// <summary>Triggers of <see cref="PackActivationState"/>.</summary>
public enum PackActivationTrigger
{
    /// <summary>Submit for approval.</summary>
    Submit,

    /// <summary>Approved; scheduled.</summary>
    Approve,

    /// <summary>The activation instant is reached.</summary>
    Activate,

    /// <summary>A later activation supersedes it.</summary>
    Supersede,

    /// <summary>Withdrawn before its instant.</summary>
    Withdraw,
}

/// <summary>The PackActivation lifecycle.</summary>
public static class PackActivationStateModel
{
    /// <summary>The table.</summary>
    public static StateMachine<PackActivationState, PackActivationTrigger> Machine { get; } =
        StateMachine.Define<PackActivationState, PackActivationTrigger>(ModuleCode.MKT, "PackActivation")
            .Initial(PackActivationState.Requested)
            .Permit(PackActivationState.Requested, PackActivationTrigger.Submit, PackActivationState.PendingApproval)
            .Permit(PackActivationState.PendingApproval, PackActivationTrigger.Approve, PackActivationState.Scheduled)
            .Permit(PackActivationState.Scheduled, PackActivationTrigger.Activate, PackActivationState.Active)
            .Permit(PackActivationState.Active, PackActivationTrigger.Supersede, PackActivationState.Superseded)
            .Permit([PackActivationState.PendingApproval, PackActivationState.Scheduled], PackActivationTrigger.Withdraw, PackActivationState.Withdrawn)
            .Terminal(PackActivationState.Superseded, PackActivationState.Withdrawn)
            .Build();
}
