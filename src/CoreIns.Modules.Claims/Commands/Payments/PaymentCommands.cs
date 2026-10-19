using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Item = CoreIns.Modules.Claims.Contracts.Api.TransactionSetBuildRequest.TransactionItem;

namespace CoreIns.Modules.Claims.Commands.Payments;

/// <summary>Errors, locking and BIL plumbing shared by the payment correction commands.</summary>
internal static class PaymentSupport
{
    public const string CodePattern = "^[A-Z][A-Z0-9_]{0,63}$";

    public static DomainError NotStoppable(string detail) => DomainError.Of(ModuleCode.CLM, "NOT-STOPPABLE", detail);

    public static DomainError Illegal(string detail) => DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", detail);

    /// <summary>The claim (locked) and the tracked payment of the caller's legal entity, or null.</summary>
    public static async Task<(ClaimRow Claim, ClaimPaymentRow Payment)?> LoadAsync(
        ClaimsDbContext db, LegalEntityId legalEntity, ClaimPaymentId paymentId, CancellationToken cancellationToken)
    {
        var claimId = await db.ClaimPayments.AsNoTracking().Where(p => p.ClaimPaymentId == paymentId && p.LegalEntityId == legalEntity)
            .Select(p => (ClaimId?)p.ClaimId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (claimId is null || await ClaimSupport.LoadAsync(db, legalEntity, claimId.Value, cancellationToken).ConfigureAwait(false) is not { } claim)
        {
            return null;
        }

        var payment = await db.ClaimPayments.SingleAsync(p => p.ClaimPaymentId == paymentId, cancellationToken).ConfigureAwait(false);
        return (claim, payment);
    }

    /// <summary>A key derived from the caller's key and the operation, so the nested BIL call replays with the outer command.</summary>
    public static IdempotencyKey Derived(IdempotencyKey? key, ClaimPaymentId payment, string operation)
    {
        var seed = key is { } k ? k.Value.ToString("D") : payment.Value.ToString("D");
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{seed}|clm.payment|{payment.Value:D}|{operation}"));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return IdempotencyKey.From(new Guid(bytes));
    }

    /// <summary>BIL refusals that mean the disbursement is past the point of no return are CLM-ERR-NOT-STOPPABLE; anything else propagates.</summary>
    public static bool IsNotStoppable(DomainException ex) =>
        ex.Error.Code.Module == ModuleCode.BIL && ex.Error.Code.Name is "NOT-STOPPABLE" or "NOT-VOIDABLE" or "ILLEGAL-TRANSITION" or "STATE" or "ILLEGAL-STATE";

    public static CommandAuditFacts Facts(ClaimPaymentId payment, ClaimId? claim, object? changes) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.CLM, "ClaimPayment", payment),
        BusinessKeys = claim is { } c ? BusinessKeys.Empty.With("paymentId", payment.Value.ToString()).With("claimId", c.Value.ToString()) : BusinessKeys.Empty.With("paymentId", payment.Value.ToString()),
        Changes = changes is null ? [] : AuditDiff.Compute(null, changes),
    };
}

// ---- clm.Payment.void --------------------------------------------------------------------------------------------------------------

/// <summary><c>clm.Payment.void</c>.</summary>
internal sealed record VoidPayment(PaymentVoidRequest Request) : ICommand<PaymentVoidResponse>;

internal sealed class VoidPaymentValidator : AbstractValidator<VoidPayment>
{
    public VoidPaymentValidator()
    {
        RuleFor(c => c.Request.PaymentId).NotEmpty();
        RuleFor(c => c.Request.Reason).NotEmpty().Matches(PaymentSupport.CodePattern).WithErrorCode("CODE");
    }
}

/// <summary>
/// Voids a payment (REQ-CLM-125). A payment BIL has not accepted yet (pending approval, approved, on hold) is cancelled inside CLM
/// (a set still awaiting its checkers is rejected and the requests withdrawn); an Issued payment that has not Cleared is voided
/// at BIL (<c>bil.Disbursement.void</c>, the manual stub bank, Development) and its ledger effect reversed here at once, so the
/// answer carries the reversal transaction; BIL's own <c>DisbursementVoided</c> later finds the payment corrected and is ignored.
/// A Cleared or otherwise corrected payment is an illegal transition. <c>PaymentVoided</c> goes out once, for a payment that was issued.
/// </summary>
internal sealed class VoidPaymentHandler(
    ClaimsDbContext db, RequestContext context, ClaimProtection protection, PaymentCorrections corrections, SetLifecycle lifecycle, IServiceProvider services)
    : ICommandHandler<VoidPayment, PaymentVoidResponse>
{
    public async Task<Result<PaymentVoidResponse>> HandleAsync(VoidPayment command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var id = new ClaimPaymentId(command.Request.PaymentId.Value);
        var loaded = await PaymentSupport.LoadAsync(db, legalEntity, id, cancellationToken).ConfigureAwait(false);
        if (loaded is null)
        {
            return ClaimSupport.NotFound("payment");
        }

        var (claim, payment) = loaded.Value;

        if (payment.Status == Codes.Of(PaymentStatus.Voided))
        {
            return await ResponseAsync(payment, cancellationToken).ConfigureAwait(false); // replay
        }

        var status = Codes.Parse<PaymentStatus>(payment.Status);
        if (status is not (PaymentStatus.Pending or PaymentStatus.Approved or PaymentStatus.OnHold or PaymentStatus.Issued))
        {
            return PaymentSupport.Illegal($"A {payment.Status} payment cannot be voided; only a payment that is not issued yet, or issued and not cleared.");
        }

        if (context.DryRun)
        {
            return new PaymentVoidResponse { PaymentId = command.Request.PaymentId, Status = Codes.Of(PaymentStatus.Voided) };
        }

        if (status == PaymentStatus.Pending)
        {
            var set = (await FinancialSupport.LockSetAsync(db, legalEntity, payment.SetId, cancellationToken).ConfigureAwait(false))!;
            if (set.Status == Codes.Of(SetStatus.PendingApproval) || set.Status == Codes.Of(SetStatus.Draft) || set.Status == Codes.Of(SetStatus.Submitted))
            {
                var content = await FinancialSupport.ContentAsync(db, set, cancellationToken).ConfigureAwait(false);
                await lifecycle.RejectAsync(claim, content, command.Request.Reason, cancellationToken).ConfigureAwait(false);
                payment.Status = Codes.Of(PaymentStatus.Voided);
                return await ResponseAsync(payment, cancellationToken).ConfigureAwait(false);
            }
        }

        if (status == PaymentStatus.Issued && payment.DisbursementId is { } disbursementId)
        {
            if (services.GetService<IBillingDisbursementService>() is not { } bil)
            {
                return DomainError.Of(ModuleCode.CLM, "DEPENDENCY-UNAVAILABLE", "The disbursement service is not available.");
            }

            try
            {
                await bil.VoidAsync(new DisbursementVoidRequest { DisbursementId = disbursementId, Reason = command.Request.Reason },
                    new CommandOptions(PaymentSupport.Derived(context.IdempotencyKey, id, "void")), cancellationToken).ConfigureAwait(false);
            }
            catch (DomainException ex) when (PaymentSupport.IsNotStoppable(ex))
            {
                return PaymentSupport.NotStoppable($"BIL refused to void the disbursement ({ex.Error.Code.Value}).");
            }
        }

        await corrections.ApplyAsync(claim, payment, PaymentStatus.Voided, PaymentVoidedV1.ReasonValue.Voided, command.Request.Reason, cancellationToken).ConfigureAwait(false);
        return await ResponseAsync(payment, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PaymentVoidResponse> ResponseAsync(ClaimPaymentRow payment, CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var reversal = await db.FinancialTransactions.AsNoTracking().Where(t => t.ClaimPaymentId == payment.ClaimPaymentId && t.ReversesTxnId != null)
            .OrderBy(t => t.Sequence).Select(t => (Guid?)t.TxnId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return new PaymentVoidResponse
        {
            PaymentId = new PaymentId(payment.ClaimPaymentId.Value), Status = payment.Status,
            ReversalTransactionId = reversal is { } txn ? new PolicyTransactionId(txn) : null,
        };
    }
}

internal sealed class VoidPaymentAuditor : ICommandAuditor<VoidPayment, PaymentVoidResponse>
{
    public CommandAuditFacts Describe(VoidPayment command, Result<PaymentVoidResponse>? result) =>
        PaymentSupport.Facts(new ClaimPaymentId(command.Request.PaymentId.Value), null,
            result is { IsSuccess: true } ok ? new { status = ok.Value.Status, reason = command.Request.Reason, reversal = ok.Value.ReversalTransactionId?.Value } : null);
}

// ---- clm.Payment.stop --------------------------------------------------------------------------------------------------------------

/// <summary><c>clm.Payment.stop</c>.</summary>
internal sealed record StopPayment(PaymentStopRequest Request) : ICommand<PaymentStopResponse>;

internal sealed class StopPaymentValidator : AbstractValidator<StopPayment>
{
    public StopPaymentValidator()
    {
        RuleFor(c => c.Request.PaymentId).NotEmpty();
        RuleFor(c => c.Request.Reason).NotEmpty().Matches(PaymentSupport.CodePattern).WithErrorCode("CODE");
    }
}

/// <summary>
/// Stops a payment (REQ-CLM-126): <c>bil.Disbursement.stop</c> under BIL's authority, executed only while BIL can still hold the
/// money back (Submitted or Issued, never Cleared); a refusal or <c>stoppable = false</c> is CLM-ERR-NOT-STOPPABLE. A payment BIL never
/// received (on hold) is stopped inside CLM. The ledger effect is reversed at once (open reserve restored when the payment eroded it).
/// </summary>
internal sealed class StopPaymentHandler(
    ClaimsDbContext db, RequestContext context, ClaimProtection protection, PaymentCorrections corrections, IServiceProvider services)
    : ICommandHandler<StopPayment, PaymentStopResponse>
{
    public async Task<Result<PaymentStopResponse>> HandleAsync(StopPayment command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var id = new ClaimPaymentId(command.Request.PaymentId.Value);
        var loaded = await PaymentSupport.LoadAsync(db, legalEntity, id, cancellationToken).ConfigureAwait(false);
        if (loaded is null)
        {
            return ClaimSupport.NotFound("payment");
        }

        var (claim, payment) = loaded.Value;

        if (payment.Status == Codes.Of(PaymentStatus.Stopped))
        {
            return Response(payment); // replay
        }

        var status = Codes.Parse<PaymentStatus>(payment.Status);
        if (status is not (PaymentStatus.Approved or PaymentStatus.OnHold or PaymentStatus.Submitted or PaymentStatus.Issued))
        {
            return PaymentSupport.NotStoppable($"A {payment.Status} payment cannot be stopped.");
        }

        if (context.DryRun)
        {
            return new PaymentStopResponse { PaymentId = command.Request.PaymentId, Status = Codes.Of(PaymentStatus.Stopped), DisbursementId = payment.DisbursementId };
        }

        if (status is PaymentStatus.Submitted or PaymentStatus.Issued && payment.DisbursementId is { } disbursementId)
        {
            if (services.GetService<IBillingDisbursementService>() is not { } bil)
            {
                return DomainError.Of(ModuleCode.CLM, "DEPENDENCY-UNAVAILABLE", "The disbursement service is not available.");
            }

            try
            {
                var stopped = await bil.StopAsync(new DisbursementStopRequest { DisbursementId = disbursementId, Reason = command.Request.Reason },
                    new CommandOptions(PaymentSupport.Derived(context.IdempotencyKey, id, "stop")), cancellationToken).ConfigureAwait(false);
                if (stopped.Stoppable == false)
                {
                    return PaymentSupport.NotStoppable("BIL reports the disbursement can no longer be stopped.");
                }
            }
            catch (DomainException ex) when (PaymentSupport.IsNotStoppable(ex))
            {
                return PaymentSupport.NotStoppable($"BIL refused to stop the disbursement ({ex.Error.Code.Value}).");
            }
        }

        await corrections.ApplyAsync(claim, payment, PaymentStatus.Stopped, PaymentVoidedV1.ReasonValue.Stopped, command.Request.Reason, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Response(payment);
    }

    private static PaymentStopResponse Response(ClaimPaymentRow payment) =>
        new() { PaymentId = new PaymentId(payment.ClaimPaymentId.Value), Status = payment.Status, DisbursementId = payment.DisbursementId };
}

internal sealed class StopPaymentAuditor : ICommandAuditor<StopPayment, PaymentStopResponse>
{
    public CommandAuditFacts Describe(StopPayment command, Result<PaymentStopResponse>? result) =>
        PaymentSupport.Facts(new ClaimPaymentId(command.Request.PaymentId.Value), null,
            result is { IsSuccess: true } ok ? new { status = ok.Value.Status, reason = command.Request.Reason } : null);
}

// ---- clm.Payment.reissue -----------------------------------------------------------------------------------------------------------

/// <summary><c>clm.Payment.reissue</c>.</summary>
internal sealed record ReissuePayment(PaymentReissueRequest Request) : ICommand<PaymentReissueResponse>;

internal sealed class ReissuePaymentValidator : AbstractValidator<ReissuePayment>
{
    public ReissuePaymentValidator()
    {
        RuleFor(c => c.Request.PaymentId).NotEmpty();
        RuleFor(c => c.Request.Reason).NotEmpty().Matches(PaymentSupport.CodePattern).WithErrorCode("CODE");
    }
}

/// <summary>
/// Reissues a voided, stopped, returned or rejected payment (REQ-CLM-127): a new payment of the same amount, type, exposure and
/// cost line, to the same payee and the original account unless the request names another account captured on the claim,
/// built and submitted through the normal engine as the caller, so every check runs again (payable, duplicate, reserve, authority,
/// referral) and the new payment is linked to the original (<c>reissue_of</c>, at most once). The new set may be referred.
/// </summary>
internal sealed class ReissuePaymentHandler(
    ClaimsDbContext db, RequestContext context, ClaimProtection protection,
    ICommandHandler<BuildTransactionSet, TransactionSetBuildResponse> build,
    ICommandHandler<SubmitTransactionSet, TransactionSetSubmitResponse> submit) : ICommandHandler<ReissuePayment, PaymentReissueResponse>
{
    private static readonly PaymentStatus[] Reissuable = [PaymentStatus.Voided, PaymentStatus.Stopped, PaymentStatus.Returned, PaymentStatus.Rejected];

    public async Task<Result<PaymentReissueResponse>> HandleAsync(ReissuePayment command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var id = new ClaimPaymentId(request.PaymentId.Value);
        var loaded = await PaymentSupport.LoadAsync(db, legalEntity, id, cancellationToken).ConfigureAwait(false);
        if (loaded is null)
        {
            return ClaimSupport.NotFound("payment");
        }

        var (claim, original) = loaded.Value;

        if (!Reissuable.Contains(Codes.Parse<PaymentStatus>(original.Status)))
        {
            return PaymentSupport.Illegal($"Only a voided, stopped, returned or rejected payment is reissued; this one is {original.Status}.");
        }

        if (original.Method == "CLEARING")
        {
            return PaymentSupport.Illegal("A clearing payment is not reissued; it follows the Friendly Settlement statement.");
        }

        var existing = await db.ClaimPayments.AsNoTracking().Where(p => p.ReissueOf == id).Select(p => new { p.ClaimPaymentId, p.SetId }).FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return PaymentSupport.Illegal("The payment was reissued already.");
        }

        var firstLine = await db.FinancialTransactions.AsNoTracking()
            .Where(t => t.ClaimPaymentId == id && t.Kind == "PAYMENT" && t.ReversesTxnId == null).OrderBy(t => t.Sequence).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (firstLine is null)
        {
            return PaymentSupport.Illegal("The payment has no ledger line to reissue.");
        }

        var line = await db.ReserveLines.AsNoTracking().SingleAsync(l => l.ReserveLineId == firstLine.ReserveLineId, cancellationToken).ConfigureAwait(false);
        var buildRequest = new TransactionSetBuildRequest
        {
            ClaimId = claim.ClaimId,
            Transactions =
            [
                new Item
                {
                    Kind = Item.KindValue.Payment, ExposureId = original.ExposureId, CostType = line.CostType, CostCategory = line.CostCategory,
                    Amount = ClaimMoney.Of(original.Amount, original.Currency), PayeePartyId = original.PayeePartyId,
                    PayeeAccountId = request.PayeeAccountId ?? original.PayeeAccountId, PaymentType = original.PaymentType == Codes.Of(PaymentType.Final) ? Item.PaymentTypeValue.Final : Item.PaymentTypeValue.Partial,
                },
            ],
        };

        TransactionSetSubmitResponse submitted;
        ClaimPaymentRow created;
        using (context.Use(PaymentSupport.Derived(context.IdempotencyKey, id, "reissue-build"), context.DryRun))
        {
            var built = await build.HandleAsync(new BuildTransactionSet(buildRequest), cancellationToken).ConfigureAwait(false);
            if (built.IsFailure)
            {
                return built.Error!;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            created = await db.ClaimPayments.SingleAsync(p => p.SetId == new ClaimTransactionSetId(built.Value.SetId), cancellationToken).ConfigureAwait(false);
            created.ReissueOf = id;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            using (context.Use(PaymentSupport.Derived(context.IdempotencyKey, id, "reissue-submit"), context.DryRun))
            {
                var result = await submit.HandleAsync(new SubmitTransactionSet(new TransactionSetSubmitRequest { SetId = built.Value.SetId }), cancellationToken).ConfigureAwait(false);
                if (result.IsFailure)
                {
                    return result.Error!;
                }

                submitted = result.Value;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new PaymentReissueResponse { OriginalPaymentId = request.PaymentId, NewPaymentId = new PaymentId(created.ClaimPaymentId.Value), SetId = submitted.SetId };
    }
}

internal sealed class ReissuePaymentAuditor : ICommandAuditor<ReissuePayment, PaymentReissueResponse>
{
    public CommandAuditFacts Describe(ReissuePayment command, Result<PaymentReissueResponse>? result) =>
        PaymentSupport.Facts(new ClaimPaymentId(command.Request.PaymentId.Value), null,
            result is { IsSuccess: true } ok ? new { newPaymentId = ok.Value.NewPaymentId.Value, setId = ok.Value.SetId, reason = command.Request.Reason } : null);
}

// ---- BIL facts ---------------------------------------------------------------------------------------------------------------------

/// <summary>A BIL disbursement correction fact (stopped, voided, returned or rejected) applied to the claim payment.</summary>
internal sealed record RecordDisbursementCorrection(ClaimPaymentId ClaimPaymentId, DisbursementId DisbursementId, PaymentStatus Target, string ReasonCode) : ICommand<string>;

/// <summary>
/// <c>DisbursementStopped/Voided/Returned/Rejected</c> → the payment is corrected exactly as the clm.Payment.* commands do
/// (reversal set, status, <c>PaymentVoided</c> once). Idempotent: an already corrected payment, another disbursement than the
/// payment's, or a payment not known to CLM is ignored. A bank return after clearing is accepted; a stop or void after
/// clearing is not a BIL state, so it is ignored defensively.
/// </summary>
internal sealed class RecordDisbursementCorrectionHandler(ClaimsDbContext db, RequestContext context, ClaimProtection protection, PaymentCorrections corrections)
    : ICommandHandler<RecordDisbursementCorrection, string>
{
    public async Task<Result<string>> HandleAsync(RecordDisbursementCorrection command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var loaded = await PaymentSupport.LoadAsync(db, legalEntity, command.ClaimPaymentId, cancellationToken).ConfigureAwait(false);
        if (loaded is null)
        {
            return "IGNORED";
        }

        var (claim, payment) = loaded.Value;

        if (payment.DisbursementId is { } known && known != command.DisbursementId)
        {
            return "IGNORED";
        }

        if (PaymentCorrections.IsCorrected(payment) || payment.Status is "PENDING" or "APPROVED")
        {
            return "IGNORED";
        }

        if (payment.Status == Codes.Of(PaymentStatus.Cleared) && command.Target != PaymentStatus.Returned)
        {
            return "IGNORED";
        }

        var reason = command.Target switch
        {
            PaymentStatus.Voided => PaymentVoidedV1.ReasonValue.Voided,
            PaymentStatus.Stopped => PaymentVoidedV1.ReasonValue.Stopped,
            PaymentStatus.Returned => PaymentVoidedV1.ReasonValue.Returned,
            _ => PaymentVoidedV1.ReasonValue.Rejected,
        };
        await corrections.ApplyAsync(claim, payment, command.Target, reason, command.ReasonCode, cancellationToken).ConfigureAwait(false);
        return payment.Status;
    }
}

internal sealed class RecordDisbursementCorrectionAuditor : ICommandAuditor<RecordDisbursementCorrection, string>
{
    public CommandAuditFacts Describe(RecordDisbursementCorrection command, Result<string>? result) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.CLM, "ClaimPayment", command.ClaimPaymentId),
        BusinessKeys = BusinessKeys.Empty.With("paymentId", command.ClaimPaymentId.Value.ToString()).With("disbursementId", command.DisbursementId.Value.ToString()),
        Changes = result is { IsSuccess: true } ok ? AuditDiff.Compute(null, new { status = ok.Value, reason = command.ReasonCode }) : [],
    };
}
