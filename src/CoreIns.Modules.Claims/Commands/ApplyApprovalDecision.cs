using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Claims.Authority;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoreIns.Modules.Claims.Commands;

/// <summary>Applies a PLT decision (from <c>ApprovalDecided</c>) to the referred set. Internal (no HTTP), run by the event handler.</summary>
/// <param name="RequestId">The PLT approval request.</param>
/// <param name="Decision">APPROVED, REJECTED, WITHDRAWN or EXPIRED.</param>
internal sealed record ApplyApprovalDecision(Guid RequestId, string Decision) : ICommand<ApprovalOutcome>;

/// <summary>What the decision did to the set (IGNORED when no pending set of this request exists: replays, other modules' requests).</summary>
internal sealed record ApprovalOutcome(Guid? SetId, string Outcome);

/// <summary>
/// The approval path of a referred set (REQ-CLM-109, -111, -112, PRD-07 §7.3.2): the checker decides in the PLT inbox
/// (maker ≠ checker is PLT's rule, REQ-PLT-115); CLM consumes <c>ApprovalDecided</c> and, for an approval, (1) verifies it
/// with <c>plt.Approval.verifyForExecution</c> for exactly its type, subject and hash, (2) compares the returned authority
/// with the one it computes from the set's own transactions, (3) recomputes the content hashes, (4) re-validates claim,
/// exposures and line balances; any mismatch rejects the set (APPROVAL_MISMATCH / SET_STALE, CLM-ERR-SET-STALE semantics,
/// REQ-CLM-112) instead of applying it. Rejected, withdrawn or expired requests reject the set (REQ-CLM-111). Idempotent and
/// race-safe: under the claim and set locks only a PendingApproval set moves, so a replay or a second delivery has no effect.
/// </summary>
internal sealed partial class ApplyApprovalDecisionHandler(
    ClaimsDbContext db,
    RequestContext context,
    ClaimProtection protection,
    IPlatformApprovalService approvals,
    SetLifecycle lifecycle,
    ILogger<ApplyApprovalDecisionHandler> logger) : ICommandHandler<ApplyApprovalDecision, ApprovalOutcome>
{
    public async Task<Result<ApprovalOutcome>> HandleAsync(ApplyApprovalDecision command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var target = await db.TransactionSets.AsNoTracking()
            .Where(s => s.ApprovalRequestId == command.RequestId && s.LegalEntityId == legalEntity)
            .Select(s => new { s.SetId, s.ClaimId })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return new ApprovalOutcome(null, "IGNORED");
        }

        var claim = (await ClaimSupport.LoadAsync(db, legalEntity, target.ClaimId, cancellationToken).ConfigureAwait(false))!;
        var set = (await FinancialSupport.LockSetAsync(db, legalEntity, target.SetId, cancellationToken).ConfigureAwait(false))!;
        if (set.Status != Codes.Of(SetStatus.PendingApproval))
        {
            return new ApprovalOutcome(set.SetId.Value, "IGNORED");
        }

        var content = await FinancialSupport.ContentAsync(db, set, cancellationToken).ConfigureAwait(false);
        switch (command.Decision)
        {
            case "APPROVED":
                break;
            case "REJECTED":
                lifecycle.Reject(claim, content, FinancialReasons.ApproverRejected);
                return new ApprovalOutcome(set.SetId.Value, Codes.Of(SetStatus.Rejected));
            default:
                lifecycle.Reject(claim, content, FinancialReasons.Withdrawn);
                return new ApprovalOutcome(set.SetId.Value, Codes.Of(SetStatus.Rejected));
        }

        if (await VerifyAsync(set, content, cancellationToken).ConfigureAwait(false) is { } mismatch)
        {
            LogMismatch(logger, set.SetId.Value, mismatch);
            lifecycle.Reject(claim, content, FinancialReasons.ApprovalMismatch);
            return new ApprovalOutcome(set.SetId.Value, Codes.Of(SetStatus.Rejected));
        }

        if (await lifecycle.RevalidateAsync(claim, content, cancellationToken).ConfigureAwait(false) is { } stale)
        {
            LogStale(logger, set.SetId.Value, stale);
            lifecycle.Reject(claim, content, FinancialReasons.SetStale);
            return new ApprovalOutcome(set.SetId.Value, Codes.Of(SetStatus.Rejected));
        }

        var decision = (await approvals.GetAsync(command.RequestId.ToString("D"), cancellationToken).ConfigureAwait(false)).Decision;
        var checker = decision is null ? "UNKNOWN" : $"{decision.Checker.Kind.ToString().ToUpperInvariant()}:{decision.Checker.Id}";
        var checkerUser = decision is { Checker.Kind: Platform.Contracts.Common.Actor.KindValue.User } && Guid.TryParse(decision.Checker.Id, out var oid) && oid != Guid.Empty
            ? oid
            : (Guid?)null;
        await lifecycle.ApproveAsync(claim, content, checker, checkerUser, fourEyes: true, cancellationToken).ConfigureAwait(false);
        return new ApprovalOutcome(set.SetId.Value, Codes.Of(SetStatus.Approved));
    }

    /// <summary>Null when PLT's approval covers exactly this set; otherwise why not.</summary>
    private async Task<string?> VerifyAsync(TransactionSetRow set, SetContent content, CancellationToken cancellationToken)
    {
        // CLM's own view of the bound content: the set content hash, or the disbursement content hash of its payment.
        var payment = content.Payments.SingleOrDefault();
        var expectedType = payment is null ? ClaimApprovals.TransactionSet : DisbursementApproval.ClaimPaymentType;
        var expectedHash = payment is null
            ? SetHashing.Content(set.SetId, set.ClaimId, content.Canonical()).Value
            : DisbursementContent.Hash(
                DisbursementCodes.ClaimPayment, payment.ClaimPaymentId.Value.ToString("D"), payment.PayeePartyId, payment.PayeeAccountId,
                ClaimMoney.Of(payment.Amount, payment.Currency)).Value;
        if (set.ApprovalType != expectedType || set.ApprovalPayloadHash != expectedHash || payment?.DisbursementContentHash is { } stored && stored != expectedHash)
        {
            return "The bound content hash does not match the set's content.";
        }

        ApprovalVerifyForExecutionResponse verified;
        try
        {
            verified = await approvals.VerifyForExecutionAsync(
                new ApprovalVerifyForExecutionRequest
                {
                    RequestId = set.ApprovalRequestId!.Value,
                    Hash = Sha256Hash.Parse(expectedHash),
                    Type = expectedType,
                    ObjectRef = new ObjectRef(ModuleCode.CLM, set.ApprovalSubjectType!, set.ApprovalSubjectId!),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            return $"PLT refused execution ({ex.Error.Code}).";
        }

        if (!verified.Ok)
        {
            return $"The approval request is {verified.Status}.";
        }

        // The authority PLT checked the checker against must be the one CLM computes from the set's transactions.
        var lead = content.Transactions
            .Where(t => Math.Abs(t.Amount) == set.ApprovalAuthorityAmount && SubmitTransactionSetHandler.TypeOf(t).Value == set.ApprovalAuthorityType)
            .Select(t => (Amount: Math.Abs(t.Amount), t.Currency, Type: SubmitTransactionSetHandler.TypeOf(t).Value, content.Lines[t.ReserveLineId].CostType))
            .FirstOrDefault();
        var authority = verified.Authority;
        var costType = authority.Codes?.GetValueOrDefault(ClaimsAuthorityTypes.CostTypeDimension);
        if (lead.Type is null || authority.Type != lead.Type || authority.Amount is not { } amount
            || amount != ClaimMoney.Of(lead.Amount, lead.Currency) || costType != lead.CostType)
        {
            return "The approved authority differs from the set's referred transaction.";
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "SECURITY: approval of transaction set {SetId} not executed: {Reason}")]
    private static partial void LogMismatch(ILogger logger, Guid setId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Transaction set {SetId} is stale at approval: {Reason}")]
    private static partial void LogStale(ILogger logger, Guid setId, string reason);
}

/// <summary>Audit facts of the decision's application.</summary>
internal sealed class ApplyApprovalDecisionAuditor : ICommandAuditor<ApplyApprovalDecision, ApprovalOutcome>
{
    public CommandAuditFacts Describe(ApplyApprovalDecision command, Result<ApprovalOutcome>? result)
    {
        var keys = BusinessKeys.Empty.With("requestId", command.RequestId.ToString());
        if (result is not { IsSuccess: true, Value.SetId: { } setId } ok)
        {
            return new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.PLT, "ApprovalRequest", new ApprovalRequestId(command.RequestId)), BusinessKeys = keys };
        }

        return new CommandAuditFacts
        {
            ObjectRef = ClaimApprovals.SetSubject(new ClaimTransactionSetId(setId)),
            BusinessKeys = keys.With("setId", setId.ToString()),
            Changes = AuditDiff.Compute(new { status = "PENDING_APPROVAL" }, new { status = ok.Value.Outcome, decision = command.Decision }),
        };
    }
}
