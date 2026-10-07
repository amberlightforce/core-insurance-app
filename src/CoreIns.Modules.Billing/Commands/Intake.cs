using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Services;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Billing.Commands;

/// <summary>Where an intake command's input came from (the consumed event: its id, aggregate sequence and set fields).</summary>
internal sealed record EventSource(Guid EventId, long AggregateSequence, EventSet? Set);

/// <summary>Outcome of an intake command.</summary>
/// <param name="Applied">False when the input had been applied before (idempotent replay).</param>
/// <param name="InvoicesCreated">Invoices created by this step.</param>
internal sealed record IntakeOutcome(bool Applied, int InvoicesCreated);

/// <summary>
/// <c>bil.Charge.intake</c> (internal, from <c>pol.ChargeDeltaEmitted</c>): stores the delta frozen as received,
/// idempotent on the charge id (REQ-BIL-002), then lets the term progress (REQ-BIL-364).
/// </summary>
/// <param name="Delta">The payload.</param>
/// <param name="PolicyId">The policy (envelope business key <c>policyId</c>; the payload carries none).</param>
/// <param name="Source">The consumed event.</param>
internal sealed record IntakeCharge(ChargeDeltaEmittedV1 Delta, PolicyId PolicyId, EventSource Source) : ICommand<IntakeOutcome>;

internal sealed class IntakeChargeHandler(
    BillingDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    TermBilling billing) : ICommandHandler<IntakeCharge, IntakeOutcome>
{
    public async Task<Result<IntakeOutcome>> HandleAsync(IntakeCharge command, CancellationToken cancellationToken)
    {
        var delta = command.Delta;
        await billing.LockTermAsync(delta.TermId, cancellationToken).ConfigureAwait(false);
        if (await db.Charges.AnyAsync(c => c.ChargeId == delta.ChargeId, cancellationToken).ConfigureAwait(false))
        {
            // A replay still lets the term progress: an earlier delivery may have stopped short of billing.
            return new IntakeOutcome(false, await billing.ProgressAsync(delta.TermId, cancellationToken).ConfigureAwait(false));
        }

        // D4: ChargeDeltaEmitted always carries its set; a missing set is a contract violation and is retried/parked.
        var set = command.Source.Set ?? throw new InvalidOperationException("ChargeDeltaEmitted without set_id/set_size/index (contract D4).");
        db.Charges.Add(new ChargeRow
        {
            ChargeId = delta.ChargeId,
            LegalEntityId = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity.")),
            SetId = set.SetId,
            SetSize = set.Size,
            SetIndex = set.Index,
            PolicyId = command.PolicyId,
            TermId = delta.TermId,
            TransactionId = delta.TransactionId,
            ElementLocator = delta.ElementLocator,
            CoverageCode = delta.CoverageCode,
            ChargeType = delta.ChargeType,
            ChargeCategory = delta.ChargeCategory,
            DeltaKind = delta.DeltaKind,
            Amount = delta.NetAmount.Amount,
            Currency = delta.NetAmount.Currency.Code,
            ValidFrom = delta.ValidPeriod.Start,
            ValidTo = delta.ValidPeriod.End,
            BookingDate = delta.BookingDate,
            CorrelationKey = delta.CorrelationKey,
            TaxTreatmentRef = delta.TaxTreatmentRef,
            SourceEventId = command.Source.EventId,
            SourceSequence = command.Source.AggregateSequence,
            ReceivedAt = clock.Now,
            Status = Codes.Of(ChargeStatus.Received),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var created = await billing.ProgressAsync(delta.TermId, cancellationToken).ConfigureAwait(false);
        return new IntakeOutcome(true, created);
    }
}

internal sealed class IntakeChargeAuditor : ICommandAuditor<IntakeCharge, IntakeOutcome>
{
    public CommandAuditFacts Describe(IntakeCharge command, Result<IntakeOutcome>? result) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.BIL, "Charge", command.Delta.ChargeId),
        BusinessKeys = BusinessKeys.Empty.With("chargeId", command.Delta.ChargeId.Value.ToString())
            .With("policyTermId", command.Delta.TermId.Value.ToString()).With("transactionId", command.Delta.TransactionId.Value.ToString()),
        Changes = result is { IsSuccess: true } ok
            ? AuditDiff.Compute(null, new { applied = ok.Value.Applied, invoices = ok.Value.InvoicesCreated, chargeType = command.Delta.ChargeType })
            : [],
    };
}

/// <summary>
/// <c>bil.BillingAccount.attachTerm</c> (internal, from <c>pol.PolicyBound</c>): finds the payer's Active account in the
/// legal entity and currency or creates one (REQ-BIL-030, REQ-BIL-033), creates the term's plan instance
/// (REQ-BIL-052; ANNUAL only, D-SLC-10c — any other plan is recorded as an exception and not billed), then lets the
/// term progress. Idempotent on the term id.
/// </summary>
internal sealed record AttachTerm(PolicyBoundV1 Bound, EventSource Source) : ICommand<IntakeOutcome>;

internal sealed class AttachTermHandler(
    BillingDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    INumberingService numbering,
    IEventPublisher events,
    TermBilling billing,
    IOptions<BillingOptions> options) : ICommandHandler<AttachTerm, IntakeOutcome>
{
    /// <summary>Numbering identifier type of billing accounts (REQ-BIL-030; series in Platform:Numbering).</summary>
    public const string BillingAccountScheme = "BILLING_ACCOUNT";

    public async Task<Result<IntakeOutcome>> HandleAsync(AttachTerm command, CancellationToken cancellationToken)
    {
        var bound = command.Bound;
        await billing.LockTermAsync(bound.TermId, cancellationToken).ConfigureAwait(false);
        if (await db.PlanInstances.AnyAsync(p => p.TermId == bound.TermId, cancellationToken).ConfigureAwait(false))
        {
            return new IntakeOutcome(false, await billing.ProgressAsync(bound.TermId, cancellationToken).ConfigureAwait(false));
        }

        if (!string.Equals(bound.PaymentPlanRef, PaymentPlans.Annual, StringComparison.Ordinal))
        {
            await billing.RaiseAsync(ExceptionKinds.PlanUnsupported, bound.TermId.Value.ToString(), "PLAN-" + bound.PaymentPlanRef,
                $"Payment plan '{bound.PaymentPlanRef}' is not served (the slice bills ANNUAL only, D-SLC-10c); the term is not billed.", cancellationToken)
                .ConfigureAwait(false);
            return new IntakeOutcome(false, 0);
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
        var zone = options.Value.Zone;
        var now = clock.Now;
        var today = now.ToBusinessDate(zone);

        // PolicyBound carries no currency: take the term's charges' currency when they arrived first, else the stamp default.
        var currency = await db.Charges.Where(c => c.TermId == bound.TermId).Select(c => c.Currency).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                       ?? options.Value.Currency.Code;
        var active = Codes.Of(BillingAccountStatus.Active);
        var account = await db.Accounts.SingleOrDefaultAsync(
            a => a.LegalEntityId == legalEntity && a.PayerPartyId == bound.PayerPartyId && a.Currency == currency && a.Status == active,
            cancellationToken).ConfigureAwait(false);
        var createdAccount = account is null;
        if (account is null)
        {
            var number = await numbering.NextAsync(new NumberRequest(BillingAccountScheme, today), cancellationToken).ConfigureAwait(false);
            account = new BillingAccountRow
            {
                BillingAccountId = BillingAccountId.New(),
                LegalEntityId = legalEntity,
                Jurisdiction = (context.Jurisdiction ?? throw new InvalidOperationException("No jurisdiction.")).Value,
                AccountNumber = number.Value,
                PayerPartyId = bound.PayerPartyId,
                Currency = currency,
                Status = active,
                CreatedAt = now,
                CreatedBy = context.Actor.ToString(),
                RecordVersion = 1,
            };
            db.Accounts.Add(account);
        }

        db.PlanInstances.Add(new PlanInstanceRow
        {
            TermId = bound.TermId,
            PlanInstanceId = Guid.CreateVersion7(),
            BillingAccountId = account.BillingAccountId,
            LegalEntityId = legalEntity,
            PolicyId = bound.PolicyId,
            PolicyNumber = bound.PolicyNumber.Value,
            TermNumber = bound.TermNumber,
            BoundTransactionId = bound.TransactionId,
            ProductCode = bound.ProductCode,
            ProductVersion = bound.ProductVersion.ToString(),
            ArtefactHash = bound.ArtefactHash.Value,
            PlanCode = PaymentPlans.Annual,
            BillMode = PaymentPlans.DirectBill,
            Method = TermBilling.DefaultMethod,
            TermFrom = bound.EffectivePeriod.Start.ToBusinessDate(zone),
            TermTo = (bound.EffectivePeriod.End ?? throw new InvalidOperationException("PolicyBound without a term end.")).ToBusinessDate(zone),
            SourceEventId = command.Source.EventId,
            SourceSequence = command.Source.AggregateSequence,
            CreatedAt = now,
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
                ChangeKind = createdAccount ? "CREATED" : "TERM_ATTACHED",
                TermIds = termIds,
            },
            BusinessKeys.Empty.With("billingAccountId", account.BillingAccountId.Value.ToString()).With("policyId", bound.PolicyId.Value.ToString())
                .With("policyTermId", bound.TermId.Value.ToString())) { OccurredAt = now });

        var created = await billing.ProgressAsync(bound.TermId, cancellationToken).ConfigureAwait(false);
        return new IntakeOutcome(true, created);
    }
}

internal sealed class AttachTermAuditor : ICommandAuditor<AttachTerm, IntakeOutcome>
{
    public CommandAuditFacts Describe(AttachTerm command, Result<IntakeOutcome>? result) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.POL, "PolicyTerm", command.Bound.TermId),
        ObjectNumber = command.Bound.PolicyNumber.Value,
        BusinessKeys = BusinessKeys.Empty.With("policyId", command.Bound.PolicyId.Value.ToString())
            .With("policyTermId", command.Bound.TermId.Value.ToString()).With("transactionId", command.Bound.TransactionId.Value.ToString()),
        Changes = result is { IsSuccess: true } ok
            ? AuditDiff.Compute(null, new { applied = ok.Value.Applied, invoices = ok.Value.InvoicesCreated, plan = command.Bound.PaymentPlanRef })
            : [],
    };
}
