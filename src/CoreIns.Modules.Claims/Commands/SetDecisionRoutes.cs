using System.Security.Cryptography;
using System.Text;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.Commands;

/// <summary>What an HTTP decision route did to the set.</summary>
/// <param name="SetId">The set.</param>
/// <param name="Status">The set's status after the decision.</param>
/// <param name="DisbursementIds">BIL disbursements requested by the approval.</param>
internal sealed record SetDecisionOutcome(Guid SetId, string Status, IReadOnlyList<DisbursementId> DisbursementIds);

/// <summary>
/// The thin HTTP routes <c>clm.TransactionSet.approve / reject / return</c> (BO-CLM-04). They add no decision logic: each
/// pending PLT request of the set is decided with <c>plt.Approval.decide</c> (maker not equal to checker, the checker's authority
/// and the bound hash are PLT's rules, identical to deciding in the inbox), then the decision is applied through
/// <see cref="ApplyApprovalDecision"/>, the command the <c>ApprovalDecided</c> consumer runs (idempotent, so the later event
/// is a no-op). A set that is no longer pending, or whose content hash differs from the caller's, is CLM-ERR-SET-STALE.
/// </summary>
internal sealed class SetDecisionRoutes(
    ClaimsDbContext db,
    RequestContext context,
    ClaimProtection protection,
    IPlatformApprovalService approvals,
    ICommandHandler<ApplyApprovalDecision, ApprovalOutcome> apply)
{
    public Task<Result<SetDecisionOutcome>> ApproveAsync(Guid setId, string contentHash, string? comment, CancellationToken cancellationToken) =>
        DecideAsync(setId, contentHash, ApprovalDecideRequest.DecisionValue.Approve, "APPROVED", comment, cancellationToken);

    public Task<Result<SetDecisionOutcome>> RejectAsync(Guid setId, string reason, string? comment, CancellationToken cancellationToken) =>
        DecideAsync(setId, null, ApprovalDecideRequest.DecisionValue.Reject, "REJECTED", Join(reason, comment), cancellationToken);

    /// <summary>Return to the maker: the checker declines the set as it stands; the maker builds a corrected one (TODO(hardening): copy set).</summary>
    public Task<Result<SetDecisionOutcome>> ReturnAsync(Guid setId, string? reason, string? comment, CancellationToken cancellationToken) =>
        DecideAsync(setId, null, ApprovalDecideRequest.DecisionValue.Reject, "REJECTED", Join(reason ?? "RETURNED", comment), cancellationToken);

    private async Task<Result<SetDecisionOutcome>> DecideAsync(
        Guid setId, string? contentHash, ApprovalDecideRequest.DecisionValue decision, string applied, string? comment, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var id = new ClaimTransactionSetId(setId);
        var set = await db.TransactionSets.AsNoTracking().SingleOrDefaultAsync(s => s.SetId == id && s.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (set is null)
        {
            return ClaimSupport.NotFound("transaction set");
        }

        if (set.Status != Codes.Of(SetStatus.PendingApproval))
        {
            return FinancialSupport.Stale($"The set is {set.Status}; only a set pending approval can be decided.");
        }

        if (contentHash is not null && !string.Equals(contentHash, set.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            return FinancialSupport.Stale("The set content changed since the caller read it (content hash differs).");
        }

        var pending = await db.SetApprovals.AsNoTracking().Where(a => a.SetId == id && a.Status == "PENDING").OrderBy(a => a.ApprovalRequestId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return FinancialSupport.Stale("The set has no pending approval request.");
        }

        foreach (var row in pending)
        {
            try
            {
                await approvals.DecideAsync(
                    new ApprovalDecideRequest { RequestId = row.ApprovalRequestId, Decision = decision, PayloadHash = Sha256Hash.Parse(row.PayloadHash), Comment = comment },
                    new CommandOptions(KeyFor(row.ApprovalRequestId)), cancellationToken).ConfigureAwait(false);
            }
            catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
            {
                return ex.Error;
            }

            var outcome = await apply.HandleAsync(new ApplyApprovalDecision(row.ApprovalRequestId, applied), cancellationToken).ConfigureAwait(false);
            if (!outcome.IsSuccess)
            {
                return outcome.Error!;
            }

            if (decision == ApprovalDecideRequest.DecisionValue.Reject)
            {
                break;
            }
        }

        var after = await db.TransactionSets.AsNoTracking().SingleAsync(s => s.SetId == id, cancellationToken).ConfigureAwait(false);
        var disbursements = await db.ClaimPayments.AsNoTracking().Where(p => p.SetId == id && p.DisbursementId != null)
            .Select(p => p.DisbursementId!.Value).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new SetDecisionOutcome(setId, after.Status, disbursements);
    }

    private static string Join(string reason, string? comment) => string.IsNullOrWhiteSpace(comment) ? reason : $"{reason}: {comment}";

    /// <summary>A stable key per (HTTP key, request) so a replay of the route replays each PLT decision instead of repeating it.</summary>
    private IdempotencyKey KeyFor(Guid requestId) =>
        context.IdempotencyKey is not { } key
            ? IdempotencyKey.New()
            : IdempotencyKey.From(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{key.Value:D}|{requestId:D}")).AsSpan(0, 16)));
}
