using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Services;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Billing.Commands;

/// <summary>
/// <c>bil.Term.stopBilling</c> (internal, from <c>pol.PolicyCancelled</c>, REQ-BIL-074): records the cancellation on the
/// term's plan instance and stops the planned items of the cancelled term from the effective date (item state
/// Cancelled). The credit itself arrives as negative charge deltas (<c>ChargeDeltaEmitted</c>) and is billed at once.
/// Idempotent: a replay changes nothing. ANNUAL plans have no planned items (everything is billed when written), so the
/// stop usually finds none; the mark still prevents a new debit on the cancelled term.
/// </summary>
internal sealed record StopTermBilling(PolicyCancelledV1 Cancelled, EventSource Source) : ICommand<IntakeOutcome>;

internal sealed class StopTermBillingHandler(BillingDbContext db, TermBilling billing) : ICommandHandler<StopTermBilling, IntakeOutcome>
{
    public async Task<Result<IntakeOutcome>> HandleAsync(StopTermBilling command, CancellationToken cancellationToken)
    {
        var cancelled = command.Cancelled;
        await billing.LockTermAsync(cancelled.TermId, cancellationToken).ConfigureAwait(false);

        // A term the slice does not bill (no plan instance) cannot be stopped: fail closed so the event is retried and parked.
        var plan = await db.PlanInstances.SingleOrDefaultAsync(p => p.TermId == cancelled.TermId, cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"PolicyCancelled for term {cancelled.TermId.Value} that billing has not attached (no plan instance).");
        if (plan.CancelledEffective is not null)
        {
            return new IntakeOutcome(false, 0);
        }

        plan.CancelledEffective = cancelled.EffectiveDate;
        plan.CancellationSource = cancelled.Source;
        var planned = Codes.Of(InvoiceItemState.Planned);
        var stopped = await db.InvoiceItems.Where(i => i.TermId == cancelled.TermId && i.State == planned && i.ValidFrom >= cancelled.EffectiveDate)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in stopped)
        {
            item.State = Codes.Of(InvoiceItemState.Cancelled);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new IntakeOutcome(true, stopped.Count);
    }
}

internal sealed class StopTermBillingAuditor : ICommandAuditor<StopTermBilling, IntakeOutcome>
{
    public CommandAuditFacts Describe(StopTermBilling command, Result<IntakeOutcome>? result) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.POL, "PolicyTerm", command.Cancelled.TermId),
        BusinessKeys = BusinessKeys.Empty.With("policyTermId", command.Cancelled.TermId.Value.ToString())
            .With("transactionId", command.Cancelled.TransactionId.Value.ToString()),
        Changes = result is { IsSuccess: true } ok
            ? AuditDiff.Compute(null, new { applied = ok.Value.Applied, itemsStopped = ok.Value.InvoicesCreated, source = command.Cancelled.Source })
            : [],
    };
}

/// <summary>
/// <c>bil.BillingAccount.attachRenewalTerm</c> (internal, from <c>pol.RenewalBound</c>, REQ-BIL-002 subset): attaches term
/// n+1 to the billing account of the expiring term it continues (the payer comes from the account, never from the event)
/// with the same plan and method, then lets the term progress, so its charges are invoiced like a new bind. The event
/// carries no term period: the renewal term starts where the expiring one ends and runs one year (annual motor terms);
/// charges outside that period are quarantined (PERIOD-OUTSIDE-TERM), so a different period fails closed.
/// Idempotent on the term id.
/// </summary>
internal sealed record AttachRenewalTerm(RenewalBoundV1 Bound, PolicyId PolicyId, EventSource Source) : ICommand<IntakeOutcome>;

internal sealed class AttachRenewalTermHandler(
    BillingDbContext db,
    IClock clock,
    IEventPublisher events,
    TermBilling billing) : ICommandHandler<AttachRenewalTerm, IntakeOutcome>
{
    public async Task<Result<IntakeOutcome>> HandleAsync(AttachRenewalTerm command, CancellationToken cancellationToken)
    {
        var bound = command.Bound;
        await billing.LockTermAsync(bound.NewTermId, cancellationToken).ConfigureAwait(false);
        if (await db.PlanInstances.AnyAsync(p => p.TermId == bound.NewTermId, cancellationToken).ConfigureAwait(false))
        {
            return new IntakeOutcome(false, await billing.ProgressAsync(bound.NewTermId, cancellationToken).ConfigureAwait(false));
        }

        // PITFALLS 10/12: the contract says predecessorTermId is always set; a missing or unattached predecessor is not guessed.
        var predecessorId = bound.PredecessorTermId is { } id
            ? new PolicyTermId(id)
            : throw new InvalidOperationException("RenewalBound without predecessorTermId (contract: always set).");
        var predecessor = await db.PlanInstances.SingleOrDefaultAsync(p => p.TermId == predecessorId, cancellationToken).ConfigureAwait(false)
                          ?? throw new InvalidOperationException($"RenewalBound for a term whose predecessor {predecessorId.Value} billing has not attached.");
        var account = await db.Accounts.SingleAsync(a => a.BillingAccountId == predecessor.BillingAccountId, cancellationToken).ConfigureAwait(false);
        if (account.Status != Codes.Of(BillingAccountStatus.Active))
        {
            throw new InvalidOperationException($"Billing account {account.AccountNumber} is {account.Status}; the renewal term is not attached.");
        }

        var now = clock.Now;
        db.PlanInstances.Add(new PlanInstanceRow
        {
            TermId = bound.NewTermId,
            PlanInstanceId = Guid.CreateVersion7(),
            BillingAccountId = account.BillingAccountId,
            LegalEntityId = account.LegalEntityId,
            PolicyId = command.PolicyId,
            PolicyNumber = predecessor.PolicyNumber,
            TermNumber = bound.NewTermNumber,
            BoundTransactionId = bound.TransactionId,
            ProductCode = bound.ProductCode,
            ProductVersion = bound.ProductVersion.ToString(),
            ArtefactHash = bound.ArtefactHash.Value,
            PlanCode = predecessor.PlanCode,
            BillMode = predecessor.BillMode,
            Method = predecessor.Method,
            TermFrom = predecessor.TermTo,
            TermTo = predecessor.TermTo.AddYears(1),
            SourceEventId = command.Source.EventId,
            SourceSequence = command.Source.AggregateSequence,
            CreatedAt = now,
            PredecessorTermId = predecessorId,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var termIds = await db.PlanInstances.Where(p => p.BillingAccountId == account.BillingAccountId).Select(p => p.TermId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(BillingAccountChangedV1.Descriptor), "BillingAccount", account.BillingAccountId.Value.ToString(),
            new BillingAccountChangedV1
            {
                BillingAccountId = account.BillingAccountId,
                AccountNumber = AccountNumber.Parse(account.AccountNumber),
                PayerPartyId = account.PayerPartyId,
                ChangeKind = "TERM_ATTACHED",
                TermIds = termIds,
            },
            BusinessKeys.Empty.With("billingAccountId", account.BillingAccountId.Value.ToString()).With("policyId", command.PolicyId.Value.ToString())
                .With("policyTermId", bound.NewTermId.Value.ToString())) { OccurredAt = now });

        return new IntakeOutcome(true, await billing.ProgressAsync(bound.NewTermId, cancellationToken).ConfigureAwait(false));
    }
}

internal sealed class AttachRenewalTermAuditor : ICommandAuditor<AttachRenewalTerm, IntakeOutcome>
{
    public CommandAuditFacts Describe(AttachRenewalTerm command, Result<IntakeOutcome>? result) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.POL, "PolicyTerm", command.Bound.NewTermId),
        BusinessKeys = BusinessKeys.Empty.With("policyId", command.PolicyId.Value.ToString())
            .With("policyTermId", command.Bound.NewTermId.Value.ToString()).With("transactionId", command.Bound.TransactionId.Value.ToString()),
        Changes = result is { IsSuccess: true } ok
            ? AuditDiff.Compute(null, new { applied = ok.Value.Applied, invoices = ok.Value.InvoicesCreated, termNumber = command.Bound.NewTermNumber })
            : [],
    };
}
