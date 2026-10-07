using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.StateMachines;

namespace CoreIns.Modules.Documents.Contracts;

/// <summary>Outbound document states (contract §3.2.4 with R-44, PRD-18 §9.2.8).</summary>
public enum DocumentState
{
    /// <summary>Requested (<c>DocumentRequested</c>; sub-states Queued, AwaitingFiscal, AwaitingData).</summary>
    Requested,

    /// <summary>Rendering.</summary>
    Rendering,

    /// <summary>Rendered (<c>DocumentRendered</c>).</summary>
    Rendered,

    /// <summary>Rendering failed (<c>DocumentRenderFailed</c>; sub-states Retrying, Final).</summary>
    Failed,

    /// <summary>Superseded (<c>DocumentSuperseded</c>; Replaced, Withdrawn).</summary>
    Superseded,
}

/// <summary>Triggers of <see cref="DocumentState"/>.</summary>
public enum DocumentTrigger
{
    /// <summary>Rendering starts (also a retry after failure).</summary>
    StartRendering,

    /// <summary>Rendering succeeded.</summary>
    Render,

    /// <summary>Rendering failed.</summary>
    Fail,

    /// <summary>Replaced or withdrawn.</summary>
    Supersede,
}

/// <summary>The canonical outbound Document lifecycle.</summary>
public static class DocumentStateModel
{
    /// <summary>The table.</summary>
    public static StateMachine<DocumentState, DocumentTrigger> Machine { get; } =
        StateMachine.Define<DocumentState, DocumentTrigger>(ModuleCode.DOC, "Document")
            .Initial(DocumentState.Requested)
            .Permit([DocumentState.Requested, DocumentState.Failed], DocumentTrigger.StartRendering, DocumentState.Rendering)
            .Permit(DocumentState.Rendering, DocumentTrigger.Render, DocumentState.Rendered)
            .Permit(DocumentState.Rendering, DocumentTrigger.Fail, DocumentState.Failed)
            .Permit([DocumentState.Requested, DocumentState.Rendered], DocumentTrigger.Supersede, DocumentState.Superseded)
            .Terminal(DocumentState.Superseded)
            .Build();
}

/// <summary>Delivery states (contract §3.2.4: a delivery has its own status). No clock may use rendering as proof of delivery.</summary>
public enum DeliveryState
{
    /// <summary>Pending.</summary>
    Pending,

    /// <summary>Sent.</summary>
    Sent,

    /// <summary>Delivered with proof (<c>DocumentDelivered</c>).</summary>
    Delivered,

    /// <summary>Failed (<c>DeliveryFailed</c>).</summary>
    Failed,

    /// <summary>Bounced.</summary>
    Bounced,
}

/// <summary>Triggers of <see cref="DeliveryState"/>.</summary>
public enum DeliveryTrigger
{
    /// <summary>Handed to the channel.</summary>
    Send,

    /// <summary>Proof of delivery received.</summary>
    Deliver,

    /// <summary>Delivery failed.</summary>
    Fail,

    /// <summary>Bounced.</summary>
    Bounce,
}

/// <summary>The canonical Delivery lifecycle.</summary>
public static class DeliveryStateModel
{
    /// <summary>The table.</summary>
    public static StateMachine<DeliveryState, DeliveryTrigger> Machine { get; } =
        StateMachine.Define<DeliveryState, DeliveryTrigger>(ModuleCode.DOC, "Delivery")
            .Initial(DeliveryState.Pending)
            .Permit(DeliveryState.Pending, DeliveryTrigger.Send, DeliveryState.Sent)
            .Permit(DeliveryState.Sent, DeliveryTrigger.Deliver, DeliveryState.Delivered)
            .Permit(DeliveryState.Sent, DeliveryTrigger.Fail, DeliveryState.Failed)
            .Permit(DeliveryState.Sent, DeliveryTrigger.Bounce, DeliveryState.Bounced)
            .Terminal(DeliveryState.Delivered, DeliveryState.Failed, DeliveryState.Bounced)
            .Build();
}
