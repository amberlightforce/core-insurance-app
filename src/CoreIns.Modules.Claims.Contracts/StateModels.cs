using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.StateMachines;

namespace CoreIns.Modules.Claims.Contracts;

/// <summary>Claim states (contract §3.2.4, PRD-18 §9.2.6). Open sub-states New, InProgress, UnderInvestigation, Settled are owner-private.</summary>
public enum ClaimState
{
    /// <summary>FNOL draft.</summary>
    Draft,

    /// <summary>FNOL submitted (<c>ClaimReported</c>).</summary>
    Open,

    /// <summary>Closed (<c>ClaimClosed</c>; outcome Completed, Denied, Withdrawn, Duplicate, NoPayment).</summary>
    Closed,
}

/// <summary>Triggers of <see cref="ClaimState"/>.</summary>
public enum ClaimTrigger
{
    /// <summary>FNOL submitted.</summary>
    Submit,

    /// <summary>Last exposure closed.</summary>
    Close,

    /// <summary>Reopened with a reason (<c>ClaimReopened</c>).</summary>
    Reopen,

    /// <summary>A draft is discarded (Withdrawn, no number).</summary>
    Discard,
}

/// <summary>The canonical Claim lifecycle.</summary>
public static class ClaimStateModel
{
    /// <summary>The table.</summary>
    public static StateMachine<ClaimState, ClaimTrigger> Machine { get; } =
        StateMachine.Define<ClaimState, ClaimTrigger>(ModuleCode.CLM, "Claim")
            .Initial(ClaimState.Draft)
            .Permit(ClaimState.Draft, ClaimTrigger.Submit, ClaimState.Open)
            .Permit(ClaimState.Open, ClaimTrigger.Close, ClaimState.Closed)
            .Permit(ClaimState.Closed, ClaimTrigger.Reopen, ClaimState.Open)
            .Permit(ClaimState.Draft, ClaimTrigger.Discard, ClaimState.Closed)
            .Build();
}

/// <summary>Exposure states: the same states as Claim (contract §3.2.4).</summary>
public enum ExposureState
{
    /// <summary>Draft.</summary>
    Draft,

    /// <summary>Open (<c>ExposureCreated</c>).</summary>
    Open,

    /// <summary>Closed (clocks Met, Cancelled or Breached-and-acknowledged, D-CON-04).</summary>
    Closed,
}

/// <summary>Triggers of <see cref="ExposureState"/>.</summary>
public enum ExposureTrigger
{
    /// <summary>Submitted with the FNOL.</summary>
    Submit,

    /// <summary>Closed.</summary>
    Close,

    /// <summary>Reopened with a reason.</summary>
    Reopen,

    /// <summary>A draft is discarded.</summary>
    Discard,
}

/// <summary>The canonical Exposure lifecycle (same transitions as Claim).</summary>
public static class ExposureStateModel
{
    /// <summary>The table.</summary>
    public static StateMachine<ExposureState, ExposureTrigger> Machine { get; } =
        StateMachine.Define<ExposureState, ExposureTrigger>(ModuleCode.CLM, "Exposure")
            .Initial(ExposureState.Draft)
            .Permit(ExposureState.Draft, ExposureTrigger.Submit, ExposureState.Open)
            .Permit(ExposureState.Open, ExposureTrigger.Close, ExposureState.Closed)
            .Permit(ExposureState.Closed, ExposureTrigger.Reopen, ExposureState.Open)
            .Permit(ExposureState.Draft, ExposureTrigger.Discard, ExposureState.Closed)
            .Build();
}

/// <summary>ReserveLine states (PRD-18 §9.2.6, names as proposed there). The balance is derived from immutable financial transactions.</summary>
public enum ReserveLineState
{
    /// <summary>First reserve transaction approved (<c>ReserveChanged</c>).</summary>
    OpenLine,

    /// <summary>Final payment or release to zero (<c>final_flag</c>).</summary>
    FinalLine,
}

/// <summary>Triggers of <see cref="ReserveLineState"/>.</summary>
public enum ReserveLineTrigger
{
    /// <summary>A reserve, eroding payment or recovery-reserve transaction is approved.</summary>
    ApplyTransaction,

    /// <summary>Final payment or release to zero.</summary>
    Finalise,

    /// <summary>The claim or exposure is reopened.</summary>
    Reopen,
}

/// <summary>The canonical ReserveLine lifecycle.</summary>
public static class ReserveLineStateModel
{
    /// <summary>The table.</summary>
    public static StateMachine<ReserveLineState, ReserveLineTrigger> Machine { get; } =
        StateMachine.Define<ReserveLineState, ReserveLineTrigger>(ModuleCode.CLM, "ReserveLine")
            .Initial(ReserveLineState.OpenLine)
            .Permit(ReserveLineState.OpenLine, ReserveLineTrigger.ApplyTransaction, ReserveLineState.OpenLine)
            .Permit(ReserveLineState.OpenLine, ReserveLineTrigger.Finalise, ReserveLineState.FinalLine)
            .Permit(ReserveLineState.FinalLine, ReserveLineTrigger.Reopen, ReserveLineState.OpenLine)
            .Build();
}

/// <summary>Claim TransactionSet states (contract §3.2.4, PRD-18 §9.2.6).</summary>
public enum TransactionSetState
{
    /// <summary>Being prepared.</summary>
    Draft,

    /// <summary>Submitted.</summary>
    Submitted,

    /// <summary>Referred or four-eyes (<c>ApprovalRequested</c>).</summary>
    PendingApproval,

    /// <summary>Approved (<c>TransactionSetApproved</c>).</summary>
    Approved,

    /// <summary>Rejected (<c>TransactionSetRejected</c>).</summary>
    Rejected,

    /// <summary>Posted by FIN (<c>JournalPosted</c> with the set reference).</summary>
    Posted,
}

/// <summary>Triggers of <see cref="TransactionSetState"/>.</summary>
public enum TransactionSetTrigger
{
    /// <summary>Submitted.</summary>
    Submit,

    /// <summary>Authority allows, or the checker approves.</summary>
    Approve,

    /// <summary>Authority refers, or four-eyes applies.</summary>
    Refer,

    /// <summary>Rejected by the checker.</summary>
    Reject,

    /// <summary>FIN posted the journal.</summary>
    Post,
}

/// <summary>The canonical claim TransactionSet lifecycle.</summary>
public static class TransactionSetStateModel
{
    /// <summary>The table.</summary>
    public static StateMachine<TransactionSetState, TransactionSetTrigger> Machine { get; } =
        StateMachine.Define<TransactionSetState, TransactionSetTrigger>(ModuleCode.CLM, "TransactionSet")
            .Initial(TransactionSetState.Draft)
            .Permit(TransactionSetState.Draft, TransactionSetTrigger.Submit, TransactionSetState.Submitted)
            .Permit([TransactionSetState.Submitted, TransactionSetState.PendingApproval], TransactionSetTrigger.Approve, TransactionSetState.Approved)
            .Permit(TransactionSetState.Submitted, TransactionSetTrigger.Refer, TransactionSetState.PendingApproval)
            .Permit(TransactionSetState.PendingApproval, TransactionSetTrigger.Reject, TransactionSetState.Rejected)
            .Permit(TransactionSetState.Approved, TransactionSetTrigger.Post, TransactionSetState.Posted)
            .Terminal(TransactionSetState.Rejected, TransactionSetState.Posted)
            .Build();
}

/// <summary>ClaimPayment states (PRD-07 §7.3.3; mirrors BIL Disbursement for Released, Issued, Cleared, Rejected, Stopped, Voided, Returned per D4).</summary>
public enum ClaimPaymentState
{
    /// <summary>In an unapproved transaction set.</summary>
    Pending,

    /// <summary>The set was approved.</summary>
    Approved,

    /// <summary>The set was rejected.</summary>
    Rejected,

    /// <summary>Held (sanctions, SIU, VoP).</summary>
    OnHold,

    /// <summary>Disbursement requested (BIL Requested … Released).</summary>
    Submitted,

    /// <summary>Bank accepted (<c>DisbursementIssued</c> → <c>PaymentIssued</c>).</summary>
    Issued,

    /// <summary>Statement debit matched (<c>DisbursementCleared</c>).</summary>
    Cleared,

    /// <summary>Stopped (<c>DisbursementStopped</c>; publishes <c>PaymentVoided</c>).</summary>
    Stopped,

    /// <summary>BIL rejected the disbursement (<c>DisbursementRejected</c>; publishes <c>PaymentVoided</c>).</summary>
    DisbursementRejected,

    /// <summary>Recalled after issue (publishes <c>PaymentVoided</c>).</summary>
    Voided,

    /// <summary>Returned by the bank (publishes <c>PaymentVoided</c>).</summary>
    Returned,

    /// <summary>CLEARING payment settled through the FS statement net.</summary>
    Settled,
}

/// <summary>Triggers of <see cref="ClaimPaymentState"/>.</summary>
public enum ClaimPaymentTrigger
{
    /// <summary>The transaction set is approved.</summary>
    Approve,

    /// <summary>The transaction set is rejected.</summary>
    Reject,

    /// <summary>A hold is placed.</summary>
    Hold,

    /// <summary>The hold is released.</summary>
    ReleaseHold,

    /// <summary>The disbursement is requested from BIL.</summary>
    Submit,

    /// <summary>BIL reports the disbursement issued.</summary>
    Issue,

    /// <summary>BIL reports the disbursement cleared.</summary>
    Clear,

    /// <summary>The disbursement is stopped.</summary>
    Stop,

    /// <summary>BIL rejected the disbursement.</summary>
    RejectDisbursement,

    /// <summary>The issued disbursement is recalled.</summary>
    Void,

    /// <summary>The bank returned the payment (also after clearing).</summary>
    Return,

    /// <summary>A CLEARING payment's FS statement net is settled.</summary>
    Settle,
}

/// <summary>The canonical ClaimPayment lifecycle.</summary>
public static class ClaimPaymentStateModel
{
    /// <summary>The table.</summary>
    public static StateMachine<ClaimPaymentState, ClaimPaymentTrigger> Machine { get; } =
        StateMachine.Define<ClaimPaymentState, ClaimPaymentTrigger>(ModuleCode.CLM, "ClaimPayment")
            .Initial(ClaimPaymentState.Pending)
            .Permit(ClaimPaymentState.Pending, ClaimPaymentTrigger.Approve, ClaimPaymentState.Approved)
            .Permit(ClaimPaymentState.Pending, ClaimPaymentTrigger.Reject, ClaimPaymentState.Rejected)
            .Permit(ClaimPaymentState.Approved, ClaimPaymentTrigger.Hold, ClaimPaymentState.OnHold)
            .Permit(ClaimPaymentState.OnHold, ClaimPaymentTrigger.ReleaseHold, ClaimPaymentState.Approved)
            .Permit(ClaimPaymentState.Approved, ClaimPaymentTrigger.Submit, ClaimPaymentState.Submitted)
            .Permit(ClaimPaymentState.Submitted, ClaimPaymentTrigger.Issue, ClaimPaymentState.Issued)
            .Permit(ClaimPaymentState.Issued, ClaimPaymentTrigger.Clear, ClaimPaymentState.Cleared)
            .Permit(ClaimPaymentState.Submitted, ClaimPaymentTrigger.Stop, ClaimPaymentState.Stopped)
            .Permit(ClaimPaymentState.Submitted, ClaimPaymentTrigger.RejectDisbursement, ClaimPaymentState.DisbursementRejected)
            .Permit(ClaimPaymentState.Issued, ClaimPaymentTrigger.Void, ClaimPaymentState.Voided)
            .Permit([ClaimPaymentState.Issued, ClaimPaymentState.Cleared], ClaimPaymentTrigger.Return, ClaimPaymentState.Returned)
            .Permit(ClaimPaymentState.Approved, ClaimPaymentTrigger.Settle, ClaimPaymentState.Settled)
            .Terminal(
                ClaimPaymentState.Rejected, ClaimPaymentState.Stopped, ClaimPaymentState.DisbursementRejected,
                ClaimPaymentState.Voided, ClaimPaymentState.Returned, ClaimPaymentState.Settled)
            .Build();
}
