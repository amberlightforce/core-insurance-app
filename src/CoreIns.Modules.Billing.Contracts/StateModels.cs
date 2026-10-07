using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.StateMachines;

namespace CoreIns.Modules.Billing.Contracts;

/// <summary>Invoice states (contract §3.2.4, PRD-18 §9.2.4). Invoices are non-fiscal payment demands (D3).</summary>
public enum InvoiceState
{
    /// <summary>Scheduled, not yet billed.</summary>
    Planned,

    /// <summary>Issued by the invoice run (<c>InvoiceIssued</c>).</summary>
    Billed,

    /// <summary>Due for payment.</summary>
    Due,

    /// <summary>Allocations equal the amount (<c>CashAllocated</c>).</summary>
    Paid,

    /// <summary>Partly allocated.</summary>
    PartiallyPaid,

    /// <summary>Due plus grace passed with an open amount above tolerance (<c>DelinquencyStarted</c>).</summary>
    Overdue,

    /// <summary>Written off (<c>WriteOffPosted</c>).</summary>
    WrittenOff,

    /// <summary>Offset in full by a credit note.</summary>
    Reversed,
}

/// <summary>Triggers of <see cref="InvoiceState"/>.</summary>
public enum InvoiceTrigger
{
    /// <summary>The invoice run bills the planned invoice.</summary>
    Bill,

    /// <summary>The due date arrives.</summary>
    BecomeDue,

    /// <summary>Allocations reach the full amount.</summary>
    AllocateInFull,

    /// <summary>A partial allocation.</summary>
    AllocatePartially,

    /// <summary>Due plus grace passes with an open amount.</summary>
    BecomeOverdue,

    /// <summary>The remaining amount is written off.</summary>
    WriteOff,

    /// <summary>A payment is reversed before the due date.</summary>
    PaymentReversedBeforeDue,

    /// <summary>A payment is reversed after the due date.</summary>
    PaymentReversedAfterDue,

    /// <summary>A credit note offsets the invoice in full.</summary>
    Reverse,
}

/// <summary>The canonical Invoice lifecycle.</summary>
public static class InvoiceStateModel
{
    /// <summary>The table.</summary>
    public static StateMachine<InvoiceState, InvoiceTrigger> Machine { get; } =
        StateMachine.Define<InvoiceState, InvoiceTrigger>(ModuleCode.BIL, "Invoice")
            .Initial(InvoiceState.Planned)
            .Permit(InvoiceState.Planned, InvoiceTrigger.Bill, InvoiceState.Billed)
            .Permit(InvoiceState.Billed, InvoiceTrigger.BecomeDue, InvoiceState.Due)
            .Permit([InvoiceState.Due, InvoiceState.PartiallyPaid, InvoiceState.Overdue], InvoiceTrigger.AllocateInFull, InvoiceState.Paid)
            .Permit(InvoiceState.Due, InvoiceTrigger.AllocatePartially, InvoiceState.PartiallyPaid)
            .Permit([InvoiceState.Due, InvoiceState.PartiallyPaid], InvoiceTrigger.BecomeOverdue, InvoiceState.Overdue)
            .Permit(InvoiceState.Overdue, InvoiceTrigger.WriteOff, InvoiceState.WrittenOff)
            .Permit(InvoiceState.Paid, InvoiceTrigger.PaymentReversedBeforeDue, InvoiceState.Due)
            .Permit(InvoiceState.Paid, InvoiceTrigger.PaymentReversedAfterDue, InvoiceState.Overdue)
            .Permit([InvoiceState.Billed, InvoiceState.Due], InvoiceTrigger.Reverse, InvoiceState.Reversed)
            .Terminal(InvoiceState.WrittenOff, InvoiceState.Reversed)
            .Build();
}

/// <summary>Incoming payment (receipt) states (contract §3.2.4, PRD-18 §9.2.4, PRD-06 §7.3).</summary>
public enum PaymentState
{
    /// <summary>Received (<c>PaymentReceived</c>).</summary>
    Received,

    /// <summary>Fully allocated.</summary>
    Allocated,

    /// <summary>Partly allocated.</summary>
    PartiallyAllocated,

    /// <summary>Unapplied cash.</summary>
    Suspense,

    /// <summary>Reversed (<c>PaymentReversed</c>).</summary>
    Reversed,

    /// <summary>Refunded to the payer.</summary>
    Refunded,
}

/// <summary>Triggers of <see cref="PaymentState"/>.</summary>
public enum PaymentTrigger
{
    /// <summary>The whole amount is allocated.</summary>
    AllocateInFull,

    /// <summary>Part of the amount is allocated.</summary>
    AllocatePartially,

    /// <summary>The (remaining) amount goes to suspense.</summary>
    MoveToSuspense,

    /// <summary>The payment is reversed (bank return, chargeback, …).</summary>
    Reverse,

    /// <summary>The payment (or the overpaid part) is refunded.</summary>
    Refund,
}

/// <summary>The canonical incoming Payment lifecycle (PRD-18 edges plus the PRD-06 edges between the same states).</summary>
public static class PaymentStateModel
{
    /// <summary>The table.</summary>
    public static StateMachine<PaymentState, PaymentTrigger> Machine { get; } =
        StateMachine.Define<PaymentState, PaymentTrigger>(ModuleCode.BIL, "Payment")
            .Initial(PaymentState.Received)
            .Permit([PaymentState.Received, PaymentState.PartiallyAllocated, PaymentState.Suspense], PaymentTrigger.AllocateInFull, PaymentState.Allocated)
            .Permit([PaymentState.Received, PaymentState.Suspense], PaymentTrigger.AllocatePartially, PaymentState.PartiallyAllocated)
            .Permit([PaymentState.Received, PaymentState.PartiallyAllocated], PaymentTrigger.MoveToSuspense, PaymentState.Suspense)
            .Permit([PaymentState.Allocated, PaymentState.PartiallyAllocated], PaymentTrigger.Reverse, PaymentState.Reversed)
            .Permit([PaymentState.Suspense, PaymentState.Allocated], PaymentTrigger.Refund, PaymentState.Refunded)
            .Terminal(PaymentState.Reversed, PaymentState.Refunded)
            .Build();
}

/// <summary>Refund states (PRD-06 §7.3; PRD-18 §9 canonical by reference). Requester ≠ approver.</summary>
public enum RefundState
{
    /// <summary>Proposed.</summary>
    Proposed,

    /// <summary>Waiting for approval (maker-checker, authority).</summary>
    PendingApproval,

    /// <summary>Approved (<c>RefundApproved</c>).</summary>
    Approved,

    /// <summary>In the refund-hold window.</summary>
    Held,

    /// <summary>A disbursement is in progress.</summary>
    Disbursing,

    /// <summary>Paid via an intermediary; waiting for proof.</summary>
    AwaitingProof,

    /// <summary>Paid (<c>RefundDisbursed</c>, D-CON-10).</summary>
    Paid,

    /// <summary>Rejected (<c>RefundRejected</c>).</summary>
    Rejected,

    /// <summary>The disbursement came back; the refund is re-proposed.</summary>
    Returned,
}

/// <summary>Triggers of <see cref="RefundState"/>.</summary>
public enum RefundTrigger
{
    /// <summary>Submitted for approval.</summary>
    Submit,

    /// <summary>Approved.</summary>
    Approve,

    /// <summary>Rejected.</summary>
    Reject,

    /// <summary>Put on hold (refund-hold window).</summary>
    Hold,

    /// <summary>Hold released.</summary>
    Release,

    /// <summary>A disbursement is requested.</summary>
    Disburse,

    /// <summary>Paid via an intermediary; proof outstanding.</summary>
    AwaitProof,

    /// <summary>Paid.</summary>
    Pay,

    /// <summary>The disbursement was returned.</summary>
    Return,

    /// <summary>A returned refund is proposed again.</summary>
    Repropose,
}

/// <summary>The canonical Refund lifecycle.</summary>
public static class RefundStateModel
{
    /// <summary>The table.</summary>
    public static StateMachine<RefundState, RefundTrigger> Machine { get; } =
        StateMachine.Define<RefundState, RefundTrigger>(ModuleCode.BIL, "Refund")
            .Initial(RefundState.Proposed)
            .Permit(RefundState.Proposed, RefundTrigger.Submit, RefundState.PendingApproval)
            .Permit(RefundState.PendingApproval, RefundTrigger.Approve, RefundState.Approved)
            .Permit([RefundState.Proposed, RefundState.PendingApproval], RefundTrigger.Reject, RefundState.Rejected)
            .Permit(RefundState.Approved, RefundTrigger.Hold, RefundState.Held)
            .Permit(RefundState.Held, RefundTrigger.Release, RefundState.Approved)
            .Permit(RefundState.Approved, RefundTrigger.Disburse, RefundState.Disbursing)
            .Permit(RefundState.Disbursing, RefundTrigger.AwaitProof, RefundState.AwaitingProof)
            .Permit([RefundState.Disbursing, RefundState.AwaitingProof], RefundTrigger.Pay, RefundState.Paid)
            .Permit(RefundState.Disbursing, RefundTrigger.Return, RefundState.Returned)
            .Permit(RefundState.Returned, RefundTrigger.Repropose, RefundState.Proposed)
            .Terminal(RefundState.Paid, RefundState.Rejected)
            .Build();
}

/// <summary>Disbursement (outgoing payment) states (contract §3.2.4, PRD-18 §9.2.5, PRD-06 §7.3, D4).</summary>
public enum DisbursementState
{
    /// <summary>Requested by the source module (refund, claim payment, RI settlement, …).</summary>
    Requested,

    /// <summary>Holds and approvals (sanctions, VoP, duplicate, bank change four-eyes).</summary>
    PendingApproval,

    /// <summary>Approved.</summary>
    Approved,

    /// <summary>Released in a payment batch.</summary>
    Released,

    /// <summary>Accepted by the bank (<c>DisbursementIssued</c>).</summary>
    Issued,

    /// <summary>Debit matched on the bank statement (<c>DisbursementCleared</c>).</summary>
    Cleared,

    /// <summary>Rejected by an approver or the bank (<c>DisbursementRejected</c>).</summary>
    Rejected,

    /// <summary>Stopped before release (<c>DisbursementStopped</c>).</summary>
    Stopped,

    /// <summary>Recalled after issue (<c>DisbursementVoided</c>).</summary>
    Voided,

    /// <summary>Returned by the beneficiary bank (<c>DisbursementReturned</c>).</summary>
    Returned,
}

/// <summary>Triggers of <see cref="DisbursementState"/>.</summary>
public enum DisbursementTrigger
{
    /// <summary>Enters approval and hold checks.</summary>
    EnterApproval,

    /// <summary>Approved.</summary>
    Approve,

    /// <summary>Rejected by the approver (from PendingApproval) or the bank (from Released).</summary>
    Reject,

    /// <summary>Stopped.</summary>
    Stop,

    /// <summary>Released in a batch.</summary>
    Release,

    /// <summary>Bank acknowledgement.</summary>
    Issue,

    /// <summary>Statement debit matched.</summary>
    Clear,

    /// <summary>Recall.</summary>
    Void,

    /// <summary>Beneficiary bank return (also late, after clearing).</summary>
    Return,
}

/// <summary>The canonical Disbursement lifecycle.</summary>
public static class DisbursementStateModel
{
    /// <summary>The table.</summary>
    public static StateMachine<DisbursementState, DisbursementTrigger> Machine { get; } =
        StateMachine.Define<DisbursementState, DisbursementTrigger>(ModuleCode.BIL, "Disbursement")
            .Initial(DisbursementState.Requested)
            .Permit(DisbursementState.Requested, DisbursementTrigger.EnterApproval, DisbursementState.PendingApproval)
            .Permit(DisbursementState.PendingApproval, DisbursementTrigger.Approve, DisbursementState.Approved)
            .Permit([DisbursementState.PendingApproval, DisbursementState.Released], DisbursementTrigger.Reject, DisbursementState.Rejected)
            .Permit([DisbursementState.PendingApproval, DisbursementState.Approved], DisbursementTrigger.Stop, DisbursementState.Stopped)
            .Permit(DisbursementState.Approved, DisbursementTrigger.Release, DisbursementState.Released)
            .Permit(DisbursementState.Released, DisbursementTrigger.Issue, DisbursementState.Issued)
            .Permit(DisbursementState.Issued, DisbursementTrigger.Clear, DisbursementState.Cleared)
            .Permit(DisbursementState.Issued, DisbursementTrigger.Void, DisbursementState.Voided)
            .Permit([DisbursementState.Issued, DisbursementState.Cleared], DisbursementTrigger.Return, DisbursementState.Returned)
            .Terminal(DisbursementState.Rejected, DisbursementState.Stopped, DisbursementState.Voided, DisbursementState.Returned)
            .Build();
}
