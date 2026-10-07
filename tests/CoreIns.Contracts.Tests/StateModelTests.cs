using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Claims.Contracts;
using CoreIns.Modules.Compliance.Contracts;
using CoreIns.Modules.Documents.Contracts;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Work.Contracts;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.StateMachines;

namespace CoreIns.Contracts.Tests;

/// <summary>
/// The canonical state models: each builds (the builder refuses inconsistent tables), names its owner, and contains
/// the transitions its source states — and nothing that the source forbids.
/// </summary>
public sealed class StateModelTests
{
    [Fact]
    public void Job_follows_contract_3_2_4_prd18_9_2_1_and_d_con_08()
    {
        var m = JobStateModel.Machine;
        Owned(m, ModuleCode.POL);
        m.InitialStates.ShouldBe([JobState.Draft]);
        Allows(m, JobState.Draft, JobTrigger.Quote, JobState.Quoted);
        Allows(m, JobState.Quoted, JobTrigger.Bind, JobState.Bound);
        Allows(m, JobState.Bound, JobTrigger.Schedule, JobState.Scheduled);
        Allows(m, JobState.Scheduled, JobTrigger.Rescind, JobState.Rescinded);
        Forbids(m, JobState.Quoted, JobTrigger.Schedule);
        Forbids(m, JobState.Draft, JobTrigger.Bind);
        Forbids(m, JobState.Draft, JobTrigger.NotTake);
        m.TerminalStates.ShouldBe([JobState.Rescinded, JobState.Withdrawn, JobState.Declined, JobState.NotTaken, JobState.Expired], ignoreOrder: true);
    }

    [Fact]
    public void Quote_versions_are_superseded_or_expire()
    {
        var m = QuoteStateModel.Machine;
        Owned(m, ModuleCode.POL);
        Allows(m, QuoteState.Draft, QuoteTrigger.Quote, QuoteState.Quoted);
        Allows(m, QuoteState.Quoted, QuoteTrigger.Supersede, QuoteState.Superseded);
        Forbids(m, QuoteState.Superseded, QuoteTrigger.Quote);
    }

    [Fact]
    public void Policy_term_has_void_rewrite_and_rescission_per_d_con_02()
    {
        var m = PolicyTermStateModel.Machine;
        Owned(m, ModuleCode.POL);
        m.InitialStates.ShouldBe([PolicyTermState.Scheduled, PolicyTermState.InForce], ignoreOrder: true);
        Allows(m, PolicyTermState.InForce, PolicyTermTrigger.ScheduleCancellation, PolicyTermState.PendingCancellation);
        Allows(m, PolicyTermState.PendingCancellation, PolicyTermTrigger.Rescind, PolicyTermState.InForce);
        Allows(m, PolicyTermState.Cancelled, PolicyTermTrigger.Rescind, PolicyTermState.InForce);
        Allows(m, PolicyTermState.Cancelled, PolicyTermTrigger.Reinstate, PolicyTermState.InForce);
        Allows(m, PolicyTermState.InForce, PolicyTermTrigger.Void, PolicyTermState.Voided);
        Allows(m, PolicyTermState.InForce, PolicyTermTrigger.Rewrite, PolicyTermState.Rewritten);
        Allows(m, PolicyTermState.Expired, PolicyTermTrigger.ChangePriorTerm, PolicyTermState.Expired);
        Forbids(m, PolicyTermState.Expired, PolicyTermTrigger.Reinstate);
        m.TerminalStates.ShouldBe([PolicyTermState.Voided, PolicyTermState.Rewritten], ignoreOrder: true);
    }

    [Fact]
    public void Policy_transactions_are_only_reversed()
    {
        var m = PolicyTransactionStateModel.Machine;
        Allows(m, PolicyTransactionState.Bound, PolicyTransactionTrigger.Reverse, PolicyTransactionState.Reversed);
        Forbids(m, PolicyTransactionState.Reversed, PolicyTransactionTrigger.Reverse);
        m.Transitions.Count.ShouldBe(1);
    }

    [Fact]
    public void Invoice_payment_refund_and_disbursement_follow_billing_models()
    {
        Owned(InvoiceStateModel.Machine, ModuleCode.BIL);
        Allows(InvoiceStateModel.Machine, InvoiceState.Paid, InvoiceTrigger.PaymentReversedAfterDue, InvoiceState.Overdue);
        Allows(InvoiceStateModel.Machine, InvoiceState.Overdue, InvoiceTrigger.WriteOff, InvoiceState.WrittenOff);
        Forbids(InvoiceStateModel.Machine, InvoiceState.Due, InvoiceTrigger.WriteOff);

        Allows(PaymentStateModel.Machine, PaymentState.Suspense, PaymentTrigger.Refund, PaymentState.Refunded);
        Forbids(PaymentStateModel.Machine, PaymentState.Refunded, PaymentTrigger.AllocateInFull);

        Allows(RefundStateModel.Machine, RefundState.Disbursing, RefundTrigger.Return, RefundState.Returned);
        Allows(RefundStateModel.Machine, RefundState.Returned, RefundTrigger.Repropose, RefundState.Proposed);
        Forbids(RefundStateModel.Machine, RefundState.Proposed, RefundTrigger.Pay);

        var d = DisbursementStateModel.Machine;
        Allows(d, DisbursementState.Released, DisbursementTrigger.Reject, DisbursementState.Rejected);
        Allows(d, DisbursementState.Cleared, DisbursementTrigger.Return, DisbursementState.Returned);
        Forbids(d, DisbursementState.Released, DisbursementTrigger.Stop);
        d.TerminalStates.ShouldBe(
            [DisbursementState.Rejected, DisbursementState.Stopped, DisbursementState.Voided, DisbursementState.Returned], ignoreOrder: true);
    }

    [Fact]
    public void Claims_models_follow_contract_and_prd_07()
    {
        Owned(ClaimStateModel.Machine, ModuleCode.CLM);
        Allows(ClaimStateModel.Machine, ClaimState.Closed, ClaimTrigger.Reopen, ClaimState.Open);
        Allows(ExposureStateModel.Machine, ExposureState.Closed, ExposureTrigger.Reopen, ExposureState.Open);
        Allows(ReserveLineStateModel.Machine, ReserveLineState.FinalLine, ReserveLineTrigger.Reopen, ReserveLineState.OpenLine);
        Allows(TransactionSetStateModel.Machine, TransactionSetState.Approved, TransactionSetTrigger.Post, TransactionSetState.Posted);
        Forbids(TransactionSetStateModel.Machine, TransactionSetState.Draft, TransactionSetTrigger.Approve);

        var p = ClaimPaymentStateModel.Machine;
        Allows(p, ClaimPaymentState.Submitted, ClaimPaymentTrigger.RejectDisbursement, ClaimPaymentState.DisbursementRejected);
        Allows(p, ClaimPaymentState.Cleared, ClaimPaymentTrigger.Return, ClaimPaymentState.Returned);
        Forbids(p, ClaimPaymentState.Pending, ClaimPaymentTrigger.Submit);
    }

    [Theory]
    [InlineData(StatutoryClockKind.Deadline, StatutoryClockState.Breached)]
    [InlineData(StatutoryClockKind.FixedDate, StatutoryClockState.Breached)]
    [InlineData(StatutoryClockKind.WaitingPeriod, StatutoryClockState.Elapsed)]
    public void Statutory_clocks_breach_or_elapse_by_kind_per_r_78(StatutoryClockKind kind, StatutoryClockState expiry)
    {
        var m = StatutoryClockStateModel.For(kind);
        Owned(m, ModuleCode.CMP);
        Allows(m, StatutoryClockState.Running, StatutoryClockTrigger.Expire, expiry);
        Allows(m, StatutoryClockState.Warned, StatutoryClockTrigger.Stop, StatutoryClockState.Met);
        Allows(m, StatutoryClockState.Paused, StatutoryClockTrigger.Cancel, StatutoryClockState.Cancelled);
        Forbids(m, StatutoryClockState.Paused, StatutoryClockTrigger.Expire);
        var other = expiry == StatutoryClockState.Breached ? StatutoryClockState.Elapsed : StatutoryClockState.Breached;
        m.States.ShouldNotContain(other);
        m.Transitions.ShouldNotContain(t => t.To == other);
    }

    [Fact]
    public void Product_pack_activity_and_document_models_build()
    {
        Owned(ProductVersionStateModel.Machine, ModuleCode.PFC);
        Allows(ProductVersionStateModel.Machine, ProductVersionState.Approved, ProductVersionTrigger.Return, ProductVersionState.Draft);
        Forbids(ProductVersionStateModel.Machine, ProductVersionState.Locked, ProductVersionTrigger.Return);
        Allows(ProductVersionLockedStateModel.Machine, ProductVersionLockedSubstate.Active, ProductVersionLockedTrigger.CloseToNewBusiness, ProductVersionLockedSubstate.ClosedToNewBusiness);

        Owned(PackStateModel.Machine, ModuleCode.MKT);
        Allows(PackStateModel.Machine, PackState.Signed, PackTrigger.Reject, PackState.Rejected);
        Allows(PackActivationStateModel.Machine, PackActivationState.Scheduled, PackActivationTrigger.Withdraw, PackActivationState.Withdrawn);

        Owned(ActivityStateModel.Machine, ModuleCode.WRK);
        ActivityStateModel.Machine.TerminalStates.Count.ShouldBe(3);

        Owned(DocumentStateModel.Machine, ModuleCode.DOC);
        Allows(DocumentStateModel.Machine, DocumentState.Failed, DocumentTrigger.StartRendering, DocumentState.Rendering);
        Forbids(DocumentStateModel.Machine, DocumentState.Rendered, DocumentTrigger.Fail);
        Allows(DeliveryStateModel.Machine, DeliveryState.Sent, DeliveryTrigger.Bounce, DeliveryState.Bounced);
    }

    [Fact]
    public void Check_constraints_list_the_canonical_state_names() =>
        PolicyTermStateModel.Machine.CheckConstraintSql("status")
            .ShouldBe("status IN ('Scheduled', 'InForce', 'PendingCancellation', 'Cancelled', 'Expired', 'Voided', 'Rewritten')");

    private static void Owned<TState, TTrigger>(StateMachine<TState, TTrigger> machine, ModuleCode owner)
        where TState : struct, Enum
        where TTrigger : struct, Enum
    {
        machine.Owner.ShouldBe(owner);
        machine.RejectionCode.Value.ShouldBe($"{owner}-ERR-INVALID-STATE-TRANSITION");
    }

    private static void Allows<TState, TTrigger>(StateMachine<TState, TTrigger> machine, TState from, TTrigger trigger, TState to)
        where TState : struct, Enum
        where TTrigger : struct, Enum =>
        machine.Fire(from, trigger).Value.ShouldBe(to);

    private static void Forbids<TState, TTrigger>(StateMachine<TState, TTrigger> machine, TState from, TTrigger trigger)
        where TState : struct, Enum
        where TTrigger : struct, Enum =>
        machine.Fire(from, trigger).IsFailure.ShouldBeTrue($"{machine.Name}: {from} --{trigger}--> must be rejected");
}
