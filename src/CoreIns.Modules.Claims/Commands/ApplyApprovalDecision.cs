using CoreIns.Modules.Claims.Authority;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
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

/// <summary>What the decision did to the set (IGNORED when no pending request of a pending set exists: replays, other modules' requests).</summary>
internal sealed record ApprovalOutcome(Guid? SetId, string Outcome);

/// <summary>
/// The approval path of a referred set (REQ-CLM-109, -111, -112; D-SL2-13). The checker decides each of the set's PLT
/// requests in the inbox (maker ≠ checker and the checker's authority are PLT's rules, REQ-PLT-115); CLM consumes
/// <c>ApprovalDecided</c>: a rejected, withdrawn or expired request rejects the set; an approved one is recorded, and once
/// every request of the set is approved CLM (1) verifies each with <c>plt.Approval.verifyForExecution</c> for exactly its
/// type, subject and hash, (2) checks that each decided authority dominates what CLM computes now from the set's own
/// transactions and balances (same type and cost type, amount at least the largest requirement of that bucket), and (3)
/// re-validates claim, exposures and balances; any mismatch rejects the set (APPROVAL_MISMATCH / SET_STALE) instead of
/// applying it. Idempotent and race-safe under the claim and set locks.
/// </summary>
internal sealed partial class ApplyApprovalDecisionHandler(
    ClaimsDbContext db,
    RequestContext context,
    IClock clock,
    ClaimProtection protection,
    IPlatformApprovalService approvals,
    SetLifecycle lifecycle,
    FinancialsReader reader,
    ILogger<ApplyApprovalDecisionHandler> logger) : ICommandHandler<ApplyApprovalDecision, ApprovalOutcome>
{
    private const string Pending = "PENDING";
    private const string Approved = "APPROVED";

    public async Task<Result<ApprovalOutcome>> HandleAsync(ApplyApprovalDecision command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var target = await (
                from a in db.SetApprovals.AsNoTracking()
                join s in db.TransactionSets.AsNoTracking() on a.SetId equals s.SetId
                where a.ApprovalRequestId == command.RequestId && a.LegalEntityId == legalEntity
                select new { s.SetId, s.ClaimId })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return new ApprovalOutcome(null, "IGNORED");
        }

        var claim = (await ClaimSupport.LoadAsync(db, legalEntity, target.ClaimId, cancellationToken).ConfigureAwait(false))!;
        var set = (await FinancialSupport.LockSetAsync(db, legalEntity, target.SetId, cancellationToken).ConfigureAwait(false))!;
        var rows = await db.SetApprovals.Where(a => a.SetId == set.SetId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var row = rows.Single(a => a.ApprovalRequestId == command.RequestId);
        if (set.Status != Codes.Of(SetStatus.PendingApproval) || row.Status != Pending)
        {
            return new ApprovalOutcome(set.SetId.Value, "IGNORED");
        }

        var content = await FinancialSupport.ContentAsync(db, set, cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        row.DecidedAt = now;
        row.RecordVersion++;
        if (command.Decision != Approved)
        {
            row.Status = "REJECTED";
            lifecycle.Reject(claim, content, command.Decision == "REJECTED" ? FinancialReasons.ApproverRejected : FinancialReasons.Withdrawn);
            return new ApprovalOutcome(set.SetId.Value, Codes.Of(SetStatus.Rejected));
        }

        var decision = (await approvals.GetAsync(command.RequestId.ToString("D"), cancellationToken).ConfigureAwait(false)).Decision;
        row.Status = Approved;
        row.Checker = decision is null ? "UNKNOWN" : $"{decision.Checker.Kind.ToString().ToUpperInvariant()}:{decision.Checker.Id}";
        row.CheckerUserId = decision is { Checker.Kind: Platform.Contracts.Common.Actor.KindValue.User } && Guid.TryParse(decision.Checker.Id, out var oid) && oid != Guid.Empty
            ? oid
            : null;
        if (rows.Any(a => a.Status != Approved))
        {
            return new ApprovalOutcome(set.SetId.Value, "AWAITING_APPROVALS");
        }

        if (await lifecycle.RevalidateAsync(claim, content, cancellationToken).ConfigureAwait(false) is { } stale)
        {
            LogStale(logger, set.SetId.Value, stale);
            lifecycle.Reject(claim, content, FinancialReasons.SetStale);
            return new ApprovalOutcome(set.SetId.Value, Codes.Of(SetStatus.Rejected));
        }

        if (await VerifyAsync(claim, set, content, rows, cancellationToken).ConfigureAwait(false) is { } mismatch)
        {
            LogMismatch(logger, set.SetId.Value, mismatch);
            lifecycle.Reject(claim, content, FinancialReasons.ApprovalMismatch);
            return new ApprovalOutcome(set.SetId.Value, Codes.Of(SetStatus.Rejected));
        }

        await lifecycle.ApproveAsync(claim, content, string.Join(",", rows.Select(a => a.Checker).Distinct()), row.CheckerUserId, fourEyes: true, cancellationToken)
            .ConfigureAwait(false);
        return new ApprovalOutcome(set.SetId.Value, Codes.Of(SetStatus.Approved));
    }

    /// <summary>Null when PLT's approvals together cover exactly this set and dominate every referred requirement; otherwise why not.</summary>
    private async Task<string?> VerifyAsync(ClaimRow claim, TransactionSetRow set, SetContent content, List<SetApprovalRow> rows, CancellationToken cancellationToken)
    {
        var contentHash = SetHashing.Content(set.SetId, set.ClaimId, content.Canonical());
        if (contentHash.Value != set.ContentHash)
        {
            return "The set content does not match its content hash.";
        }

        var payment = content.Payments.SingleOrDefault();
        var requirements = SetAuthority.Requirements(
            content,
            await reader.LinesAsync(claim.ClaimId, cancellationToken).ConfigureAwait(false),
            await reader.ApprovedAsync(claim.ClaimId, null, cancellationToken).ConfigureAwait(false));
        foreach (var row in rows)
        {
            // The bound subject and hash CLM computes itself for this bucket.
            var bucket = requirements.Where(r => r.Bucket == (row.AuthorityType, row.AuthorityCostType)).MaxBy(r => r.Amount);
            if (bucket is null)
            {
                return $"The set no longer needs {row.AuthorityType} {row.AuthorityCostType}.";
            }

            var (type, subject, hash) = SubmitTransactionSetHandler.SubjectOf(set, payment, bucket);
            if (row.ApprovalType != type || row.SubjectType != subject.Type || row.SubjectId != subject.Id || row.PayloadHash != hash.Value)
            {
                return "An approval's bound subject or hash does not match the set's content.";
            }

            ApprovalVerifyForExecutionResponse verified;
            try
            {
                verified = await approvals.VerifyForExecutionAsync(
                    new ApprovalVerifyForExecutionRequest { RequestId = row.ApprovalRequestId, Hash = hash, Type = type, ObjectRef = subject }, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
            {
                return $"PLT refused execution ({ex.Error.Code}).";
            }

            if (!verified.Ok)
            {
                return $"Approval request {row.ApprovalRequestId} is {verified.Status}.";
            }

            // The decided authority must dominate what CLM computes: same type and cost type, at least the largest amount.
            var authority = verified.Authority;
            if (authority.Type != bucket.Type.Value
                || authority.Codes?.GetValueOrDefault(ClaimsAuthorityTypes.CostTypeDimension) != bucket.CostType
                || authority.Amount is not { } amount || amount.Currency.Code != bucket.Currency || amount.Amount < bucket.Amount)
            {
                return $"The approved authority {authority.Type} does not cover {bucket.Type} {bucket.CostType} {SetHashing.Fixed(bucket.Amount)}.";
            }
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
