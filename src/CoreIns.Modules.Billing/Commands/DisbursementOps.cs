using System.Text.Json;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Services;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Commands;

/// <summary>The result of one disbursement operation: the row as it now stands and the reversal entry, if one was posted.</summary>
internal sealed record DisbursementOpOutcome(DisbursementRow Row, string IbanLast4, Money Amount, Guid? ReversalEntryId)
{
    /// <summary><c>reversalEntries</c> of the stop and void responses.</summary>
    public IReadOnlyList<JsonElement> ReversalEntries =>
        ReversalEntryId is { } id ? [JsonSerializer.SerializeToElement(new { entryId = id, entryType = EntryTypes.DisbursementReversed })] : [];
}

/// <summary>
/// The corrections of an issued disbursement (SL4-BIL-DISBOPS, D-SL4-13, REQ-BIL-208, -210, -361): stop (before the bank has
/// it), void (issued, not cleared), the bank's reject of a released payment and its return (also after clearing). Each moves
/// the state machine, posts one <c>DISBURSEMENT_REVERSED</c> entry that gives the source payable back (LA-17 / LA-12 / LA-24;
/// cash LA-10 too when the payment had cleared) and publishes the typed event once. The one service behind the HTTP
/// operations, the in-process <c>IBillingDisbursementService</c> (CLM payment corrections) and the dev bank endpoints.
/// </summary>
internal sealed class DisbursementOperator(
    BillingDbContext db, RequestContext context, IClock clock, LedgerWriter ledger, DisbursementReleaser releaser, IEventPublisher events,
    IAuthorityService authority)
{
    /// <summary>Roles that may stop or void a disbursement of a source (CLM acts for its payments; BIL for its own).</summary>
    private static readonly string[] Allowed =
        ["Staff.Billing", "Staff.BillingManager", "Staff.ClaimsHandler", "Staff.ClaimsManager", "Platform.Admin"];

    public async Task<Result<DisbursementOpOutcome>> StopAsync(DisbursementId id, string reason, CancellationToken cancellationToken)
    {
        if (!context.Roles.Any(r => Allowed.Contains(r, StringComparer.Ordinal)))
        {
            return DomainError.Of(ModuleCode.BIL, "SOD", "Stopping a disbursement needs BIL.DisbursementStop authority.");
        }

        return await RunAsync(id, DisbursementTrigger.Stop, reason, cancellationToken, requireAuthority: true).ConfigureAwait(false);
    }

    public async Task<Result<DisbursementOpOutcome>> VoidAsync(DisbursementId id, string reason, CancellationToken cancellationToken)
    {
        if (!context.Roles.Any(r => Allowed.Contains(r, StringComparer.Ordinal)))
        {
            return DomainError.Of(ModuleCode.BIL, "SOD", "Voiding a disbursement needs BIL.DisbursementStop authority.");
        }

        return await RunAsync(id, DisbursementTrigger.Void, reason, cancellationToken, requireAuthority: true).ConfigureAwait(false);
    }

    /// <summary>The beneficiary bank returned the payment (Issued, or Cleared: late return).</summary>
    public Task<Result<DisbursementOpOutcome>> ReturnAsync(DisbursementId id, string reasonCode, CancellationToken cancellationToken) =>
        RunAsync(id, DisbursementTrigger.Return, reasonCode, cancellationToken);

    /// <summary>The bank rejected a released payment (pain.002 RJCT).</summary>
    public Task<Result<DisbursementOpOutcome>> BankRejectAsync(DisbursementId id, string reasonCode, CancellationToken cancellationToken) =>
        RunAsync(id, DisbursementTrigger.Reject, reasonCode, cancellationToken);

    /// <summary>Manual stub bank: the acknowledgement of a Released payment (Released to Issued).</summary>
    public async Task<Result<DisbursementOpOutcome>> BankIssueAsync(DisbursementId id, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (row, last4, refund, amount) = loaded.Value;
        if (!DisbursementStateModel.Machine.CanFire(Codes.Parse<DisbursementState>(row.State), DisbursementTrigger.Issue))
        {
            return NotAllowed(row, "issued");
        }

        releaser.Issue(row, amount, refund, clock.Now, row.ValueDate ?? ledger.Today, StubBankChannel.ReferencePrefix + row.DisbursementNumber);
        row.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DisbursementOpOutcome(row, last4, amount, null);
    }

    /// <summary>Manual stub bank: the statement debit of an Issued payment (Issued to Cleared).</summary>
    public async Task<Result<DisbursementOpOutcome>> BankClearAsync(DisbursementId id, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (row, last4, refund, amount) = loaded.Value;
        if (!DisbursementStateModel.Machine.CanFire(Codes.Parse<DisbursementState>(row.State), DisbursementTrigger.Clear))
        {
            return NotAllowed(row, "cleared");
        }

        await releaser.ClearAsync(row, amount, refund, clock.Now, row.ValueDate ?? ledger.Today, cancellationToken).ConfigureAwait(false);
        row.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DisbursementOpOutcome(row, last4, amount, null);
    }

    private async Task<Result<DisbursementOpOutcome>> RunAsync(DisbursementId id, DisbursementTrigger trigger, string reason, CancellationToken cancellationToken, bool requireAuthority = false)
    {
        var loaded = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (row, last4, refund, amount) = loaded.Value;
        // Stop and void are limited by amount (NX-EC-09): the actor must hold BIL.DISBURSEMENT_STOP over this payment. A refer is
        // a refusal too: the correction is immediate, so there is no second person to refer it to.
        if (requireAuthority)
        {
            var check = await authority.CheckAsync(
                new AuthorityCheckRequest(
                    context.Actor, context.Roles, SupportAuthorityTypes.DisbursementStop,
                    new Dictionary<string, DimensionValue>(StringComparer.Ordinal) { [SupportAuthorityTypes.AmountDimension] = DimensionValue.Of(amount) },
                    new ObjectRef(ModuleCode.BIL, "Disbursement", row.DisbursementId.Value.ToString()), clock.Now),
                cancellationToken).ConfigureAwait(false);
            context.AuthorityChecks.Add(check);
            if (check.Decision != AuthorityDecision.Allow)
            {
                return DomainError.Of(ModuleCode.BIL, "SOD", $"No authority to {trigger.ToString().ToLowerInvariant()} a disbursement of {amount} (BIL.DISBURSEMENT_STOP: {check.ReasonCode}).") with
                {
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["authorityCheckId"] = check.CheckId.ToString(), ["reasonCode"] = check.ReasonCode },
                };
            }
        }

        var machine = DisbursementStateModel.Machine;
        var from = Codes.Parse<DisbursementState>(row.State);
        if (!machine.CanFire(from, trigger))
        {
            return NotAllowed(row, trigger.ToString().ToLowerInvariant());
        }

        // The payable is only given back when a release entry had moved it (a stop before release posted nothing).
        var reversal = await releaser.ReverseAsync(row, amount, refund, cancellationToken).ConfigureAwait(false);
        row.State = Codes.Of(machine.FireOrThrow(from, trigger));
        row.RecordVersion++;

        var now = clock.Now;
        var keys = BusinessKeys.Empty.With("disbursementId", row.DisbursementId.Value.ToString());
        keys = row.ClaimId is { } claim ? keys.With("claimId", claim.Value.ToString()) : keys;
        var aggregateId = row.DisbursementId.Value.ToString();
        switch (trigger)
        {
            case DisbursementTrigger.Stop:
                events.Publish(new OutgoingEvent(
                    EventDescriptor.From(DisbursementStoppedV1.Descriptor), "Disbursement", aggregateId,
                    new DisbursementStoppedV1
                    {
                        DisbursementId = row.DisbursementId,
                        SourceType = row.SourceType,
                        SourceId = row.SourceId,
                        ReasonCode = reason,
                        Actor = context.Actor.ToString(),
                        StatementRef = row.StatementRef,
                        Method = row.Method,
                    },
                    keys) { OccurredAt = now });
                break;
            case DisbursementTrigger.Void:
                events.Publish(new OutgoingEvent(
                    EventDescriptor.From(DisbursementVoidedV1.Descriptor), "Disbursement", aggregateId,
                    new DisbursementVoidedV1
                    {
                        DisbursementId = row.DisbursementId,
                        SourceType = row.SourceType,
                        SourceId = row.SourceId,
                        Reason = reason,
                        StatementRef = row.StatementRef,
                        Method = row.Method,
                    },
                    keys) { OccurredAt = now });
                break;
            case DisbursementTrigger.Return:
                events.Publish(new OutgoingEvent(
                    EventDescriptor.From(DisbursementReturnedV1.Descriptor), "Disbursement", aggregateId,
                    new DisbursementReturnedV1
                    {
                        DisbursementId = row.DisbursementId,
                        SourceType = row.SourceType,
                        SourceId = row.SourceId,
                        ReasonCode = reason,
                        StatementRef = row.StatementRef,
                        Method = row.Method,
                    },
                    keys) { OccurredAt = now });
                break;
            default:
                events.Publish(new OutgoingEvent(
                    EventDescriptor.From(DisbursementRejectedV1.Descriptor), "Disbursement", aggregateId,
                    new DisbursementRejectedV1
                    {
                        DisbursementId = row.DisbursementId,
                        SourceType = row.SourceType,
                        SourceId = row.SourceId,
                        ReasonCode = reason,
                        RejectingParty = DisbursementRejectedV1.RejectingPartyValue.Bank,
                        StatementRef = row.StatementRef,
                        Method = row.Method,
                    },
                    keys) { OccurredAt = now });
                break;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DisbursementOpOutcome(row, last4, amount, reversal);
    }

    private async Task<Result<(DisbursementRow Row, string IbanLast4, RefundRow? Refund, Money Amount)>> LoadAsync(DisbursementId id, CancellationToken cancellationToken)
    {
        var legalEntity = ledger.LegalEntityId;
        var row = await db.Disbursements.SingleOrDefaultAsync(d => d.DisbursementId == id && d.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return BillingErrors.NotFound("disbursement");
        }

        var last4 = await db.PayeeAccounts.AsNoTracking().Where(a => a.PayeeAccountId == row.PayeeAccountId).Select(a => a.IbanLast4).SingleAsync(cancellationToken).ConfigureAwait(false);
        RefundRow? refund = null;
        if (row.SourceType == DisbursementCodes.RefundPayment && Guid.TryParseExact(row.SourceId, "D", out var refundId))
        {
            var key = new RefundId(refundId);
            refund = await db.Refunds.AsNoTracking().SingleOrDefaultAsync(r => r.RefundId == key, cancellationToken).ConfigureAwait(false);
        }

        return (row, last4, refund, new Money(row.Amount, Currency.FromCode(row.Currency)));
    }

    private static DomainError NotAllowed(DisbursementRow row, string what) =>
        DomainError.Of(ModuleCode.BIL, "NOT-STOPPABLE", $"A disbursement in state {row.State} cannot be {what}.");
}

/// <summary><c>bil.Disbursement.stop</c> (REQ-BIL-208, REQ-CLM-126): before the bank has the payment (PendingApproval, Approved, Released).</summary>
internal sealed record StopDisbursement(DisbursementStopRequest Request) : ICommand<DisbursementStopResponse>;

/// <summary><c>bil.Disbursement.void</c> (REQ-BIL-208, REQ-CLM-125): an Issued payment that has not cleared.</summary>
internal sealed record VoidDisbursement(DisbursementVoidRequest Request) : ICommand<DisbursementVoidResponse>;

internal sealed class StopDisbursementValidator : AbstractValidator<StopDisbursement>
{
    public StopDisbursementValidator() => RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(100);
}

internal sealed class VoidDisbursementValidator : AbstractValidator<VoidDisbursement>
{
    public VoidDisbursementValidator() => RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(100);
}

internal sealed class StopDisbursementHandler(DisbursementOperator ops) : ICommandHandler<StopDisbursement, DisbursementStopResponse>
{
    public async Task<Result<DisbursementStopResponse>> HandleAsync(StopDisbursement command, CancellationToken cancellationToken)
    {
        var result = await ops.StopAsync(command.Request.DisbursementId, command.Request.Reason, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        var o = result.Value;
        return new DisbursementStopResponse
        {
            Disbursement = new DisbursementStopResponse.DisbursementDetail
            {
                DisbursementId = o.Row.DisbursementId,
                Status = Codes.Api<DisbursementStopResponse.DisbursementDetail.StatusValue>(Codes.Parse<DisbursementState>(o.Row.State)),
                SourceType = o.Row.SourceType,
                SourceId = o.Row.SourceId,
                Amount = o.Amount,
                Method = o.Row.Method,
                RequestedValueDate = o.Row.RequestedValueDate,
                MaskedPayeeAccount = IbanMask.Mask(o.IbanLast4),
            },
            Stoppable = true,
            ReversalEntries = o.ReversalEntries,
            SourcePayableRestored = o.Amount,
        };
    }
}

internal sealed class VoidDisbursementHandler(DisbursementOperator ops) : ICommandHandler<VoidDisbursement, DisbursementVoidResponse>
{
    public async Task<Result<DisbursementVoidResponse>> HandleAsync(VoidDisbursement command, CancellationToken cancellationToken)
    {
        var result = await ops.VoidAsync(command.Request.DisbursementId, command.Request.Reason, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        var o = result.Value;
        return new DisbursementVoidResponse
        {
            Disbursement = new DisbursementVoidResponse.DisbursementDetail
            {
                DisbursementId = o.Row.DisbursementId,
                Status = Codes.Api<DisbursementVoidResponse.DisbursementDetail.StatusValue>(Codes.Parse<DisbursementState>(o.Row.State)),
                SourceType = o.Row.SourceType,
                SourceId = o.Row.SourceId,
                Amount = o.Amount,
                Method = o.Row.Method,
                RequestedValueDate = o.Row.RequestedValueDate,
                MaskedPayeeAccount = IbanMask.Mask(o.IbanLast4),
            },
            ReversalEntries = o.ReversalEntries,
            SourcePayableRestored = o.Amount,
        };
    }
}

internal sealed class StopDisbursementAuditor : ICommandAuditor<StopDisbursement, DisbursementStopResponse>
{
    public CommandAuditFacts Describe(StopDisbursement command, Result<DisbursementStopResponse>? result) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.BIL, "Disbursement", command.Request.DisbursementId),
        BusinessKeys = BusinessKeys.Empty.With("disbursementId", command.Request.DisbursementId.Value.ToString()),
        Changes = AuditDiff.Compute(null, new { reason = command.Request.Reason, status = result is { IsSuccess: true } ok ? ok.Value.Disbursement.Status.ToString() : null }),
    };
}

internal sealed class VoidDisbursementAuditor : ICommandAuditor<VoidDisbursement, DisbursementVoidResponse>
{
    public CommandAuditFacts Describe(VoidDisbursement command, Result<DisbursementVoidResponse>? result) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.BIL, "Disbursement", command.Request.DisbursementId),
        BusinessKeys = BusinessKeys.Empty.With("disbursementId", command.Request.DisbursementId.Value.ToString()),
        Changes = AuditDiff.Compute(null, new { reason = command.Request.Reason, status = result is { IsSuccess: true } ok ? ok.Value.Disbursement.Status.ToString() : null }),
    };
}

/// <summary>What a dev bank operation reports back.</summary>
internal sealed record DevBankResult(Guid DisbursementId, string Status, Guid? ReversalEntryId);

/// <summary>A manual stub-bank event on a disbursement (issue, clear, return, reject); runs in the command pipeline so the unit of work and outbox exist.</summary>
internal sealed record DevBankOperation(DisbursementId DisbursementId, string Action, string? Reason) : ICommand<DevBankResult>;

internal sealed class DevBankOperationHandler(DisbursementOperator ops) : ICommandHandler<DevBankOperation, DevBankResult>
{
    public async Task<Result<DevBankResult>> HandleAsync(DevBankOperation command, CancellationToken cancellationToken)
    {
        var result = command.Action switch
        {
            "issue" => await ops.BankIssueAsync(command.DisbursementId, cancellationToken).ConfigureAwait(false),
            "clear" => await ops.BankClearAsync(command.DisbursementId, cancellationToken).ConfigureAwait(false),
            "return" => await ops.ReturnAsync(command.DisbursementId, command.Reason ?? "AC04", cancellationToken).ConfigureAwait(false),
            "reject" => await ops.BankRejectAsync(command.DisbursementId, command.Reason ?? "AC01", cancellationToken).ConfigureAwait(false),
            _ => DomainError.Of(ModuleCode.BIL, "VALIDATION", "Unknown bank action."),
        };
        return result.IsSuccess
            ? new DevBankResult(result.Value.Row.DisbursementId.Value, result.Value.Row.State, result.Value.ReversalEntryId)
            : result.Error!;
    }
}

internal sealed class DevBankOperationAuditor : ICommandAuditor<DevBankOperation, DevBankResult>
{
    public CommandAuditFacts Describe(DevBankOperation command, Result<DevBankResult>? result) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.BIL, "Disbursement", command.DisbursementId),
        BusinessKeys = BusinessKeys.Empty.With("disbursementId", command.DisbursementId.Value.ToString()),
        Changes = AuditDiff.Compute(null, new { action = command.Action, reason = command.Reason, status = result is { IsSuccess: true } ok ? ok.Value.Status : null }),
    };
}
