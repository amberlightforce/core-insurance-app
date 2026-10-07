using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.StateMachines;

namespace CoreIns.Modules.Product.Contracts;

/// <summary>ProductVersion states (contract §3.2.4, PRD-02 §7.3). Only Locked versions resolve for transactions.</summary>
public enum ProductVersionState
{
    /// <summary>Being authored.</summary>
    Draft,

    /// <summary>Submitted for approval (<c>ProductVersionSubmitted</c>).</summary>
    Submitted,

    /// <summary>Approved, not yet published (<c>ProductVersionApproved</c>).</summary>
    Approved,

    /// <summary>Published and immutable (<c>ProductVersionPublished</c>); see <see cref="ProductVersionLockedSubstate"/>.</summary>
    Locked,

    /// <summary>Retired (<c>ProductVersionRetired</c>).</summary>
    Retired,
}

/// <summary>Triggers of <see cref="ProductVersionState"/>.</summary>
public enum ProductVersionTrigger
{
    /// <summary>Submit for approval.</summary>
    Submit,

    /// <summary>Approve.</summary>
    Approve,

    /// <summary>Return to draft (<c>ProductVersionReturned</c>).</summary>
    Return,

    /// <summary>Publish (lock).</summary>
    Publish,

    /// <summary>Retire.</summary>
    Retire,
}

/// <summary>The canonical ProductVersion lifecycle.</summary>
public static class ProductVersionStateModel
{
    /// <summary>The table.</summary>
    public static StateMachine<ProductVersionState, ProductVersionTrigger> Machine { get; } =
        StateMachine.Define<ProductVersionState, ProductVersionTrigger>(ModuleCode.PFC, "ProductVersion")
            .Initial(ProductVersionState.Draft)
            .Permit(ProductVersionState.Draft, ProductVersionTrigger.Submit, ProductVersionState.Submitted)
            .Permit(ProductVersionState.Submitted, ProductVersionTrigger.Approve, ProductVersionState.Approved)
            .Permit([ProductVersionState.Submitted, ProductVersionState.Approved], ProductVersionTrigger.Return, ProductVersionState.Draft)
            .Permit(ProductVersionState.Approved, ProductVersionTrigger.Publish, ProductVersionState.Locked)
            .Permit(ProductVersionState.Locked, ProductVersionTrigger.Retire, ProductVersionState.Retired)
            .Terminal(ProductVersionState.Retired)
            .Build();
}

/// <summary>Sub-states of a Locked product version (R-11, CCR-PFC-06).</summary>
public enum ProductVersionLockedSubstate
{
    /// <summary>Resolves for new business, renewals and changes.</summary>
    Active,

    /// <summary>Closed to new business; renewals and in-force changes per conversion rules.</summary>
    ClosedToNewBusiness,

    /// <summary>Run-off.</summary>
    RunOff,
}

/// <summary>Triggers of <see cref="ProductVersionLockedSubstate"/>.</summary>
public enum ProductVersionLockedTrigger
{
    /// <summary>Close to new business.</summary>
    CloseToNewBusiness,

    /// <summary>Enter run-off.</summary>
    EnterRunOff,
}

/// <summary>The Locked sub-state lifecycle (Active → ClosedToNewBusiness → RunOff).</summary>
public static class ProductVersionLockedStateModel
{
    /// <summary>The table.</summary>
    public static StateMachine<ProductVersionLockedSubstate, ProductVersionLockedTrigger> Machine { get; } =
        StateMachine.Define<ProductVersionLockedSubstate, ProductVersionLockedTrigger>(ModuleCode.PFC, "ProductVersion.Locked")
            .Initial(ProductVersionLockedSubstate.Active)
            .Permit(ProductVersionLockedSubstate.Active, ProductVersionLockedTrigger.CloseToNewBusiness, ProductVersionLockedSubstate.ClosedToNewBusiness)
            .Permit(ProductVersionLockedSubstate.ClosedToNewBusiness, ProductVersionLockedTrigger.EnterRunOff, ProductVersionLockedSubstate.RunOff)
            .Terminal(ProductVersionLockedSubstate.RunOff)
            .Build();
}
