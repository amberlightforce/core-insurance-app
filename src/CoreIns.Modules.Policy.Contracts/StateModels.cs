using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.StateMachines;

namespace CoreIns.Modules.Policy.Contracts;

/// <summary>Job states (contract §3.2.4 Job, ruling D-CON-08b). Referred and Preempted are flags, not states; Issued is not a job state.</summary>
public enum JobState
{
    /// <summary>Being prepared (sub-states QuickQuote, Converting).</summary>
    Draft,

    /// <summary>Rated and UW-evaluated; can be bound (sub-states Offered, Accepted for renewals).</summary>
    Quoted,

    /// <summary>Bound (bind gates passed). Issued is not a job state.</summary>
    Bound,

    /// <summary>Future-effective cancellation or renewal waiting for its effective time (Quoted → Scheduled → Bound or Rescinded).</summary>
    Scheduled,

    /// <summary>A scheduled job was rescinded (<c>CancellationRescinded</c>).</summary>
    Rescinded,

    /// <summary>Withdrawn (<c>JobWithdrawn</c>).</summary>
    Withdrawn,

    /// <summary>Declined by underwriting (<c>DeclineIssued</c>).</summary>
    Declined,

    /// <summary>Not taken up by the customer (<c>JobNotTaken</c>).</summary>
    NotTaken,

    /// <summary>Draft inactivity or quote validity ended (<c>QuoteExpired</c>).</summary>
    Expired,
}

/// <summary>Triggers of <see cref="JobState"/>.</summary>
public enum JobTrigger
{
    /// <summary>Rating and UW evaluation passed (<c>QuoteIssued</c>).</summary>
    Quote,

    /// <summary>The quoted job is edited (new quote version; the previous one is superseded).</summary>
    Edit,

    /// <summary>Bind gates passed (<c>PolicyBound</c> / <c>RenewalBound</c>).</summary>
    Bind,

    /// <summary>A quoted future-effective cancellation or renewal is scheduled for its effective time.</summary>
    Schedule,

    /// <summary>The scheduled job's effective time is reached or it is accepted: it binds.</summary>
    Activate,

    /// <summary>The scheduled job is rescinded.</summary>
    Rescind,

    /// <summary>Withdrawn by the user or channel.</summary>
    Withdraw,

    /// <summary>Declined by underwriting.</summary>
    Decline,

    /// <summary>Customer declines or the acceptance deadline passes.</summary>
    NotTake,

    /// <summary>Inactivity (Draft) or validity end (Quoted).</summary>
    Expire,
}

/// <summary>The canonical Job lifecycle.</summary>
public static class JobStateModel
{
    /// <summary>
    /// The table (contract §3.2.4, ruling D-CON-08b): Draft → Quoted → Bound; Quoted → Scheduled for a future-effective
    /// cancellation or renewal, then Scheduled → Bound or Rescinded; terminal Withdrawn, Declined, NotTaken, Expired.
    /// Quoted → Draft (edit, new quote version) is PRD-18 §9.2.1.
    /// </summary>
    public static StateMachine<JobState, JobTrigger> Machine { get; } =
        StateMachine.Define<JobState, JobTrigger>(ModuleCode.POL, "Job")
            .Initial(JobState.Draft)
            .Permit(JobState.Draft, JobTrigger.Quote, JobState.Quoted)
            .Permit(JobState.Quoted, JobTrigger.Edit, JobState.Draft)
            .Permit(JobState.Quoted, JobTrigger.Bind, JobState.Bound)
            .Permit(JobState.Quoted, JobTrigger.Schedule, JobState.Scheduled)
            .Permit(JobState.Scheduled, JobTrigger.Activate, JobState.Bound)
            .Permit(JobState.Scheduled, JobTrigger.Rescind, JobState.Rescinded)
            .Permit([JobState.Draft, JobState.Quoted], JobTrigger.Withdraw, JobState.Withdrawn)
            .Permit([JobState.Draft, JobState.Quoted], JobTrigger.Decline, JobState.Declined)
            .Permit(JobState.Quoted, JobTrigger.NotTake, JobState.NotTaken)
            .Permit([JobState.Draft, JobState.Quoted], JobTrigger.Expire, JobState.Expired)
            .Terminal(JobState.Bound, JobState.Rescinded, JobState.Withdrawn, JobState.Declined, JobState.NotTaken, JobState.Expired)
            .Build();
}

/// <summary>Quote (quote version) states (PRD-18 §9.2.1: a quote = Job + QuoteVersion; only a Quoted version can be bound).</summary>
public enum QuoteState
{
    /// <summary>Being prepared.</summary>
    Draft,

    /// <summary>Priced and evaluated; DOC renders DT-QUOTE from it; RAT pins the artefact until expiry.</summary>
    Quoted,

    /// <summary>Replaced by a newer version of the same job.</summary>
    Superseded,

    /// <summary>Validity ended (or the job expired).</summary>
    Expired,
}

/// <summary>Triggers of <see cref="QuoteState"/>.</summary>
public enum QuoteTrigger
{
    /// <summary>Rated and evaluated.</summary>
    Quote,

    /// <summary>A newer version replaces this one.</summary>
    Supersede,

    /// <summary>Validity ends.</summary>
    Expire,
}

/// <summary>The canonical Quote (quote version) lifecycle.</summary>
public static class QuoteStateModel
{
    /// <summary>The table (PRD-18 §9.2.1 QuoteVersion states Draft, Quoted, Superseded, Expired).</summary>
    public static StateMachine<QuoteState, QuoteTrigger> Machine { get; } =
        StateMachine.Define<QuoteState, QuoteTrigger>(ModuleCode.POL, "Quote")
            .Initial(QuoteState.Draft)
            .Permit(QuoteState.Draft, QuoteTrigger.Quote, QuoteState.Quoted)
            .Permit(QuoteState.Quoted, QuoteTrigger.Supersede, QuoteState.Superseded)
            .Permit([QuoteState.Draft, QuoteState.Quoted], QuoteTrigger.Expire, QuoteState.Expired)
            .Terminal(QuoteState.Superseded, QuoteState.Expired)
            .Build();
}

/// <summary>PolicyTerm states (contract §3.2.4, PRD-18 §9.2.2, D-CON-02). Suspension is a transaction kind, never a term state.</summary>
public enum PolicyTermState
{
    /// <summary>Bound, not yet effective.</summary>
    Scheduled,

    /// <summary>In force.</summary>
    InForce,

    /// <summary>A cancellation is scheduled (<c>CancellationScheduled</c>).</summary>
    PendingCancellation,

    /// <summary>Cancelled (<c>PolicyCancelled</c>); can be reinstated or rescinded.</summary>
    Cancelled,

    /// <summary>The term end was reached (outcome Renewed / NonRenewed / Lapsed / Rewritten on the next-term job).</summary>
    Expired,

    /// <summary>Voided ab initio (<c>PolicyVoided</c>, D-CON-02).</summary>
    Voided,

    /// <summary>Rewritten (<c>PolicyRewritten</c>, D-CON-02).</summary>
    Rewritten,
}

/// <summary>Triggers of <see cref="PolicyTermState"/>.</summary>
public enum PolicyTermTrigger
{
    /// <summary>The term start is reached (derived, no event, F-204).</summary>
    StartReached,

    /// <summary>A future-effective cancellation is bound.</summary>
    ScheduleCancellation,

    /// <summary>A scheduled or effected cancellation is rescinded (D-CON-02 rescission).</summary>
    Rescind,

    /// <summary>The scheduled cancellation's effective time is reached.</summary>
    CancellationEffective,

    /// <summary>A cancellation effective now or earlier (including flat cancellation of a scheduled term).</summary>
    Cancel,

    /// <summary>Void after objection or withdrawal.</summary>
    Void,

    /// <summary>The term is rewritten.</summary>
    Rewrite,

    /// <summary>Reinstatement (<c>PolicyReinstated</c>).</summary>
    Reinstate,

    /// <summary>The term end is reached.</summary>
    Expire,

    /// <summary>A change to a prior (expired) term (<c>PolicyChanged</c>).</summary>
    ChangePriorTerm,
}

/// <summary>The canonical PolicyTerm lifecycle.</summary>
public static class PolicyTermStateModel
{
    /// <summary>The table.</summary>
    public static StateMachine<PolicyTermState, PolicyTermTrigger> Machine { get; } =
        StateMachine.Define<PolicyTermState, PolicyTermTrigger>(ModuleCode.POL, "PolicyTerm")
            .Initial(PolicyTermState.Scheduled, PolicyTermState.InForce)
            .Permit(PolicyTermState.Scheduled, PolicyTermTrigger.StartReached, PolicyTermState.InForce)
            .Permit(PolicyTermState.Scheduled, PolicyTermTrigger.Cancel, PolicyTermState.Cancelled)
            .Permit(PolicyTermState.Scheduled, PolicyTermTrigger.Void, PolicyTermState.Voided)
            .Permit(PolicyTermState.Scheduled, PolicyTermTrigger.Rewrite, PolicyTermState.Rewritten)
            .Permit(PolicyTermState.InForce, PolicyTermTrigger.ScheduleCancellation, PolicyTermState.PendingCancellation)
            .Permit(PolicyTermState.PendingCancellation, PolicyTermTrigger.Rescind, PolicyTermState.InForce)
            .Permit(PolicyTermState.PendingCancellation, PolicyTermTrigger.CancellationEffective, PolicyTermState.Cancelled)
            .Permit(PolicyTermState.InForce, PolicyTermTrigger.Cancel, PolicyTermState.Cancelled)
            .Permit(PolicyTermState.InForce, PolicyTermTrigger.Void, PolicyTermState.Voided)
            .Permit(PolicyTermState.InForce, PolicyTermTrigger.Rewrite, PolicyTermState.Rewritten)
            .Permit(PolicyTermState.Cancelled, PolicyTermTrigger.Reinstate, PolicyTermState.InForce)
            .Permit(PolicyTermState.Cancelled, PolicyTermTrigger.Rescind, PolicyTermState.InForce)
            .Permit(PolicyTermState.InForce, PolicyTermTrigger.Expire, PolicyTermState.Expired)
            .Permit(PolicyTermState.Expired, PolicyTermTrigger.ChangePriorTerm, PolicyTermState.Expired)
            .Terminal(PolicyTermState.Voided, PolicyTermState.Rewritten)
            .Build();
}

/// <summary>PolicyTransaction states (PRD-18 §9.2.3). A reversed row is never updated in place by the domain; Reversed is derived from the link.</summary>
public enum PolicyTransactionState
{
    /// <summary>Committed with a bound job.</summary>
    Bound,

    /// <summary>A later Reversal transaction links to it (<c>TransactionReversed</c>).</summary>
    Reversed,
}

/// <summary>Triggers of <see cref="PolicyTransactionState"/>.</summary>
public enum PolicyTransactionTrigger
{
    /// <summary>A reversal transaction links to this one (out-of-sequence reverse-and-reapply).</summary>
    Reverse,
}

/// <summary>Kinds of policy transaction (PRD-18 §9.2.3). Reapplications are new Bound rows of the original kind.</summary>
public enum PolicyTransactionKind
{
    /// <summary>First issuance.</summary>
    Issuance,

    /// <summary>Mid-term change.</summary>
    Change,

    /// <summary>Cancellation.</summary>
    Cancellation,

    /// <summary>Reinstatement.</summary>
    Reinstatement,

    /// <summary>Rewrite.</summary>
    Rewrite,

    /// <summary>Renewal.</summary>
    Renewal,

    /// <summary>Carry-forward of a change into the next term.</summary>
    CarryForward,

    /// <summary>Suspension (a transaction kind, never a term state, REQ-POL-236).</summary>
    Suspension,

    /// <summary>Reactivation after suspension.</summary>
    Reactivation,

    /// <summary>Void.</summary>
    Void,

    /// <summary>Reversal of an earlier transaction.</summary>
    Reversal,
}

/// <summary>The canonical PolicyTransaction lifecycle.</summary>
public static class PolicyTransactionStateModel
{
    /// <summary>The table.</summary>
    public static StateMachine<PolicyTransactionState, PolicyTransactionTrigger> Machine { get; } =
        StateMachine.Define<PolicyTransactionState, PolicyTransactionTrigger>(ModuleCode.POL, "PolicyTransaction")
            .Initial(PolicyTransactionState.Bound)
            .Permit(PolicyTransactionState.Bound, PolicyTransactionTrigger.Reverse, PolicyTransactionState.Reversed)
            .Terminal(PolicyTransactionState.Reversed)
            .Build();
}
